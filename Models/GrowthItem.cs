using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Models;

/// <summary>
/// 目录变化的分类。
/// </summary>
public enum GrowthStatus
{
    /// <summary>目录体积增大。</summary>
    Grown,
    /// <summary>目录体积减小。</summary>
    Shrunk,
    /// <summary>本轮新增目录（上一轮快照中不存在）。</summary>
    New,
    /// <summary>本轮消失目录（本轮快照中不存在）。</summary>
    Removed,
    /// <summary>体积无变化。</summary>
    Unchanged,
    /// <summary>上一轮存在但本轮因排除/权限原因未扫描，变化不可比。</summary>
    Skipped
}

/// <summary>
/// 单目录的两次快照对比结果。
/// </summary>
public sealed class GrowthItem
{
    public required string Path { get; init; }
    public required string RootKey { get; init; }
    public GrowthStatus Status { get; init; }
    /// <summary>上一轮占用，null 表示上一轮不存在。</summary>
    public long? PrevBytes { get; init; }
    /// <summary>本轮占用，null 表示本轮不存在。</summary>
    public long? CurrBytes { get; init; }
    /// <summary>增长量（本轮 - 上轮）。缺失侧按 0 计。</summary>
    public long GrowthBytes { get; init; }
    /// <summary>增长百分比。上一轮为 0（或缺失）时为 NaN，报告中显示为 NEW。</summary>
    public double GrowthPercent { get; init; }
    /// <summary>文件数变化。</summary>
    public long FileCountDelta { get; init; }
    /// <summary>相对扫描根的层级（根为 0）。</summary>
    public int Depth { get; init; }

    /// <summary>
    /// 已知目录用途（来自 dir_knowledge 知识库），未命中为 null。
    /// 刻意做成可写属性：对比结果由 SQL 产出后，才在报告阶段批量回填，避免污染扫描/对比链路。
    /// </summary>
    public KnowledgeMatch? Knowledge { get; set; }

    public long AbsGrowth => Math.Abs(GrowthBytes);

    /// <summary>状态的可读名称（随输出语言变化；`--` 与报告共用同一套措辞）。</summary>
    public string StatusText => Status switch
    {
        GrowthStatus.Grown => Lang.T("增长", "Grown"),
        GrowthStatus.Shrunk => Lang.T("缩减", "Shrunk"),
        GrowthStatus.New => Lang.T("新增", "New"),
        GrowthStatus.Removed => Lang.T("消失", "Removed"),
        GrowthStatus.Skipped => Lang.T("已跳过", "Skipped"),
        _ => Lang.T("无变化", "Unchanged")
    };
}

/// <summary>
/// 单个扫描根的总体对比结果（取该根目录自身的子树累计值）。
/// </summary>
public sealed class RootComparison
{
    public required string RootKey { get; init; }
    public required string Path { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>磁盘总容量（仅真实盘符有效，否则为 0）。</summary>
    public long CapacityBytes { get; init; }
    /// <summary>磁盘剩余空间（仅真实盘符有效，否则为 -1）。</summary>
    public long FreeBytes { get; init; } = -1;
    public long? PrevBytes { get; init; }
    public long? CurrBytes { get; init; }
    public long GrowthBytes { get; init; }
    public double GrowthPercent { get; init; }
    public long PrevFiles { get; init; }
    public long CurrFiles { get; init; }
    public long CurrDirs { get; init; }
    /// <summary>该根是否首次记录（库中没有它自己的基准；与整体是否首次运行无关）。</summary>
    public bool IsFirstRun { get; init; }
    /// <summary>该根基准行所属的扫描批次（null = 未知，旧版本库回填失败）。</summary>
    public long? BaselineRunId { get; init; }
    /// <summary>该根基准行的扫描时间（与 BaselineRunId 同源）。</summary>
    public DateTime? BaselineTime { get; init; }
}
