// 目录枚举的 Win32 实现。.NET Framework 4.5 没有 System.IO.Enumeration 命名空间
// （FileSystemEnumerable / EnumerationOptions / FileSystemEntry 均为 .NET Core 3.0+ 专有），
// 因此本文件是唯一的枚举实现（原 net9.0 侧的 BCL 分支已随目标框架收敛一并删除）。
using System.Runtime.InteropServices;
using DiskGrowthMonitor.Services;
using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Native;

/// <summary>
/// 目录枚举的原生实现（FindFirstFileExW / FindNextFileW）。
///
/// 为什么需要它：.NET Framework 4.5 没有 System.IO.Enumeration 命名空间
/// （FileSystemEnumerable / EnumerationOptions / FileSystemEntry 都是 .NET Core 3.0+ 专有），
/// 因此这里用 Win32 等价实现，语义严格对齐原先的用法：
///   - 不跳过隐藏/系统文件（原 AttributesToSkip = 0）；
///   - 目录不可访问时抛 UnauthorizedAccessException（原 IgnoreInaccessible = false）；
///   - 不递归（RecurseSubdirectories = false）；
///   - 不返回 "." 与 ".."（原 ReturnSpecialDirectories = false）。
///
/// 长路径：调用前自动加 \\?\ 前缀，突破 .NET Framework 的 MAX_PATH(260) 限制
/// （上限约 32767 字符），避免深层目录被 PathTooLongException 打断。
/// </summary>
internal static class DirectoryEnumerator
{
    // ---- Win32 常量 ----
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_NO_MORE_FILES = 18;

    private const int FindExInfoBasic = 1;          // 不返回 8.3 短名，省一次填充
    private const int FindExSearchNameMatch = 0;
    private const uint FIND_FIRST_EX_LARGE_FETCH = 0x00000002;   // 批量取回，减少系统调用

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public uint ftCreationTimeLow;
        public uint ftCreationTimeHigh;
        public uint ftLastAccessTimeLow;
        public uint ftLastAccessTimeHigh;
        public uint ftLastWriteTimeLow;
        public uint ftLastWriteTimeHigh;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "FindFirstFileExW")]
    private static extern IntPtr FindFirstFileExW(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FIND_DATAW lpFindFileData,
        int fSearchOp,
        IntPtr lpSearchFilter,
        uint dwAdditionalFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "FindNextFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);

    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>枚举单个目录的直接子项（不含 "." / ".."）。</summary>
    /// <param name="dir">目录的完全限定路径（无 \\?\ 前缀）。</param>
    /// <param name="needFilePath">是否构造文件的完整路径；false 时文件项的 FullPath 为 null，省下字符串分配。</param>
    public static IEnumerable<RawEntry> Enumerate(string dir, bool needFilePath)
    {
        string pattern = BuildSearchPattern(dir);
        IntPtr h = FindFirstFileExW(pattern, FindExInfoBasic, out WIN32_FIND_DATAW data,
            FindExSearchNameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);

        if (h == InvalidHandleValue)
        {
            int err = Marshal.GetLastWin32Error();

            // 空目录：FindFirstFile 用 ERROR_FILE_NOT_FOUND 表示"没有任何匹配项"
            if (err == ERROR_FILE_NOT_FOUND) yield break;
            if (err == ERROR_ACCESS_DENIED)
                throw new UnauthorizedAccessException(Lang.F("对路径“{0}”的访问被拒绝。",
                    "Access to the path \"{0}\" is denied.", dir));
            if (err == ERROR_PATH_NOT_FOUND)
                throw new DirectoryNotFoundException(Lang.F("找不到路径“{0}”的一部分。",
                    "Could not find a part of the path \"{0}\".", dir));

            throw new IOException(Lang.F("枚举目录失败（Win32 错误 {0}）：{1}",
                "Failed to enumerate the directory (Win32 error {0}): {1}", err, dir));
        }

        try
        {
            while (true)
            {
                string name = data.cFileName;
                uint attrs = data.dwFileAttributes;

                // FindFirstFileEx 会返回 "." 与 ".."，必须剔除：
                // 否则 Path 拼接后等于同一目录，会造成无限递归
                if (name.Length > 0 && name != "." && name != "..")
                {
                    // FileId 两字段恒为 0：FindFirstFileExW 的返回结构里没有文件标识，
                    // 调用方据此知道「这一档要用逐文件句柄拿标识」（见 RawEntry 注释）。
                    if ((attrs & FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        yield return new RawEntry(
                            name,
                            Combine(dir, name),
                            0,
                            true,
                            (attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0,
                            0,
                            0,
                            attrs);
                    }
                    else
                    {
                        yield return new RawEntry(
                            null,
                            needFilePath ? Combine(dir, name) : null,
                            ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow,
                            false,
                            false,
                            0,
                            0,
                            attrs);
                    }
                }

                if (!FindNextFileW(h, out data))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_NO_MORE_FILES)
                        throw new IOException(Lang.F("枚举目录中断（Win32 错误 {0}）：{1}",
                            "Directory enumeration was interrupted (Win32 error {0}): {1}", err, dir));
                    break;
                }
            }
        }
        finally
        {
            FindClose(h);
        }
    }

    /// <summary>拼接目录与子项名称。避免 Path.Combine 的参数校验开销（百万级调用很可观）。</summary>
    private static string Combine(string dir, string name)
    {
        if (dir.Length == 0) return name;
        return dir[dir.Length - 1] == '\\' ? dir + name : dir + "\\" + name;
    }

    /// <summary>构造搜索模式，必要时加 \\?\ 前缀以支持超长路径。</summary>
    private static string BuildSearchPattern(string dir)
    {
        if (dir.StartsWith(@"\\?\", StringComparison.Ordinal))
            return AppendWildcard(dir);

        if (dir.StartsWith(@"\\", StringComparison.Ordinal))
            return AppendWildcard(@"\\?\UNC\" + dir.Substring(2));   // UNC 形式

        return AppendWildcard(@"\\?\" + dir);
    }

    private static string AppendWildcard(string path)
    {
        if (path.Length > 0 && path[path.Length - 1] == '\\') return path + "*";
        return path + "\\*";
    }
}
