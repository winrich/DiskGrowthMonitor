using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Models;

/// <summary>
/// 目录被跳过（未扫描）的原因。
/// </summary>
public enum SkipReason
{
    /// <summary>命中内置默认排除规则（Windows、$Recycle.Bin、System Volume Information）。</summary>
    DefaultExclude,
    /// <summary>命中命令行 -x 指定的排除规则。</summary>
    UserExclude,
    /// <summary>权限不足（UnauthorizedAccessException），下次扫描将直接跳过。</summary>
    AccessDenied,
    /// <summary>IO 错误或其他异常，下次扫描将直接跳过。</summary>
    IoError,
    /// <summary>目录符号链接 / junction，默认不跟随以避免同一份数据重复计数。</summary>
    ReparsePoint
}

/// <summary>
/// 跳过清单中的一条记录。持久化到 skip_paths 表，
/// 其中 AccessDenied / IoError 会在下次扫描时被直接跳过，不再重复尝试。
/// </summary>
public sealed class SkipRecord
{
    public required string Path { get; init; }
    public required string RootKey { get; init; }
    public SkipReason Reason { get; init; }
    public string? Detail { get; init; }

    /// <summary>数据库中已存在的命中次数（本次运行内新增的为 0）。</summary>
    public int ExistingHitCount { get; set; }

    /// <summary>本批次扫描时间，用于写入 last_seen_run。</summary>
    public long RunId { get; set; }

    public static string ReasonCode(SkipReason r) => r switch
    {
        SkipReason.DefaultExclude => "DEFAULT_EXCLUDE",
        SkipReason.UserExclude => "USER_EXCLUDE",
        SkipReason.AccessDenied => "ACCESS_DENIED",
        SkipReason.IoError => "IO_ERROR",
        SkipReason.ReparsePoint => "REPARSE_POINT",
        _ => "UNKNOWN"
    };

    public static SkipReason ParseReason(string code) => code switch
    {
        "DEFAULT_EXCLUDE" => SkipReason.DefaultExclude,
        "USER_EXCLUDE" => SkipReason.UserExclude,
        "ACCESS_DENIED" => SkipReason.AccessDenied,
        "IO_ERROR" => SkipReason.IoError,
        "REPARSE_POINT" => SkipReason.ReparsePoint,
        _ => SkipReason.IoError
    };

    /// <summary>原因的可读名称（随输出语言变化）。</summary>
    public static string ReasonText(SkipReason r) => r switch
    {
        SkipReason.DefaultExclude => Lang.T("默认排除", "Default exclusion"),
        SkipReason.UserExclude => Lang.T("自定义排除", "Custom exclusion"),
        SkipReason.AccessDenied => Lang.T("权限不足", "Access denied"),
        SkipReason.IoError => Lang.T("IO 错误", "IO error"),
        SkipReason.ReparsePoint => Lang.T("符号链接（未跟随）", "Reparse point (not followed)"),
        _ => Lang.T("未知", "Unknown")
    };

    /// <summary>说明该原因是否会导致「下次扫描直接跳过」。</summary>
    public static bool IsPersistentSkip(SkipReason r)
        => r is SkipReason.AccessDenied or SkipReason.IoError;
}
