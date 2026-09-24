namespace DiskGrowthMonitor.Models;

/// <summary>
/// 单个目录的占用快照记录。
/// Size/FileCount/DirCount 均为「子树累计值」：包含自身直接文件以及所有后代目录的内容。
/// </summary>
/// <param name="Path">目录完整路径，作为数据库主键。</param>
/// <param name="RootKey">所属扫描根键（盘符如 "C:" 或自定义根路径）。</param>
/// <param name="Depth">相对扫描根的层级，根为 0。</param>
/// <param name="SizeBytes">子树占用字节数。</param>
/// <param name="FileCount">子树内文件总数。</param>
/// <param name="DirCount">子树内后代目录总数（不含自身）。</param>
public readonly record struct DirSnapshot(
    string Path,
    string RootKey,
    int Depth,
    long SizeBytes,
    long FileCount,
    long DirCount);

/// <summary>
/// 一次扫描批次的汇总记录。
/// </summary>
public sealed class ScanRun
{
    public long Id { get; set; }
    public DateTime ScanTime { get; set; }
    /// <summary>本批次扫描的根，逗号分隔。</summary>
    public string Roots { get; set; } = string.Empty;
    public long TotalFiles { get; set; }
    public long TotalDirs { get; set; }
    public long TotalBytes { get; set; }
    public long ElapsedMs { get; set; }
    /// <summary>本次是否为首次运行（数据库中无上一轮快照）。</summary>
    public bool IsFirstRun { get; set; }
}

/// <summary>
/// 扫描过程中统计到的运行指标。
/// </summary>
public sealed class ScanStatistics
{
    public long TotalFiles;
    public long TotalDirs;
    public long TotalBytes;
    public long ElapsedMs;
    /// <summary>权限不足被跳过的目录数。</summary>
    public int AccessDeniedCount;
    /// <summary>命中默认/自定义排除规则的目录数（= 默认排除 + 用户排除）。</summary>
    public int ExcludedCount;
    /// <summary>
    /// 其中属于「内置默认排除」的条数；用户 <c>-x</c> 排除 = <see cref="ExcludedCount"/> 减本值。
    /// 单独统计的原因：默认排除里的系统目录命中知识库时会被豁免放行，两者合并显示会出现
    /// 「设置里默认排除是启用的，统计里排除却是 0」这种看似自相矛盾的数字。
    /// </summary>
    public int DefaultExcludedCount;
    /// <summary>目录符号链接/junction，未跟随的数量。</summary>
    public int ReparseCount;
    /// <summary>IO 错误等其他异常数量。</summary>
    public int ErrorCount;
    /// <summary>因硬链接去重而未重复计入的文件数。</summary>
    public long DedupedFileCount;
    /// <summary>因硬链接去重而未重复计入的字节数。</summary>
    public long DedupedBytes;
    /// <summary>经「目录项直出 FileId」完成去重判定的文件数（NT 枚举档下即全部非空文件数）。</summary>
    public long DedupedViaEntryFileId;
    /// <summary>去重集合中已登记的标识总数（跨卷累计）。</summary>
    public long DedupIdentifierCount;
    /// <summary>各根实际使用的目录枚举引擎摘要（按卷解析后的结果）。</summary>
    public string EnumEngineSummary = string.Empty;
    /// <summary>枚举引擎的解析/降级说明（只在确有降级等不寻常情形时非空）。</summary>
    public List<string> EnumEngineNotes { get; } = new();
    /// <summary>通过 USN 增量直接复用上一轮快照、未重新扫描的目录数。</summary>
    public long ReusedDirs;
    /// <summary>因命中目录用途知识库而被豁免、重新尝试扫描的目录数（本已命中跳过规则但被放行）。</summary>
    public long KnowledgeRescuedCount;
    /// <summary>实际使用的并行扫描线程数。</summary>
    public int UsedThreads;

    /// <summary>
    /// 后台写库线程上报的失败（写暂存表异常）。非 null 时调用方必须中止提交：
    /// 暂存数据可能残缺，不能迁入正式快照参与对比。
    /// </summary>
    public string? WriteError;
    /// <summary>扫描失败的根键（根目录不可访问或根级异常），用于决定是否保存 USN 状态。</summary>
    public HashSet<string> FailedRoots { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>各扫描根的子树统计。</summary>
    public Dictionary<string, RootStat> Roots { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>单个扫描根的子树汇总。</summary>
public sealed class RootStat
{
    public long SizeBytes;
    public long FileCount;
    public long DirCount;
}
