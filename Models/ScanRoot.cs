using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Models;

/// <summary>
/// 扫描根的定义。既可以是真实盘符（C:），也可以是 --root 指定的任意目录。
/// 作为数据库中 dir_snapshots.root_key 的值，用于按根筛选与快照轮换。
/// </summary>
public sealed class ScanRoot
{
    /// <summary>根键。盘符形式为 "C:"，自定义目录形式为规范化后的完整路径（如 "C:\Users\henry\WorkBuddy"）。</summary>
    public required string Key { get; init; }

    /// <summary>扫描起始目录的完整路径。盘符为 "C:\"，自定义目录为该目录本身。</summary>
    public required string Path { get; init; }

    /// <summary>是否为真实磁盘盘符（用于报告中的磁盘容量信息展示）。</summary>
    public bool IsDrive { get; init; }

    /// <summary>展示名称（中文档「C: 盘」，英文档 "C: drive"；目录根两种语言都用完整路径）。</summary>
    public string DisplayName => IsDrive ? Lang.T(Key + " 盘", Key + " drive") : Path;

    public static ScanRoot FromDrive(string driveLetter)
    {
        string letter = driveLetter.TrimEnd(':', '\\').ToUpperInvariant();
        return new ScanRoot
        {
            Key = letter + ":",
            Path = letter + ":\\",
            IsDrive = true
        };
    }

    public static ScanRoot FromDirectory(string dir)
    {
        string full = System.IO.Path.GetFullPath(dir);
        // 统一规范：去掉尾部分隔符（根目录保留 "X:\" 形式）
        if (full.Length > 3 && full[full.Length - 1] == '\\')
            full = full.Substring(0, full.Length - 1);

        // 目录恰好是盘符根时，与 -d 指定的盘符统一为同一个 root_key，避免同一路径出现两条记录
        bool isDriveRoot = full.Length == 3 && full[1] == ':' && full[2] == '\\';
        if (isDriveRoot)
            return FromDrive(full.Substring(0, 2));

        return new ScanRoot
        {
            Key = full,
            Path = full,
            IsDrive = false
        };
    }
}
