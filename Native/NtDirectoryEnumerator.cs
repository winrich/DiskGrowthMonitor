// ============================================================================
//  NtDirectoryEnumerator.cs —— 目录枚举的 NT 原生实现（目录项直出 FileId）
//
//  为什么需要它：
//    FindFirstFileExW 返回 WIN32_FIND_DATAW —— 有大小、有属性，**没有 FileId**。
//    因此为了硬链接去重，现行实现必须对「每个非空文件」调一次 CreateFileW +
//    GetFileInformationByHandle 才拿得到标识（实测占全盘扫描耗时约 69%）。
//
//    NtQueryDirectoryFile + FileIdExtdDirectoryInformation(60) 返回
//    FILE_ID_EXTD_DIR_INFORMATION —— 大小、属性、**128 位 FileId** 全在同一次调用里，
//    于是去重所需的全部信息零额外句柄即可拿到。
//
//  ⚠ 枚举值极易记混，本文件用的是 FILE_INFORMATION_CLASS（配 NtQueryDirectoryFile）：
//        FileIdBothDirectoryInformation = 37   （Vista / Server 2008 起可用，64 位标识）
//        FileIdExtdDirectoryInformation = 60   （128 位标识）
//    而 0xA / 0xB、0x13 / 0x14 属于 FILE_INFO_BY_HANDLE_CLASS（配 GetFileInformationByHandleEx），
//    两套是不同枚举，混用会得到 STATUS_INVALID_INFO_CLASS。
//
//  ⚠ 解析必须用 unsafe 指针，而 C# **不允许在迭代器（yield）方法里出现 unsafe 上下文**（CS1629）。
//    所以这里分成两层：内层 Parse 是普通 unsafe 方法，把「一轮缓冲」解析成一批条目；
//    外层 Enumerate 是迭代器，逐批 yield（批内复用同一个 List，下一轮解析前才 Clear）。
//
//  ⚠ 缓冲大小固定 64 KB：PoC 实测 NTFS 每轮 NtQueryDirectoryFile 约 64 KB 封顶
//    （64 / 256 / 1024 KB 三者的轮次完全相同），而 1 MB 反而慢 2.9~3.1 倍。
// ============================================================================
using System.Runtime.InteropServices;
using DiskGrowthMonitor.Services;
using DiskGrowthMonitor.Util;
using Microsoft.Win32.SafeHandles;

namespace DiskGrowthMonitor.Native;

internal static class NtDirectoryEnumerator
{
    // ---- FILE_INFORMATION_CLASS ----
    public const int FileIdBothDirectoryInformation = 37;
    public const int FileIdExtdDirectoryInformation = 60;

    // ---- NTSTATUS ----
    private const int STATUS_SUCCESS = 0;
    private const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    private const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);
    private const int STATUS_NO_MORE_FILES = unchecked((int)0x80000006);
    private const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);
    private const int STATUS_INVALID_INFO_CLASS = unchecked((int)0xC0000003);
    private const int STATUS_INVALID_PARAMETER = unchecked((int)0xC000000D);
    private const int STATUS_NOT_SUPPORTED = unchecked((int)0xC00000BB);
    private const int STATUS_OBJECT_NAME_NOT_FOUND = unchecked((int)0xC0000034);
    private const int STATUS_OBJECT_PATH_NOT_FOUND = unchecked((int)0xC000003A);
    private const int STATUS_NO_SUCH_FILE = unchecked((int)0xC000000F);

    // ---- CreateFileW 参数 ----
    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_ALL = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OPEN_EXISTING = 3;
    /// <summary>打开目录所必需（缺了它 CreateFileW 打不开目录）。</summary>
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;

    /// <summary>FILE_ATTRIBUTE_DIRECTORY：目录。</summary>
    public const uint FileAttributesDirectory = 0x00000010;
    /// <summary>FILE_ATTRIBUTE_REPARSE_POINT：重解析点（junction / 符号链接）。</summary>
    public const uint FileAttributesReparsePoint = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryDirectoryFile(
        IntPtr FileHandle,
        IntPtr Event,
        IntPtr ApcRoutine,
        IntPtr ApcContext,
        out IoStatusBlock IoStatusBlock,
        IntPtr FileInformation,
        uint Length,
        int FileInformationClass,
        byte ReturnSingleEntry,
        IntPtr FileName,
        byte RestartScan);

    // FileName 字段偏移。文档布局：class 60 = 88（FileId 16 字节之后）、class 37 = 104
    // （ShortName[12] 之后）。首次使用时按候选表自识别一次并缓存，之后零开销。
    private static volatile int _nameOffsetExtd = -1;
    private static volatile int _nameOffsetBoth = -1;

    /// <summary>
    /// 枚举单个目录的直接子项（不含 "." / ".."），条目自带 FileId。
    /// 异常类型与 <see cref="DirectoryEnumerator"/> 逐项对齐（UnauthorizedAccessException /
    /// DirectoryNotFoundException / IOException），另加 NotSupportedException 表示
    /// 该卷不接受请求的信息类（供枚举引擎探测与降级使用）。
    /// </summary>
    /// <param name="dir">目录的完全限定路径（无 \\?\ 前缀）。</param>
    /// <param name="needFilePath">是否需要构造文件完整路径；false 时文件项的 FullPath 为 null。NT 路径下
    /// 去重不需要路径 ⇒ 传 false 可省下每个文件一次字符串拼接。</param>
    /// <param name="infoClass">37（64 位 FileId）或 60（128 位 FileId）。</param>
    /// <param name="bufferSize">单轮缓冲字节数（推荐 64 KB，见文件头说明）。</param>
    public static IEnumerable<RawEntry> Enumerate(string dir, bool needFilePath, int infoClass, int bufferSize)
    {
        using var handle = CreateFileW(
            ToLongPath(dir),
            FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES,
            FILE_SHARE_ALL,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw MapWin32Error(dir, Marshal.GetLastWin32Error());

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        var batch = new List<RawEntry>(256);
        try
        {
            byte restart = 1;
            while (true)
            {
                IoStatusBlock iosb;
                int status = NtQueryDirectoryFile(
                    handle.DangerousGetHandle(), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    out iosb, buffer, (uint)bufferSize, infoClass,
                    0, IntPtr.Zero, restart);
                restart = 0;

                // 第一轮就 NO_MORE_FILES = 空目录（正常结束，不是错误）
                if (status == STATUS_NO_MORE_FILES) yield break;
                if (status != STATUS_SUCCESS && status != STATUS_BUFFER_OVERFLOW && status != STATUS_BUFFER_TOO_SMALL)
                {
                    int hard = status;
                    throw MapNtStatus(dir, hard);
                }

                int bytes = iosb.Information.ToInt32();
                // ⚠ 「状态成功」≠「拿到数据」：多个信息类会返回 STATUS_SUCCESS 但 Information = 0。
                // 这里以实际字节数为准，0 即表示本轮没有条目。
                if (bytes <= 0) yield break;

                batch.Clear();
                Parse(buffer, bytes, infoClass, dir, needFilePath, batch);
                // 批内复用同一个 List 是安全的：所有条目 yield 完才会进入下一轮 Clear。
                foreach (var entry in batch) yield return entry;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>把一轮缓冲解析成条目（unsafe 必须放在非迭代器方法里）。</summary>
    private static unsafe void Parse(
        IntPtr buffer, int bytes, int infoClass, string dir, bool needFilePath, List<RawEntry> list)
    {
        int nameOffset = ResolveNameOffset(buffer, bytes, infoClass);
        int fileIdOffset = nameOffset - (infoClass == FileIdExtdDirectoryInformation ? 16 : 8);
        if (fileIdOffset < 0) return;

        byte* start = (byte*)buffer;
        byte* end = start + bytes;
        byte* p = start;

        while (true)
        {
            // 边界下界只能取「固定头大小」：FileNameLength 位于偏移 60、占 4 字节。
            // 🔴 不能写成 p + nameOffset + 4 > end —— 那隐含要求文件名至少 2 个字符，
            //    而 NTFS 对**最后一个条目**不做 8 字节补齐（单字符名条目正好是 90 字节），
            //    于是「单字符名字的条目」会被判成越界、整个缓冲被静默丢弃。
            //    WinSxS\Temp\InFlight 下大量目录就叫 r，实测因此漏掉 33% 的目录行。
            if (p + 64 > end) break;

            uint next = *(uint*)p;
            long eof = *(long*)(p + 40);
            uint attrs = *(uint*)(p + 56);
            uint nameLen = *(uint*)(p + 60);

            // FileNameLength：必须非 0、偶数（UTF-16）、且不超 32767 个字符
            if (nameLen == 0 || (nameLen & 1) != 0 || nameLen > 65534) break;
            if (p + nameOffset + nameLen > end) break;

            string name = new string((char*)(p + nameOffset), 0, (int)(nameLen / 2));
            if (name.Length > 0 && name != "." && name != "..")
            {
                ulong fileIdLow = *(ulong*)(p + fileIdOffset);
                ulong fileIdHigh = infoClass == FileIdExtdDirectoryInformation
                    ? *(ulong*)(p + fileIdOffset + 8) : 0;

                if ((attrs & FileAttributesDirectory) != 0)
                {
                    list.Add(new RawEntry(name, Combine(dir, name), 0, true,
                        (attrs & FileAttributesReparsePoint) != 0, fileIdLow, fileIdHigh, attrs));
                }
                else
                {
                    list.Add(new RawEntry(null, needFilePath ? Combine(dir, name) : null,
                        eof, false, false, fileIdLow, fileIdHigh, attrs));
                }
            }

            if (next == 0) break;                                // 最后一条
            if ((next & 3) != 0) break;                          // 只保证 4 字节对齐
            if (p + next > end) break;
            if (next < nameOffset + nameLen) break;              // 不可能小于最小条目长度
            p += next;
        }
    }

    /// <summary>
    /// 取得（必要时先自识别）FileName 字段的字节偏移。只做一次，之后走缓存。
    /// 自识别失败时退回文档布局，让 Parse 的边界检查自行终止，而不是抛异常把整个目录判成 IO 错误。
    /// </summary>
    private static int ResolveNameOffset(IntPtr buffer, int bytes, int infoClass)
    {
        bool extd = infoClass == FileIdExtdDirectoryInformation;
        int cached = extd ? _nameOffsetExtd : _nameOffsetBoth;
        if (cached > 0) return cached;

        int[] candidates = extd ? new[] { 88, 96, 104 } : new[] { 104, 96, 112 };
        int resolved = extd ? 88 : 104;
        foreach (int candidate in candidates)
        {
            if (!ValidateLayout(buffer, bytes, candidate, infoClass)) continue;
            resolved = candidate;
            break;
        }

        if (extd) _nameOffsetExtd = resolved;
        else _nameOffsetBoth = resolved;
        return resolved;
    }

    /// <summary>按给定 FileName 偏移试走查一轮缓冲，全部条目都通过合理性校验才算布局正确。</summary>
    private static unsafe bool ValidateLayout(IntPtr buffer, int bytes, int nameOffset, int infoClass)
    {
        byte* start = (byte*)buffer;
        byte* end = start + bytes;
        byte* p = start;
        int guard = 0;

        while (true)
        {
            if (++guard > 1_000_000) return false;
            // 同 Parse：下界只能取固定头大小，绝不能把 nameOffset 加进来
            // （否则单字符名条目会被误判为越界，导致布局自识别失败）
            if (p + 64 > end) return false;

            uint next = *(uint*)p;
            uint nameLen = *(uint*)(p + 60);
            if (nameLen == 0 || (nameLen & 1) != 0 || nameLen > 65534) return false;
            if (p + nameOffset + nameLen > end) return false;
            if (!IsPlausibleName((char*)(p + nameOffset), (int)(nameLen / 2))) return false;

            if (next == 0) return true;
            if ((next & 3) != 0) return false;
            if (p + next > end) return false;
            if (next < nameOffset + nameLen) return false;
            p += next;
        }
    }

    /// <summary>文件名可信度校验：偏移错位最典型的症状就是读出控制符 / 反斜杠 / 替换符。</summary>
    private static unsafe bool IsPlausibleName(char* name, int length)
    {
        if (length <= 0) return false;
        for (int i = 0; i < length; i++)
        {
            char c = name[i];
            if (c < 0x20) return false;
            if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' ||
                c == '"' || c == '<' || c == '>' || c == '|') return false;
            if (c == '\uFFFD' || c == '\uFFFF') return false;
            if (char.IsSurrogate(c)) return false;
            if (i == 0 && length > 1 && c >= 0xE000 && c <= 0xF8FF) return false;
        }
        return true;
    }

    /// <summary>NTSTATUS → 与 Win32 枚举器语义一致的异常。</summary>
    private static Exception MapNtStatus(string dir, int status)
    {
        switch (status)
        {
            case STATUS_ACCESS_DENIED:
                return new UnauthorizedAccessException(Lang.F("对路径“{0}”的访问被拒绝。",
                    "Access to the path \"{0}\" is denied.", dir));
            case STATUS_OBJECT_NAME_NOT_FOUND:
            case STATUS_OBJECT_PATH_NOT_FOUND:
            case STATUS_NO_SUCH_FILE:
                return new DirectoryNotFoundException(Lang.F("找不到路径“{0}”的一部分。",
                    "Could not find a part of the path \"{0}\".", dir));
            // 这三个表示「该卷/文件系统不接受这个信息类」——由枚举引擎探测捕获并降级，
            // 正常路径下不应传播到扫描器。
            case STATUS_INVALID_INFO_CLASS:
            case STATUS_NOT_SUPPORTED:
            case STATUS_INVALID_PARAMETER:
                return new NotSupportedException(Lang.F("该卷不支持目录枚举信息类 {0:X8} 对应的布局：{1}",
                    "This volume does not support the layout for directory enumeration info class {0:X8}: {1}",
                    status, dir));
            default:
                return new IOException(Lang.F("枚举目录失败（NTSTATUS 0x{0:X8}）：{1}",
                    "Failed to enumerate the directory (NTSTATUS 0x{0:X8}): {1}", status, dir));
        }
    }

    /// <summary>CreateFileW 失败 → 与 Win32 枚举器语义一致的异常。</summary>
    private static Exception MapWin32Error(string dir, int err)
    {
        switch (err)
        {
            case ERROR_FILE_NOT_FOUND:
            case ERROR_PATH_NOT_FOUND:
                return new DirectoryNotFoundException(Lang.F("找不到路径“{0}”的一部分。",
                    "Could not find a part of the path \"{0}\".", dir));
            case ERROR_ACCESS_DENIED:
                return new UnauthorizedAccessException(Lang.F("对路径“{0}”的访问被拒绝。",
                    "Access to the path \"{0}\" is denied.", dir));
            default:
                return new IOException(Lang.F("打开目录失败（Win32 错误 {0}）：{1}",
                    "Failed to open the directory (Win32 error {0}): {1}", err, dir));
        }
    }

    /// <summary>拼接目录与子项名称（避免 Path.Combine 的参数校验开销）。</summary>
    private static string Combine(string dir, string name)
    {
        if (dir.Length == 0) return name;
        return dir[dir.Length - 1] == '\\' ? dir + name : dir + "\\" + name;
    }

    /// <summary>转成 NT 长路径形式（\\?\ 前缀），突破 MAX_PATH。</summary>
    private static string ToLongPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
        return @"\\?\" + path;
    }
}
