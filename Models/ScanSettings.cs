namespace DiskGrowthMonitor.Models;

/// <summary>
/// 扫描器行为配置。
/// </summary>
public sealed class ScanSettings
{
    /// <summary>是否启用硬链接去重（同一物理数据只计一次）。</summary>
    public bool Dedup { get; init; } = true;

    /// <summary>是否跟随目录符号链接 / junction。</summary>
    public bool FollowReparse { get; init; }

    /// <summary>是否启用内置默认排除清单。</summary>
    public bool UseDefaultExclude { get; init; } = true;

    /// <summary>强制全量扫描（跳过 USN 增量复用）。</summary>
    public bool ForceFullScan { get; init; }

    /// <summary>并行扫描线程数；0 = 自动（按扫描根数并行）。</summary>
    public int Threads { get; init; }

    /// <summary>
    /// 目录枚举引擎档位（阶段 B）。默认 <see cref="EnumEngineKind.Auto"/>：按卷实测 60 → 37 → Win32。
    /// 降级只影响「怎么拿到文件标识」，不影响结果口径；<see cref="EnumEngineKind.Win32"/>
    /// 即改动前的行为，是运行时的回退通道。
    /// </summary>
    public EnumEngineKind EnumEngine { get; init; } = EnumEngineKind.Auto;

    public static ScanSettings From(global::DiskGrowthMonitor.Cli.Options o) => new()
    {
        Dedup = !o.NoDedup,
        FollowReparse = o.FollowReparse,
        UseDefaultExclude = !o.NoDefaultExclude,
        ForceFullScan = o.FullScan,
        Threads = o.Threads,
        EnumEngine = o.EnumEngine
    };
}
