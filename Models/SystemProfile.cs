using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Models;

/// <summary>
/// 运行环境探测结果（阶段 P）。
///
/// 拆成「免提权」与「需提权」两段是有意为之：<see cref="ProbeComplete"/> 明确告诉调用方
/// 卷级信息到底是「真的没有」还是「没权限看」。把这两种情况混为一谈，
/// 是自适逻辑最容易犯的错 —— 非管理员下会把 SSD 误当成 HDD，从而把并行度压到 1。
/// </summary>
public sealed class SystemProfile
{
    // ---------------------------------------------------------- 第一段：任何进程都能读

    /// <summary>OS 主版本号（RtlGetVersion 真值）。</summary>
    public uint OsMajor { get; set; }

    /// <summary>OS 次版本号。</summary>
    public uint OsMinor { get; set; }

    /// <summary>OS 编译号。</summary>
    public uint OsBuild { get; set; }

    /// <summary>产品类型：1 = 工作站，2 = 域控制器，3 = 服务器。</summary>
    public byte ProductType { get; set; }

    /// <summary>可读的 OS 名称（如 "Windows 11" / "Windows Server 2012"）；探测不到时占位。</summary>
    public string OsName { get; set; } = Lang.T("未知", "unknown");

    /// <summary>版本号是否来自 RtlGetVersion（true）还是降级来源（false，可能被兼容性清单劫持）。</summary>
    public bool OsVersionReliable { get; set; }

    /// <summary>是否为服务器版（含域控制器）。</summary>
    public bool IsServer => ProductType == 2 || ProductType == 3;

    /// <summary>物理核数。</summary>
    public int PhysicalCores { get; set; }

    /// <summary>逻辑处理器数。</summary>
    public int LogicalCores { get; set; }

    /// <summary>物理内存总量（字节）；0 = 探测失败。</summary>
    public long TotalMemoryBytes { get; set; }

    /// <summary>当前进程是否为管理员。</summary>
    public bool IsElevated { get; set; }

    // ---------------------------------------------------------- 语言（免提权，第 57 轮新增）

    /// <summary>
    /// 操作系统语言探查结果（界面语言 / 控制台代码页 / 最终输出语言 / 判定依据）。
    ///
    /// 它在 <see cref="SystemProfile"/> 里只是<b>携带</b>作用 —— 真正的探查与裁决由
    /// <see cref="Services.LanguageProbe"/> 在 <c>Program.Main</c> 最开头完成
    /// （必须早于任何输出，也早于把控制台切到 UTF-8），此处仅由
    /// <see cref="Services.EnvironmentProbe.Probe"/> 原样带入，供 <c>--show-env</c> 展示。
    /// 为 null 表示调用方未提供（例如单元探针直接构造 profile），展示时按「未探查」处理。
    /// </summary>
    public LanguageInfo? Language { get; set; }

    // ---------------------------------------------------------- 第二段：需提权

    /// <summary>
    /// 卷级探测（介质类型）是否全部成功。
    /// false 表示卷信息可能为「未知」—— 绝不能据此推断介质是机械盘。
    /// </summary>
    public bool ProbeComplete { get; set; }

    /// <summary>扫描根涉及的卷（按盘符去重）。</summary>
    public List<VolumeProfile> Volumes { get; } = new();

    /// <summary>探测过程中遇到的问题（用于输出提示），已读友好。</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>单个卷的环境信息。</summary>
public sealed class VolumeProfile
{
    /// <summary>盘符字母（大写，不含冒号）。</summary>
    public char Letter { get; set; }

    /// <summary>文件系统名（NTFS / FAT32 / exFAT …）；未知时为空串。</summary>
    public string FileSystem { get; set; } = string.Empty;

    /// <summary>该卷是否支持硬链接（NTFS 支持，FAT32/exFAT 不支持）。</summary>
    public bool SupportsHardLinks { get; set; }

    /// <summary>卷总容量（字节）；0 = 未知。</summary>
    public long TotalBytes { get; set; }

    /// <summary>卷可用空间（字节）；0 = 未知。</summary>
    public long FreeBytes { get; set; }

    /// <summary>所在物理磁盘号；-1 = 探测失败。注意绝不能假设 C: 就是 0 号盘。</summary>
    public int DiskNumber { get; set; } = -1;

    /// <summary>介质是否为固态（false = 机械盘）；**null = 未知**（非管理员或探测失败）。</summary>
    public bool? IsSolidState { get; set; }
}

/// <summary>
/// 由环境探测结果推导出的扫描策略。
///
/// <para><b>枚举引擎（阶段 B）已接入执行链路</b>：实际用哪一档由
/// <see cref="Services.EnumEngineSelector"/> 在扫描前按卷实测决定，本类里的
/// <see cref="EnumEngine"/> 是「意图描述」，供 --show-env 展示与核对。</para>
///
/// <para>并行度候选集仍是「只展示」：策略一旦生效就会改变扫描结果口径，
/// 必须先把决策依据摆出来、让人能一条条核对，再逐阶段接入（阶段 D）。</para>
/// </summary>
public sealed class ScanStrategy
{
    /// <summary>目录枚举引擎。</summary>
    public string EnumEngine { get; set; } = string.Empty;

    /// <summary>
    /// 并行度候选集：阶段 D 会逐档实测（每档至少 2 个样本），得分最优者固化。
    /// 这里给出的是「值得一试」的范围，不是最终值。
    /// </summary>
    public int[] ParallelCandidates { get; set; } = new int[0];

    /// <summary>目录枚举缓冲字节数。</summary>
    public int EnumBufferBytes { get; set; }

    /// <summary>是否启用硬链接去重。</summary>
    public bool Dedup { get; set; } = true;

    /// <summary>决策依据（逐条打印，便于核对与排错）。</summary>
    public List<string> Reasons { get; } = new();
}
