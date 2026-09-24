using DiskGrowthMonitor.Services;

namespace DiskGrowthMonitor.Models;

/// <summary>
/// 生成 HTML 报告所需的完整数据模型。
/// </summary>
public sealed class ReportModel
{
    public required AnalysisResult Analysis { get; init; }
    public required ScanStatistics Statistics { get; init; }
    public required IReadOnlyList<ScanRoot> Roots { get; init; }
    public required IReadOnlyList<SkipRecord> SkipRecords { get; init; }

    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public DateTime ScanTime { get; init; }
    public DateTime StartedAt { get; init; }
    public long RunId { get; init; }
    public long PreviousRunId { get; init; }

    /// <summary>报告用阈值（仅用于在页面上标注）。</summary>
    public long MinBytes { get; init; }
    public double MinPercent { get; init; }
    public int Top { get; init; }
    public int DetailTop { get; init; }
    public long DetailMinBytes { get; init; }

    public bool UseDefaultExclude { get; init; }
    public bool DedupEnabled { get; init; }
    public bool FollowReparse { get; init; }

    /// <summary>本次是否开启了「知识库豁免」（命中目录用途知识库的目录每轮重新尝试扫描）。</summary>
    public bool KnowledgeRescanEnabled { get; init; }

    /// <summary>跳过清单中因命中知识库而会每轮重试的路径（用于在报告里打标记）。</summary>
    public HashSet<string> KnowledgeRescuedPaths { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 本轮命中的「可安全清理」目录（按占用降序，已去除被祖先条目覆盖的子孙）。
    /// 由 <see cref="CleanupAdvisor.Find"/> 在报告生成前算好，渲染器本身不做任何数据库访问。
    /// </summary>
    public IReadOnlyList<CleanableDir> Cleanable { get; init; } = new List<CleanableDir>();

    /// <summary>可安全清理空间合计（字节）。</summary>
    public long CleanableTotalBytes => Cleanable.Sum(x => x.Bytes);

    /// <summary>
    /// 「扫描统计 vs 卷已用」的口径对照行（只含整卷扫描根，见 <see cref="VolumeGap.Build"/>）。
    /// 由 Program 在扫描完成后算好传入 —— 渲染器不碰磁盘/卷 API，报告只是一份可离线阅读的快照。
    /// </summary>
    public IReadOnlyList<VolumeGap> VolumeGaps { get; init; } = new List<VolumeGap>();

    /// <summary>扫描模式说明（全量 / USN 增量复用等），显示在报告配置表中。</summary>
    public string? ScanModeNote { get; init; }
    public string DatabasePath { get; init; } = string.Empty;

    public bool IsFirstRun => Analysis.IsFirstRun;
}
