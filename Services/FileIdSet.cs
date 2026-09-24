namespace DiskGrowthMonitor.Services;

/// <summary>
/// 硬链接去重用的文件标识集合（阶段 B）。
///
/// 设计取舍按「数据质量 &gt; 性能 &gt; 系统资源开销」排序：
///
/// ① **数据质量（不可让步）**：必须**按卷隔离**。目录项里只有 FileId，**不带卷序列号**，
///    而各卷的 MFT 记录号是各自独立的 —— 每个卷的根目录都是记录 5、$MFT 都是记录 0。
///    若用一个全局 HashSet&lt;ulong&gt; 收纳所有卷，跨卷索引会互相碰撞，
///    表现为**静默漏算**（把 B 卷的文件误判成 A 卷已计过），比多算更难发现。
///    这里用「卷序列号 → 子集合」分组，而不是用盘符字母：**同一卷可以挂多个盘符/挂载点**，
///    按盘符分组会把同一卷拆成两组从而漏去重；卷序列号则天然把同一物理卷合并。
///
/// ② **性能**：每个卷内再拆 64 个分片、每分片一把独立锁。插入时按 FileId 低位选分片，
///    不同分片之间零争用 —— 阶段 C 引入根内并行后，同一卷会有多个 worker 同时插入，
///    单集合 + 单锁会直接成为瓶颈。单线程下无竞争锁的开销仅几十纳秒，不劣于无锁结构。
///
/// ③ **系统资源**：分片让 HashSet 的桶数组按需增长，不会因为「预估总规模」一次性预留过多内存；
///    空分片（每个约 100 字节）合计开销可忽略。
///
/// 元素用 <c>(Low, High)</c> 128 位键而不再压缩成 64 位：NTFS 上高位实测恒为 0，
/// 但 ReFS 等文件系统会给非零高位，压成 64 位会引入真实碰撞（数据质量）。代价约每元素 8 字节，
/// 在「数据质量优先」下不可省。
/// </summary>
internal sealed class FileIdSet
{
    private const int ShardBits = 6;                        // 64 个分片
    private const int ShardCount = 1 << ShardBits;
    private const int ShardMask = ShardCount - 1;

    /// <summary>一个卷上的全部文件标识。</summary>
    private sealed class VolumeSet
    {
        private readonly HashSet<(ulong Low, ulong High)>[] _shards;
        private readonly object[] _locks;
        private long _count;

        public VolumeSet()
        {
            _shards = new HashSet<(ulong Low, ulong High)>[ShardCount];
            _locks = new object[ShardCount];
            for (int i = 0; i < ShardCount; i++)
            {
                // ⚠ 不能用带容量的构造：HashSet<T>(int capacity) 是 .NET Framework 4.7.2
                // 才有的重载，net45 下编译期即报错（这里是实测踩到的）。
                // 分片数足够多（64），每片自然增长的开销可以接受。
                _shards[i] = new HashSet<(ulong Low, ulong High)>();
                _locks[i] = new object();
            }
        }

        /// <summary>首次出现返回 true（应计入）；已出现过返回 false（应跳过）。</summary>
        public bool TryAdd(ulong low, ulong high)
        {
            // 用低位选分片：NTFS 的 MFT 记录号递增加粗，分布天然均匀。
            // 不能用 GetHashCode 之外的抖动，否则同一条目在不同调用里会落到不同分片。
            int shard = (int)low & ShardMask;
            lock (_locks[shard])
            {
                if (!_shards[shard].Add((low, high))) return false;
                _count++;
                return true;
            }
        }

        public long Count => Interlocked.Read(ref _count);
    }

    // 卷级映射用 ConcurrentDictionary：TryAdd 每次都要经 GetVolumeSet 取本卷子集合，
    // 旧实现这里有一把全局锁 —— 单线程下无感，但所有卷、所有 worker 的每次插入都要排队过它，
    // 是唯一一处「全集合一把锁」的串行点（空转基线在并行档实测时定位到的）。
    // GetOrAdd 的工厂在竞争时可能被调用多次并丢弃多余实例：VolumeSet 构造只有 64 次小分配，
    // 且只有「新卷首次插入」才会发生，代价可忽略（不引入 Lazy 免得每次插入都多一层间接）。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, VolumeSet> _volumes
        = new System.Collections.Concurrent.ConcurrentDictionary<uint, VolumeSet>();
    private long _total;

    /// <summary>
    /// 登记一个文件标识。<paramref name="volumeSerial"/> 为该文件所在的**卷序列号**
    /// （由 GetVolumeInformationW 取得，同一物理卷的不同盘符会得到相同值）。
    /// </summary>
    /// <returns>true = 该标识首次出现，应计入；false = 已出现过，应跳过。</returns>
    public bool TryAdd(uint volumeSerial, ulong low, ulong high)
    {
        VolumeSet set = GetVolumeSet(volumeSerial);
        if (!set.TryAdd(low, high)) return false;
        Interlocked.Increment(ref _total);
        return true;
    }

    private VolumeSet GetVolumeSet(uint volumeSerial)
        => _volumes.GetOrAdd(volumeSerial, _ => new VolumeSet());

    /// <summary>已登记的标识总数（跨全部卷）。</summary>
    public long Count => Interlocked.Read(ref _total);

    /// <summary>已登记的卷数（用于诊断输出）。</summary>
    public int VolumeCount => _volumes.Count;
}
