namespace DiskGrowthMonitor.Models;

/// <summary>
/// 「扫描统计 vs 卷已用」的口径对照（每个被整卷扫描的卷一行）。
///
/// 为什么要专门给出这张表（2026-09-24 用户反馈）：资源管理器显示 C 盘已用 293 GB，
/// 程序只报 285 GiB，看起来像「漏扫了 8 GB」。实测证明这是两个口径 ——
///   程序口径 = <b>去重后文件逻辑大小之和</b>（逐目录累计）；
///   卷口径   = <b>卷已分配的簇数</b>（资源管理器 / <c>GetDiskFreeSpaceExW</c>）。
/// 后者天然包含不属任何目录的部分：NTFS 元数据与 MFT 保留区（本机实测保留区 3.0 GiB、
/// MFT 有效数据 2.1 GiB，合计已超差额的大半），因此前者必然更小。
/// 这个差额过去完全不出现在输出里，用户只能靠猜 —— 现在明确给出对照。
///
/// <b>只对「真实整卷扫描根」计算</b>：子目录根的统计值与该卷已用量不可比；
/// 位于 junction / 符号链接之后的根（路径盘符 ≠ 真实卷，见 Program.IsOnDriveRootVolume）
/// 同样必须排除，否则会拿 A 卷的统计值去减 B 卷的已用量。
/// </summary>
public sealed class VolumeGap
{
    /// <summary>盘符字母（大写，不含冒号）。</summary>
    public required char Letter { get; init; }

    /// <summary>卷容量（字节）。</summary>
    public long CapacityBytes { get; init; }

    /// <summary>卷已用（容量 − 可用空间，簇口径）。</summary>
    public long UsedBytes { get; init; }

    /// <summary>卷可用空间（字节）。</summary>
    public long FreeBytes { get; init; }

    /// <summary>本次扫描统计（去重后的文件逻辑大小之和）。</summary>
    public long ScannedBytes { get; init; }

    /// <summary>该卷上参与对照的整卷扫描根数量（同卷多根时统计值已累加）。</summary>
    public int RootCount { get; init; }

    /// <summary>
    /// 卷已用 − 本次统计。正数 = 统计小于卷已用（正常：元数据/保留区/被排除目录）；
    /// 负数 = 统计大于卷已用（典型是关闭硬链接去重后，跨目录的同一份物理数据被重复计入）。
    /// </summary>
    public long GapBytes => UsedBytes - ScannedBytes;

    /// <summary>差额占卷已用的百分比（卷已用为 0 时返回 0）。</summary>
    public double GapPercent => UsedBytes > 0 ? GapBytes * 100.0 / UsedBytes : 0.0;

    /// <summary>显示用的卷标签，如 "C:"。</summary>
    public string DriveLabel => Letter + ":";

    /// <summary>
    /// 构造对照行。只收录 <paramref name="isWholeVolumeRoot"/> 判定为真的根
    /// （即「整卷扫描、且数据真在该卷上」），同卷多根合并累加；卷不可用时整行跳过。
    /// </summary>
    /// <param name="roots">本次全部扫描根。</param>
    /// <param name="perRootStats">各根的子树统计（<see cref="ScanStatistics.Roots"/>）。</param>
    /// <param name="isWholeVolumeRoot">该根路径是否为「真实整卷」的谓词。</param>
    public static List<VolumeGap> Build(
        IEnumerable<ScanRoot> roots,
        IReadOnlyDictionary<string, RootStat> perRootStats,
        Func<string, bool> isWholeVolumeRoot)
    {
        var agg = new Dictionary<char, (long Scanned, int Count)>();

        foreach (var root in roots)
        {
            if (root.Path.Length < 2 || root.Path[1] != ':') continue;
            if (!isWholeVolumeRoot(root.Path)) continue;

            char letter = char.ToUpperInvariant(root.Path[0]);
            long scanned = perRootStats.TryGetValue(root.Key, out var st) ? st.SizeBytes : 0;
            agg[letter] = agg.TryGetValue(letter, out var cur)
                ? (cur.Scanned + scanned, cur.Count + 1)
                : (scanned, 1);
        }

        var list = new List<VolumeGap>();
        foreach (var kv in agg.OrderBy(p => p.Key))
        {
            long capacity, free;
            try
            {
                var drive = new DriveInfo(kv.Key + ":");   // DriveInfo 需要 "C:" 形式
                if (!drive.IsReady) continue;
                capacity = drive.TotalSize;
                free = drive.AvailableFreeSpace;
            }
            catch
            {
                continue;   // 卷不可用/无权查询：宁可不显示对照，也不给一个错的数
            }

            long used = capacity - free;
            if (used <= 0) continue;

            list.Add(new VolumeGap
            {
                Letter = kv.Key,
                CapacityBytes = capacity,
                UsedBytes = used,
                FreeBytes = free,
                ScannedBytes = kv.Value.Scanned,
                RootCount = kv.Value.Count
            });
        }
        return list;
    }
}
