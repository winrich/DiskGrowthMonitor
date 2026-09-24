using System.Collections.Concurrent;
using System.Diagnostics;
using DiskGrowthMonitor.Models;
using DiskGrowthMonitor.Compat;
using DiskGrowthMonitor.Native;
using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Services;

/// <summary>扫描进度快照，用于控制台进度输出（多线程下为聚合计数）。</summary>
public sealed class ScanProgress
{
    public long Dirs;
    public long Files;
    public long Bytes;
    public string CurrentPath = string.Empty;
    /// <summary>本条进度所属的扫描根（用于分根显示进度条）。</summary>
    public string RootKey = string.Empty;
    /// <summary>该根已记录的目录数（分根计数）。</summary>
    public long RootDirs;
    /// <summary>该根已扫过的文件字节数（叶级累加，容量估算口径的进度分子）。</summary>
    public long RootBytes;
}

/// <summary>
/// 原始目录项（单次系统调用结果）。
///
/// <para><see cref="FileIdLow"/>/<see cref="FileIdHigh"/> 是阶段 B 新增的：NT 枚举档
/// （FileIdExtd / FileIdBoth）会从目录项里直出文件标识，硬链接去重因此不再需要为每个文件
/// 开一次句柄。Win32 档（FindFirstFileExW）拿不到标识，两个字段恒为 0 —— 调用方据此
/// 回退到原来的「逐文件开句柄」路径，语义不退化。</para>
///
/// <para><see cref="Attributes"/> 保留一份原始属性位，便于后续按需判断（当前只用于诊断）。</para>
/// </summary>
internal readonly record struct RawEntry(
    string? Name,
    string? FullPath,
    long Length,
    bool IsDirectory,
    bool IsReparse,
    ulong FileIdLow,
    ulong FileIdHigh,
    uint Attributes);

/// <summary>单目录子树统计结果。Dirs 为后代目录数（不含自身）。</summary>
public readonly record struct DirStat(long Size, long Files, long Dirs);

/// <summary>
/// USN 增量复用策略：路径不在脏路径集合内 ⇒ 其整个子树自上次扫描以来没有任何变更，
/// 可整段复用上一轮快照。传 null 表示该根不允许复用（全量扫描）。
/// </summary>
public sealed class ReusePolicy
{
    private readonly HashSet<string> _dirty;
    private readonly string _rootSelf;     // 根自身（无尾反斜杠），如 "C:" / "D:\data\x"
    private readonly string _rootPrefix;   // 根前缀（带尾反斜杠），用于判定「其后代」

    /// <param name="dirtyAncestorPaths">变更路径及其全部祖先前缀（见 <c>UsnJournalService.MarkDirty</c>）。</param>
    /// <param name="rootPath">该扫描根的路径，用于判定「整根能否复用」。</param>
    public ReusePolicy(HashSet<string> dirtyAncestorPaths, string rootPath)
    {
        _dirty = dirtyAncestorPaths;
        _rootPrefix = rootPath.EndsWith("\\", StringComparison.Ordinal) ? rootPath : rootPath + "\\";
        _rootSelf = _rootPrefix.TrimEnd('\\');

        // 🔴 整根复用绝不能直接用 CanReuse(rootPath)（精确比较）：
        // 脏集里装的是按分隔符切分出的祖先串，盘符根切出来是 "C"（无尾反斜杠），
        // 而扫描根的 Path 是 "C:\"（见 ScanRoot.FromDrive：盘符根必须带尾反斜杠，
        // 因为 "C:" 在 Win32 语义里是「当前目录可变的相对盘符」）⇒ 两者永不相等
        // ⇒ CanReuse("C:\") 恒为 true ⇒ 整根复用无条件命中 ⇒ **整卷变更被静默丢弃、
        // 报告永远 0 B**（实测：日志同时打印「检测到 44 处变更，仅重扫 32 个目录子树」
        // 与「增量复用：192,126 个目录」，而 192,126 恰为全盘行数 ⇒ 一个目录都没枚举）。
        // 故改成前缀语义：根自身或其下**任一**路径变脏 ⇒ 该根不得整根复用。
        CanReuseRoot = !HasDirtyUnderRoot();
    }

    /// <summary>该扫描根能否整根复用（自身及其下所有后代都无变更）。构造时算一次，避免热路径重复扫描脏集。</summary>
    public bool CanReuseRoot { get; }

    /// <summary>单目录子树能否复用：脏集是精确路径集合，直接查表（热路径，每个子目录一次）。</summary>
    public bool CanReuse(string dirPath) => !_dirty.Contains(dirPath);

    private bool HasDirtyUnderRoot()
    {
        foreach (var p in _dirty)
        {
            if (p.Equals(_rootSelf, StringComparison.OrdinalIgnoreCase)) return true;              // 根自身
            if (p.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase)) return true;        // 根的某个后代
        }
        return false;
    }
}

/// <summary>
/// 全盘递归扫描器（多根并行 + USN 增量剪枝）。
///
/// 核心算法：**单遍递归 + 自底向上增量累加**。
/// 对每个目录只做一次枚举，遇到子目录递归取回其子树统计并相加，
/// 返回后再写入本目录记录（后序）。这样每个文件系统条目恰好被访问一次，
/// 避免了「逐目录各自求和」导致的 O(深度 × 文件数) 重复遍历。
///
/// 并行模型：每个扫描根一个 Task（磁盘天然独立），共享进度/统计用 Interlocked 聚合，
/// 所有数据库访问经 StageWriter 串行化；硬链接去重集合为 ConcurrentDictionary。
///
/// 增量剪枝：进入子目录前先问复用策略——路径不在脏集合内时，
/// 把该子树的上一轮快照整段复制进暂存表（CopyPrevSubtree），完全不递归。
/// </summary>
public sealed class DiskScanner
{
    /// <summary>每累积该数量的目录记录就批量写库，控制内存峰值。</summary>
    private const int BatchSize = 20000;

    /// <summary>递归深度上限，防御极端长路径造成的栈溢出。</summary>
    private const int MaxDepth = 1024;

    private readonly ScanSettings _settings;
    private readonly SkipListService _skip;
    private readonly StageWriter _stage;
    private readonly Action<ScanProgress>? _progress;
    private readonly ScanStatistics _stats = new();

    /// <summary>
    /// 已出现过的文件标识，保证同一物理数据只计一次。
    /// 集合本身是「按卷隔离 + 分片加锁」的（见 <see cref="FileIdSet"/> 类注释）：
    /// 卷隔离是数据质量要求（跨卷 MFT 索引会碰撞导致静默漏算），分片是并发性能要求。
    /// </summary>
    private readonly FileIdSet _fileIds = new();

    /// <summary>枚举引擎选择器：按卷实测并缓存该用哪一档（60 → 37 → Win32），探测结果全局复用。</summary>
    private readonly EnumEngineSelector _engines;

    // ---- 多线程聚合计数 ----
    private long _dirsRecorded;
    private long _reusedDirs;
    private long _globalBytes;
    private long _globalFiles;
    /// <summary>通过「目录项直出 FileId」完成去重的文件数（阶段 B 新路径的覆盖量，用于诊断）。</summary>
    private long _dedupedViaEntryFileId;

    /// <summary>各根最近一次进度上报时刻（按根独立节流：多盘并行时互不抢占时间片，
    /// 每个根都能稳定刷新；同一根内的多线程仍靠 CAS 保证只有一个线程上报）。</summary>
    private readonly ConcurrentDictionary<string, long> _lastProgressTicks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>分根目录计数（进度条按根显示百分比用）。</summary>
    private readonly ConcurrentDictionary<string, long> _rootDirCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>分根已扫文件字节数（容量估算口径的进度分子；叶级累加，不重复计入祖先）。</summary>
    private readonly ConcurrentDictionary<string, long> _rootBytes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>各根的枚举引擎解析说明（降级原因等），扫描结束后一并输出。</summary>
    private readonly List<string> _engineNotes = new();

    private readonly Stopwatch _totalTimer = Stopwatch.StartNew();

    public DiskScanner(
        ScanSettings settings,
        SkipListService skip,
        StageWriter stage,
        Action<ScanProgress>? progress = null)
    {
        _settings = settings;
        _skip = skip;
        _stage = stage;
        _progress = progress;
        _engines = new EnumEngineSelector(settings.EnumEngine, EnumEngineSelector.DefaultBufferSize);
    }

    public ScanStatistics Statistics => _stats;

    /// <summary>
    /// 扫描指定的全部根（按并行度设置同时扫描多个根）。
    /// policies 为「rootKey → 复用策略」，缺省表示该根不允许复用。
    /// </summary>
    public ScanStatistics Scan(IReadOnlyList<ScanRoot> roots, IReadOnlyDictionary<string, ReusePolicy>? policies)
    {
        policies ??= new Dictionary<string, ReusePolicy>(StringComparer.OrdinalIgnoreCase);

        // 预建各根统计，避免并行期间写同一字典
        foreach (var root in roots)
            _stats.Roots[root.Key] = new RootStat();

        // 枚举引擎按卷**实测**（阶段 B）：串行做，每个卷只探测一次，之后走缓存。
        // 放在并行之前是为了让探测日志有序、也避免多个根同时探测时在锁内排队。
        foreach (var root in roots)
        {
            _engines.Resolve(root.Path, out var engineNote);
            if (!string.IsNullOrEmpty(engineNote)) _engineNotes.Add(root.Key + "：" + engineNote);
        }

        // 自动并行度的上限取**物理核**而非逻辑核：空转基线（_optdb/enumbench）实测，
        // 单卷枚举的并行收益在物理核数处饱和（8 物理核机器上 t=8 即达上限，
        // t=16 与 t=8 相差 <1.5s），超卖到逻辑核只增加调度与争用，不产生收益。
        // 用户显式指定的 --threads 仍允许用到逻辑核数（人工决策优先于自动策略）。
        int autoCores = SystemNative.GetPhysicalCoreCount();
        if (autoCores <= 0) autoCores = Environment.ProcessorCount;
        int maxThreads = _settings.Threads > 0
            ? Math.Min(_settings.Threads, Environment.ProcessorCount)
            : Math.Min(roots.Count, autoCores);
        maxThreads = Math.Max(1, Math.Min(maxThreads, roots.Count));
        _stats.UsedThreads = maxThreads;

        using var gate = new SemaphoreSlim(maxThreads);
        var tasks = new Task[roots.Count];
        for (int i = 0; i < roots.Count; i++)
        {
            var root = roots[i];
            policies.TryGetValue(root.Key, out var policy);
            tasks[i] = Task.Run(() =>
            {
                gate.Wait();
                try { ScanRoot(root, policy); }
                finally { gate.Release(); }
            });
        }
        // 无论成败都要 Flush：等后台写线程清空队列（写线程不收尾会一直挂着到进程退出）
        try
        {
            Task.WaitAll(tasks);
        }
        finally
        {
            _stage.Flush();
        }

        // 后台写线程捕获到的批量插入失败：带回给调用方，由 Program 决定中止提交。
        // 此时内存里的统计仍是本次扫描的真实值，但暂存数据可能残缺 —— 不能进入对比。
        if (_stage.Error != null)
            _stats.WriteError = Lang.T("扫描结果写库失败：", "Failed to write scan results to the database: ") + _stage.Error.Message;

        _totalTimer.Stop();
        _stats.ElapsedMs = _totalTimer.ElapsedMilliseconds;

        // 收尾补报：上面的按根节流（300ms）会漏掉最后一帧，进度块会停在"进度 100% 但括号里的
        // 数字永远少一点"（实测全盘 191,785 vs 192,300）。这里用业务侧的真实终值补报一次，
        // 让最终画面与紧随其后的「扫描完成：」行一致。
        // 注意是**用真值覆盖旧值**，不是把已扫数与分母取 Max 凑满 —— 后者会把"整根复用只枚举了
        // 1 个目录"写成"已扫 192,037"，正好抹掉最该被看见的事实。
        foreach (var root in roots)
            ReportProgress(root.Key, string.Empty, force: true);

        _stats.ReusedDirs = Interlocked.Read(ref _reusedDirs);
        _stats.EnumEngineSummary = _engines.DescribeAll();
        foreach (var note in _engineNotes) _stats.EnumEngineNotes.Add(note);
        _stats.DedupedViaEntryFileId = Interlocked.Read(ref _dedupedViaEntryFileId);
        _stats.DedupIdentifierCount = _fileIds.Count;

        var (defaultExcludedCnt, userExcludedCnt, deniedCnt, reparseCnt, errorCnt) = _skip.CountPending();
        _stats.DefaultExcludedCount = defaultExcludedCnt;
        _stats.ExcludedCount = defaultExcludedCnt + userExcludedCnt;    // 合计口径（报告沿用）
        _stats.AccessDeniedCount = deniedCnt;
        _stats.ReparseCount = reparseCnt;
        _stats.ErrorCount = errorCnt;
        _stats.KnowledgeRescuedCount = _skip.RescuedCount;
        return _stats;
    }

    /// <summary>扫描单个根。返回 true = 成功完成（含整根复用）。</summary>
    private bool ScanRoot(ScanRoot root, ReusePolicy? policy)
    {
        if (!Directory.Exists(root.Path))
        {
            _skip.Record(root.Path, root.Key, SkipReason.IoError,
                Lang.T("扫描根目录不存在或不可访问", "The scan root does not exist or is inaccessible"));
            _stats.FailedRoots.Add(root.Key);
            return false;
        }

        // 卷不支持硬链接（如 FAT32/exFAT）时整体关闭去重，省掉逐文件句柄开销
        bool dedup = _settings.Dedup && FileSystemNative.VolumeSupportsHardLinks(root.Path);

        // 本根的扫描上下文：把「这个根该怎么扫」一次算清，递归时不再传一长串参数
        var ctx = new RootContext
        {
            RootKey = root.Key,
            Policy = policy,
            Dedup = dedup,
            Engine = _engines.Resolve(root.Path, out _)
        };

        // 「目录项直出 FileId」必须先拿到**卷序列号**当去重集合的隔离键。
        // 拿不到（罕见：卷信息读取被拒）就退回逐文件句柄档 —— 那档的标识自带卷序列号，语义不退化。
        ctx.UseEntryFileId = dedup
                             && EnumEngineSelector.UsesEntryFileId(ctx.Engine)
                             && FileSystemNative.TryGetVolumeSerial(root.Path, out ctx.VolumeSerial);

        // 只有「逐文件开句柄」档才需要文件完整路径。目录项自带 FileId 时不再拼接路径，
        // 省下每个文件一次字符串分配（百万级调用很可观）。
        ctx.NeedFilePath = dedup && !ctx.UseEntryFileId;

        var rs = _stats.Roots[root.Key];

        // 整根复用：自上次扫描以来无任何变更时，直接沿用上一轮快照，零遍历
        // ⚠ 门禁必须是 CanReuseRoot（前缀语义），不能用 CanReuse(root.Path)（精确比较）：
        // 盘符根 "C:\" 永远不会出现在脏集里（脏集切出来的是 "C"）⇒ 精确比较恒真 ⇒ 整卷变更被吞
        if (ctx.Policy != null && ctx.Policy.CanReuseRoot &&
            _stage.TryReuseSubtree(root.Key, root.Path, 0, out var reused))
        {
            AddRootTotals(reused);
            rs.SizeBytes = reused.Size;
            rs.FileCount = reused.Files;
            rs.DirCount = reused.Dirs;

            // 复用同样要计入进度与「复用」统计 —— 子树分支（见 ScanDirectory）就是这么做的，
            // 这里漏掉会带来两个错：① 控制台不打印「增量复用：N 个目录…」，且 BuildScanModeNote
            // 还会把报告里的扫描方式写成「全量扫描」（明明零遍历）；② 进度条的目录数停在 0。
            // 口径与子树分支一致：reusedRows = 该子树的目录数（含根自身）。
            long reusedRows = reused.Dirs + 1;
            Interlocked.Add(ref _reusedDirs, reusedRows);
            Interlocked.Add(ref _dirsRecorded, reusedRows);
            Interlocked.Add(ref _globalBytes, reused.Size);
            Interlocked.Add(ref _globalFiles, reused.Files);
            _rootDirCounts.AddOrUpdate(root.Key, reusedRows, (_, v) => v + reusedRows);
            _rootBytes.AddOrUpdate(root.Key, reused.Size, (_, v) => v + reused.Size);
            ReportProgress(root.Key, root.Path);
            return true;
        }

        // 根级缓冲：整根递归共用，成功结束时统一落库（失败则整体丢弃，避免部分数据污染对比）
        bool completed = false;
        try
        {
            var stat = ScanDirectory(ctx, root.Path, 0);
            AddRootTotals(stat);
            rs.SizeBytes = stat.Size;
            rs.FileCount = stat.Files;
            rs.DirCount = stat.Dirs;
            completed = true;
            return true;
        }
        catch (Exception ex)
        {
            _skip.Record(root.Path, root.Key, SkipReason.IoError,
                Lang.F("扫描根目录时异常：{0}", "Exception while scanning the root: {0}", ex.Message));
            _stats.FailedRoots.Add(root.Key);
            return false;
        }
        finally
        {
            // 关键：ScanDirectory 只在 buffer 满 BatchSize 时落库，
            // 根扫描成功结束后必须把残余不足一批的记录写入，否则小目录树全部丢失
            if (completed && ctx.Buffer.Count > 0)
                _stage.InsertBatch(ctx.Buffer);
        }
    }

    private void AddRootTotals(DirStat stat)
    {
        Interlocked.Add(ref _stats.TotalBytes, stat.Size);
        Interlocked.Add(ref _stats.TotalFiles, stat.Files);
        // 总目录数 = 后代目录数 + 根自身
        Interlocked.Add(ref _stats.TotalDirs, stat.Dirs + 1);
    }

    /// <summary>
    /// 单根扫描上下文：把「这个根该怎么扫」的决策一次算清并随身携带。
    /// 递归时只传它 + 路径 + 深度，避免参数列表膨胀到六七个。
    /// </summary>
    private sealed class RootContext
    {
        public string RootKey = string.Empty;

        /// <summary>本根是否启用硬链接去重（卷不支持硬链接时为 false）。</summary>
        public bool Dedup;

        /// <summary>本根实际使用的枚举引擎（已按卷实测解析）。</summary>
        public EnumEngineKind Engine;

        /// <summary>本根所在卷的序列号（去重集合的隔离键）；0 = 未知，此时不能走目录项档。</summary>
        public uint VolumeSerial;

        /// <summary>true = 用目录项自带的 FileId 去重（零句柄）；false = 逐文件开句柄（旧口径）。</summary>
        public bool UseEntryFileId;

        /// <summary>是否需要为文件构造完整路径（仅「逐文件开句柄」档才需要）。</summary>
        public bool NeedFilePath;

        /// <summary>USN 增量复用策略；null = 不允许复用。</summary>
        public ReusePolicy? Policy;

        /// <summary>
        /// 本根的目录记录缓冲，成功结束后由 <see cref="ScanRoot"/> 统一落库。
        /// 批次满时整块交给后台写线程并换上新缓冲（缓冲交换，不能原地清空 —— 见 ScanDirectory）。
        /// </summary>
        public List<DirSnapshot> Buffer = new(Math.Min(BatchSize, 4096));
    }

    /// <summary>
    /// 递归扫描单个目录，返回其子树累计统计。后序写库。
    /// </summary>
    private DirStat ScanDirectory(RootContext ctx, string dirPath, int depth)
    {
        long size = 0, files = 0, dirs = 0;
        string rootKey = ctx.RootKey;

        if (depth > MaxDepth)
        {
            _skip.Record(dirPath, rootKey, SkipReason.IoError,
                Lang.F("目录层级超过 {0} 层，为保证稳定性未继续深入",
                       "Directory depth exceeded {0} levels; not descending further, for stability", MaxDepth));
            return new DirStat(0, 0, 0);
        }

        try
        {
            foreach (var entry in EnumerateRaw(dirPath, ctx))
            {
                if (entry.IsDirectory)
                {
                    // 目录重解析点（junction / 符号链接）：默认不跟随，避免同一份数据被重复计数
                    if (entry.IsReparse && !_skip.FollowReparse)
                    {
                        _skip.Record(entry.FullPath!, rootKey, SkipReason.ReparsePoint,
                            Lang.T("目录符号链接或 junction，默认不跟随（可用 --follow-reparse 开启）",
                                   "Directory symlink or junction; not followed by default (enable with --follow-reparse)"));
                        continue;
                    }

                    if (_skip.ShouldSkip(entry.FullPath!, entry.Name!, depth + 1, out var reason, out var detail))
                    {
                        _skip.Record(entry.FullPath!, rootKey, reason, detail);
                        continue;
                    }

                    // 增量复用：该子树内无任何 USN 变更 → 整段沿用上一轮快照，不递归
                    if (ctx.Policy != null && ctx.Policy.CanReuse(entry.FullPath!) &&
                        _stage.TryReuseSubtree(rootKey, entry.FullPath!, depth + 1, out var st))
                    {
                        size += st.Size;
                        files += st.Files;
                        dirs += st.Dirs + 1;
                        long reusedRows = st.Dirs + 1;
                        Interlocked.Add(ref _reusedDirs, reusedRows);
                        Interlocked.Add(ref _dirsRecorded, reusedRows);
                        _rootDirCounts.AddOrUpdate(rootKey, reusedRows, (_, v) => v + reusedRows);
                        Interlocked.Add(ref _globalBytes, st.Size);
                        Interlocked.Add(ref _globalFiles, st.Files);
                        _rootBytes.AddOrUpdate(rootKey, st.Size, (_, v) => v + st.Size);
                        ReportProgress(rootKey, dirPath);
                        continue;
                    }

                    var child = ScanDirectory(ctx, entry.FullPath!, depth + 1);
                    size += child.Size;
                    files += child.Files;
                    dirs += child.Dirs + 1;
                }
                else
                {
                    // 硬链接去重。两种口径的判定结果**完全一致**（PoC 在三棵子树上逐项实证），
                    // 区别只在「怎么拿到文件标识」：
                    //   · 目录项档：FileId 随目录项一起返回 ⇒ 零额外系统调用（Stage B 的收益来源）
                    //   · Win32 档：只能为每个非空文件开一次句柄取标识（改动前的行为）
                    // 「仅非空文件参与去重」的过滤保留不动：0 字节文件直接计入，
                    // 省一次集合操作；实测两棵子树上 0 字节硬链接均为 0 个 ⇒ 过滤与否结果零差异。
                    if (ctx.Dedup && entry.Length > 0 && !IsFirstLink(ctx, entry))
                    {
                        Interlocked.Increment(ref _stats.DedupedFileCount);
                        Interlocked.Add(ref _stats.DedupedBytes, entry.Length);
                        continue;
                    }

                    size += entry.Length;
                    files++;
                    // 进度分子在「叶级文件」处一次累加：若改用后序的子树值累加，
                    // 每个祖先都会把同一份字节再加一遍，数字会虚高数倍
                    Interlocked.Add(ref _globalBytes, entry.Length);
                    Interlocked.Increment(ref _globalFiles);
                    _rootBytes.AddOrUpdate(rootKey, entry.Length, (_, v) => v + entry.Length);
                }

                ReportProgress(rootKey, dirPath);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 权限不足：记录后写入跳过清单，下次扫描直接跳过，不再重复尝试
            _skip.Record(dirPath, rootKey, SkipReason.AccessDenied,
                Lang.T("访问被拒绝（权限不足）", "Access denied (insufficient privileges)"));
        }
        catch (Exception ex) when (ex is IOException
                                      or System.Security.SecurityException
                                      or PathTooLongException
                                      or NotSupportedException)
        {
            _skip.Record(dirPath, rootKey, SkipReason.IoError, ex.GetType().Name + "：" + ex.Message);
        }

        // 后序写库：此时子树统计已完整
        ctx.Buffer.Add(new DirSnapshot(dirPath, rootKey, depth, size, files, dirs));
        Interlocked.Increment(ref _dirsRecorded);
        _rootDirCounts.AddOrUpdate(rootKey, 1, (_, v) => v + 1);
        // 注意：此处不要再累加 _globalBytes / _globalFiles（size/files 是子树值，
        // 会把同一份数据在每个祖先处重复计入），进度分子已在叶级累加完毕。
        if (ctx.Buffer.Count >= BatchSize)
        {
            // 缓冲交换而非原地清空：InsertBatch 现在是异步入队，后台线程会在稍后枚举
            // 这份列表 —— 若传活引用再 Clear()，会撞上「集合已修改」竞争
            // （实测撞过一次，被 WriteError 安全网拦下未入库）。
            var full = ctx.Buffer;
            ctx.Buffer = new List<DirSnapshot>(BatchSize);
            _stage.InsertBatch(full);
        }

        return new DirStat(size, files, dirs);
    }

    /// <summary>
    /// 判断该文件是否应计入（true = 计入）。仅在「启用去重 + 文件非空」时被调用。
    ///
    /// 目录项档：直接用条目里带的 FileId，按卷隔离登记 —— 零额外系统调用。
    /// Win32 档：退回逐文件开句柄（见 <see cref="IsFirstLinkOfFile"/>），语义与改动前完全一致。
    /// </summary>
    private bool IsFirstLink(RootContext ctx, RawEntry entry)
    {
        if (ctx.UseEntryFileId)
        {
            Interlocked.Increment(ref _dedupedViaEntryFileId);
            return _fileIds.TryAdd(ctx.VolumeSerial, entry.FileIdLow, entry.FileIdHigh);
        }

        return IsFirstLinkOfFile(entry.FullPath!);
    }

    /// <summary>
    /// Win32 档的去重判定（改动前的口径，作为降级与回退通道保留）：
    /// 仅有链接数大于 1 的文件需要查询唯一标识；标识首次出现则计入，之后一律跳过。
    /// 标识含卷序号，多根并行时不同卷互不干扰；同卷多根亦共享同一集合，保证全局去重。
    /// </summary>
    private bool IsFirstLinkOfFile(string filePath)
    {
        if (!FileSystemNative.TryGetFileId(filePath, out uint volume, out ulong index, out uint links))
            return true; // 读不到标识时按计入处理，宁多算不漏算

        if (links <= 1) return true; // 无其他硬链接

        // Win32 档拿不到 128 位标识，高位恒传 0（BY_HANDLE_FILE_INFORMATION 只给 64 位）
        return _fileIds.TryAdd(volume, index, 0);
    }

    /// <summary>
    /// 按本根的引擎档枚举单个目录的直接子项。
    ///
    /// 三档的**结果口径完全一致**（PoC 逐项实证），区别只在拿到文件标识的方式：
    ///   · extd / both —— NtQueryDirectoryFile 目录项直出 FileId
    ///   · win32 —— FindFirstFileExW，无标识（去重时再逐文件开句柄）
    /// 语义与 BCL 版逐条对齐：不跳过隐藏/系统文件、目录不可访问时抛
    /// UnauthorizedAccessException、不递归、不含 "." 与 ".."；并自动加 \\?\ 前缀支持超长路径。
    /// </summary>
    private static IEnumerable<RawEntry> EnumerateRaw(string dir, RootContext ctx)
    {
        switch (ctx.Engine)
        {
            case EnumEngineKind.Extd:
                return NtDirectoryEnumerator.Enumerate(dir, ctx.NeedFilePath,
                    NtDirectoryEnumerator.FileIdExtdDirectoryInformation, EnumEngineSelector.DefaultBufferSize);

            case EnumEngineKind.Both:
                return NtDirectoryEnumerator.Enumerate(dir, ctx.NeedFilePath,
                    NtDirectoryEnumerator.FileIdBothDirectoryInformation, EnumEngineSelector.DefaultBufferSize);

            default:
                return DirectoryEnumerator.Enumerate(dir, ctx.NeedFilePath);
        }
    }

    /// <summary>
    /// 按根节流的进度上报：每个根独立 300ms 时间片，同一根内 CAS 抢占确保单线程上报。
    /// force = true 时**忽略时间片与 CAS 直接上报**，专供扫描收尾补报终值使用：
    /// 节流必然会漏掉最后一帧（实测 19.8s 全盘扫描收尾时进度块停在 191,785，而真实为 192,300），
    /// 收尾补报一次真实值即可让最终画面与「扫描完成：」行一致。
    /// </summary>
    private void ReportProgress(string rootKey, string currentDir, bool force = false)
    {
        if (_progress == null) return;

        long now = PlatformCompat.TickCount64();
        long last = _lastProgressTicks.GetOrAdd(rootKey, 0L);
        if (!force)
        {
            if (now - last < 300) return;
            if (!_lastProgressTicks.TryUpdate(rootKey, now, last)) return;   // 已有同根线程抢先上报
        }
        else
        {
            _lastProgressTicks[rootKey] = now;
        }

        _progress(new ScanProgress
        {
            Dirs = Interlocked.Read(ref _dirsRecorded),
            Files = Interlocked.Read(ref _globalFiles),
            Bytes = Interlocked.Read(ref _globalBytes),
            CurrentPath = currentDir,
            RootKey = rootKey,
            RootDirs = _rootDirCounts.TryGetValue(rootKey, out long rd) ? rd : 0,
            RootBytes = _rootBytes.TryGetValue(rootKey, out long rb) ? rb : 0
        });
    }
}
