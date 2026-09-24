namespace DiskGrowthMonitor.Models;

/// <summary>
/// 目录枚举引擎的档位（阶段 B）。
///
/// 三档都产出同样的结果口径（目录数 / 文件数 / 占用 / 去重数完全一致，已由 PoC 实证），
/// 区别只在「拿到文件标识的方式」与随之而来的性能：
///   · <see cref="Extd"/> —— NtQueryDirectoryFile + FileIdExtdDirectoryInformation(60)，
///     目录项直出 128 位 FileId。最快，且完全不需要为每个文件开句柄。
///   · <see cref="Both"/> —— 同上的 64 位版本（FileIdBothDirectoryInformation = 37）。
///     门槛低得多（Vista / Server 2008 起即可用），是 WS2012 这类老系统上的**保险绳**：
///     即便 60 不可用，也不必退回逐文件开句柄。
///   · <see cref="Win32"/> —— 现行的 FindFirstFileExW + 逐文件 CreateFileW 取标识。
///     保底实现，也是出问题时的运行时回退通道。
///   · <see cref="Auto"/> —— 按卷在运行时探测，依次尝试 60 → 37 → Win32，结果按卷缓存。
/// </summary>
public enum EnumEngineKind
{
    /// <summary>按卷运行时探测（60 → 37 → Win32）。</summary>
    Auto = 0,

    /// <summary>强制 FileIdExtdDirectoryInformation(60)。</summary>
    Extd = 1,

    /// <summary>强制 FileIdBothDirectoryInformation(37)。</summary>
    Both = 2,

    /// <summary>强制 Win32（FindFirstFileExW + 逐文件句柄）。</summary>
    Win32 = 3
}
