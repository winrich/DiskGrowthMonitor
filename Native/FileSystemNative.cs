using System.Runtime.InteropServices;
using DiskGrowthMonitor.Compat;
using DiskGrowthMonitor.Util;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskGrowthMonitor.Native;

/// <summary>
/// Windows 文件系统原生能力封装：
/// 1）读取文件的唯一标识（卷序列号 + 文件索引）与硬链接数，用于硬链接去重；
/// 2）探测卷是否支持硬链接，不支持则整体跳过去重以节省开销。
/// </summary>
internal static class FileSystemNative
{
    // ---- CreateFileW 参数 ----
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    /// <summary>打开目录、以及打开重解析点自身（而非其目标）所必需。</summary>
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    /// <summary>卷支持硬链接的标志位（GetVolumeInformation 的 lpFileSystemFlags）。</summary>
    private const uint FILE_SUPPORTS_HARD_LINKS = 0x00400000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
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

    /// <summary>GetFinalPathNameByHandleW 的 dwFlags：规范化 + 使用 DOS 驱动器路径（\\?\C:\...）。</summary>
    private const uint FILE_NAME_NORMALIZED = 0x0;
    private const uint VOLUME_NAME_DOS = 0x0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    /// <summary>
    /// 解析路径上所有 junction / 符号链接，返回最终真实路径（\\?\ 前缀形式）。
    /// 等价于 .NET 6+ 的 Directory.ResolveLinkTarget(returnFinalTarget: true)，
    /// 但一次系统调用即解析到底，且在 .NET Framework 4.5 上同样可用。
    /// </summary>
    public static bool TryGetFinalPath(string path, out string finalPath)
    {
        finalPath = string.Empty;

        // dwDesiredAccess = 0：只查询元数据，权限要求最低
        using var handle = CreateFileW(
            path,
            0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,   // 打开目录所必需
            IntPtr.Zero);

        if (handle.IsInvalid) return false;

        var buf = new char[32768];
        uint n = GetFinalPathNameByHandleW(handle, buf, (uint)buf.Length, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);

        // 返回值是写入的字符数（不含结尾 NUL）；0 或超出缓冲即视为失败
        if (n == 0 || n >= buf.Length) return false;

        finalPath = new string(buf, 0, (int)n);
        return true;
    }

    /// <summary>
    /// 路径**实际所在卷**的盘符（先解析 junction / 符号链接 / 挂载点，再取盘符）。
    ///
    /// 为什么需要它：扫描根可能位于 junction 之后，此时「路径里的盘符」并不等于
    /// 「数据真正所在的卷」。按路径盘符去读 USN 变更日志就会读到**别的卷**，变更集
    /// 恒为空 ⇒ 增量路径判「无变更」并复用过期快照（静默错报，比直接失败难查得多）。
    ///
    /// 返回 false 的情形（调用方必须保守处理，即放弃增量）：路径不存在 / 无权限 /
    /// 解析到 <c>\\?\Volume{GUID}\</c> 或 <c>\\?\UNC\</c> 这类没有盘符的形式。
    /// </summary>
    public static bool TryGetRealVolumeLetter(string path, out char letter)
    {
        letter = '\0';

        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        if (!TryGetFinalPath(full, out string real)) return false;

        // GetFinalPathNameByHandleW 在 VOLUME_NAME_DOS 下返回 "\\?\C:\…"；
        // 无盘符的卷是 "\\?\Volume{…}\…"、UNC 是 "\\?\UNC\server\share\…" ⇒ 后两者判为不可用。
        const string devicePrefix = @"\\?\";
        if (!real.StartsWith(devicePrefix, StringComparison.Ordinal)) return false;

        int i = devicePrefix.Length;
        if (real.Length < i + 2 || real[i + 1] != ':') return false;

        letter = char.ToUpperInvariant(real[i]);
        return letter >= 'A' && letter <= 'Z';
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetVolumeInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    /// <summary>
    /// 尝试读取文件唯一标识。失败时不抛异常，返回 false 并由调用方按「计入」处理。
    /// </summary>
    /// <param name="filePath">文件完整路径。</param>
    /// <param name="volumeSerial">卷序列号。</param>
    /// <param name="fileIndex">文件索引（高 32 位 + 低 32 位合成 64 位）。</param>
    /// <param name="linkCount">该文件的硬链接总数。</param>
    public static bool TryGetFileId(string filePath, out uint volumeSerial, out ulong fileIndex, out uint linkCount)
    {
        volumeSerial = 0;
        fileIndex = 0;
        linkCount = 0;

        // FILE_FLAG_OPEN_REPARSE_POINT：符号链接文件按其自身而非目标读取标识。
        // FILE_READ_ATTRIBUTES 权限最低，最不容易触发拒绝访问。
        using var handle = CreateFileW(
            filePath,
            FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (handle.IsInvalid) return false;
        if (!GetFileInformationByHandle(handle, out var info)) return false;

        volumeSerial = info.dwVolumeSerialNumber;
        fileIndex = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;
        linkCount = info.nNumberOfLinks;
        return true;
    }

    /// <summary>
    /// FRN / FileId 中**记录号**所占的低 48 位掩码；高 16 位是序列号
    /// （NTFS 磁盘格式规定，不是 API 契约 —— 换文件系统即不成立，故只用于「同一卷内」的判定）。
    /// </summary>
    public const ulong FileIdRecordNumberMask = 0xFFFFFFFFFFFFUL;

    /// <summary>NTFS 卷根（$Root）的 MFT 记录号。属**格式不变量**：只能用于兜底判定，
    /// 不能当作「取值来源」——真正的卷根 FRN 应问 <see cref="TryGetRootFileId"/>。</summary>
    private const ulong VolumeRootRecordNumber = 5;

    /// <summary>
    /// 判断某个 FRN/FileId 是否指向**卷根**（只比记录号，忽略序列号）。
    ///
    /// 用途：MFT 枚举不返回记录 0..26，卷根本就查不到，只能靠这个不变量认出「链已经走到根」。
    /// ⚠ 依赖「卷根 = 记录 5」这一 NTFS 格式细节：换成 ReFS（128 位 ID）即失效，
    /// 所以调用方必须优先使用从 API 取得的卷根 FRN 做**精确比较**，本判定只作最后兜底。
    /// </summary>
    public static bool IsVolumeRootFileId(ulong fileId)
        => (fileId & FileIdRecordNumberMask) == VolumeRootRecordNumber;

    /// <summary>
    /// 取卷根目录的 FileId（即 FRN），**直接问系统**而不是假定「记录 5」。
    ///
    /// 实现：CreateFileW("X:\") + GetFileInformationByHandle，取 nFileIndexHigh/Low。
    /// 这是唯一**权威**的卷根 FRN 来源：不依赖任何硬编码记录号，换文件系统/换机器都成立。
    /// 实测（2026-09-24）：C: / D: / E: 三卷均得 0x0005000000000005，
    /// 与「反查根级子目录 Parent」的结果**逐位相同**，与「记录号 5」也一致。
    /// </summary>
    public static bool TryGetRootFileId(char letter, out ulong fileId)
    {
        fileId = 0;
        try
        {
            using var handle = CreateFileW(
                letter + ":\\",
                FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS,     // 打开目录所必需
                IntPtr.Zero);

            if (handle.IsInvalid) return false;
            if (!GetFileInformationByHandle(handle, out var info)) return false;

            fileId = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;
            return fileId != 0;                 // 0 会与「未知」混淆，按失败处理
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从「枚举到的子项 Parent 列表」反查卷根 FRN（<see cref="TryGetRootFileId"/> 的兜底）。
    /// 根级子项的 Parent 字段就是完整的卷根 FRN，取记录号命中 <see cref="VolumeRootRecordNumber"/>
    /// 的那个（多个候选时取最小者，保证结果确定）。
    ///
    /// ⚠ **找不到必须返回 false，绝不返回常量**：曾经这里 fallback 返回裸的 5，
    /// 于是 ResolvePath("C:\") 得到 5、children[5] 恒为空、子树只数到自己 = 1，
    /// 而调用方以「> 0」判定成功 ⇒ 分母变成 1，进度条瞬间满格却全是错的。
    /// 宁可失败让上层降级，也不要给出一个「看起来合理」的错误数字。
    /// </summary>
    public static bool TryFindRootFileIdFromParents(IEnumerable<ulong> parents, out ulong fileId)
    {
        fileId = 0;
        if (parents == null) return false;

        foreach (var parent in parents)
        {
            if (!IsVolumeRootFileId(parent)) continue;
            if (fileId == 0 || parent < fileId) fileId = parent;
        }
        return fileId != 0;
    }

    private static readonly Dictionary<string, bool> HardLinkSupportCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SyncLock CacheLock = new();

    /// <summary>
    /// 判断指定根（盘符或目录）所在卷是否支持硬链接。结果按盘符缓存。
    /// FAT32 / exFAT 等不支持硬链接的卷返回 false，可据此完全跳过逐文件句柄开销。
    /// </summary>
    public static bool VolumeSupportsHardLinks(string rootKeyOrPath)
    {
        string? drive = ResolveDriveLetter(rootKeyOrPath);
        if (drive == null) return true; // 无法判定时保守地认为支持

        lock (CacheLock)
        {
            if (HardLinkSupportCache.TryGetValue(drive, out bool cached))
                return cached;
        }

        bool supports = true;
        try
        {
            // 注意卷根路径必须形如 "C:\"（盘符 + 冒号 + 反斜杠），
            // 否则 GetVolumeInformationW 会以 ERROR_FILE_NOT_FOUND 失败，
            // 导致所有卷都被误判为「不支持硬链接」而静默关闭去重。
            string volumeRoot = drive + ":\\";
            bool ok = GetVolumeInformationW(volumeRoot, null, 0, out _, out _, out uint flags, null, 0);
            if (ok)
                supports = (flags & FILE_SUPPORTS_HARD_LINKS) != 0;
            // 探测失败时保持 true（保守开启去重），统计正确性优先于性能
        }
        catch
        {
            supports = true;
        }

        lock (CacheLock)
        {
            HardLinkSupportCache[drive] = supports;
        }
        return supports;
    }

    private static readonly Dictionary<string, uint> VolumeSerialCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 取卷序列号（用于硬链接去重集合的**卷隔离键**）。失败返回 false。
    ///
    /// 为什么隔离键必须是卷序列号，而不是盘符字母：目录项里只有 FileId、不带卷信息，
    /// 而各卷的 MFT 记录号各自独立（每个卷的根目录都是记录 5、$MFT 都是 0），
    /// 混在一起会静默漏去重。而**同一物理卷可以挂多个盘符/挂载点** ——
    /// 按盘符字母分组会把同一卷拆成两组，同样漏去重；卷序列号天然把同一物理卷合并。
    /// 只有读取失败才返回 false（调用方据此退回逐文件句柄口径，语义不退化）。
    /// </summary>
    public static bool TryGetVolumeSerial(string rootKeyOrPath, out uint serial)
    {
        serial = 0;
        string? drive = ResolveDriveLetter(rootKeyOrPath);
        if (drive == null) return false;

        lock (CacheLock)
        {
            if (VolumeSerialCache.TryGetValue(drive, out uint cached))
            {
                serial = cached;
                return true;
            }
        }

        uint value = 0;
        try
        {
            // 卷根路径必须形如 "C:\"（盘符 + 冒号 + 反斜杠），否则 GetVolumeInformationW
            // 会以 ERROR_FILE_NOT_FOUND 失败，导致所有卷都拿不到序列号而整体退回去重慢路径。
            GetVolumeInformationW(drive + ":\\", null, 0, out value, out _, out _, null, 0);
        }
        catch
        {
            value = 0;
        }

        if (value == 0) return false;   // 序列号 0 会与「未知」混淆，按失败处理；失败结果不进缓存

        lock (CacheLock)
        {
            VolumeSerialCache[drive] = value;
        }
        serial = value;
        return true;
    }

    // ------------------------- USN 变更日志 / MFT 枚举（增量扫描用） -------------------------
    // 注意：打开卷设备（\\.\C:）并读取 USN 日志需要管理员权限。

    private const uint GENERIC_READ = 0x80000000;
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    private const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    private const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    /// <summary>USN 记录原因：文件关闭（单独出现时表示无实质变更）。</summary>
    public const uint USN_REASON_CLOSE = 0x80000000;
    /// <summary>文件属性：目录。</summary>
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    /// <summary>DeviceIoControl 成功码：已到枚举/日志末尾（正常收尾，不是故障）。</summary>
    private const int ErrorHandleEof = 38;
    /// <summary>DeviceIoControl 参数错误：输入结构尺寸或字段被驱动拒绝。</summary>
    private const int ErrorInvalidParameter = 87;
    /// <summary>内部状态：枚举批次超过上限（防御设备异常导致的不收敛）。</summary>
    private const int ErrorBatchLimit = -3;
    /// <summary>枚举批次上限。正常全盘枚举在百批量级，百万批仍未结束必然是设备/参数异常。</summary>
    private const long MaxEnumBatches = 1_000_000;

    /// <summary>本工具能够解析的 USN 记录主版本（USN_RECORD_V2）。</summary>
    private const ushort RequiredRecordMajorVersion = 2;
    /// <summary>
    /// 已知的 USN 记录主版本区间（V2 / V3 / V4）。
    /// ⚠ 只用于**结构自证**，不代表能解析：V3（ReFS 的 128 位文件 ID）与 V4（无文件名）
    /// 的字段偏移与 V2 不同，本工具一律**检出并拒绝**，绝不按 V2 硬解。
    /// </summary>
    private const ushort MinKnownUsnMajorVersion = 2;
    private const ushort MaxKnownUsnMajorVersion = 4;
    /// <summary>USN_RECORD_V2 固定头长度（文件名紧跟在头之后，故也是最小记录长度）。</summary>
    private const int UsnRecordV2HeaderSize = 60;

    /// <summary>
    /// FSCTL_READ_USN_JOURNAL 与 FSCTL_ENUM_USN_DATA 输出缓冲的**公共头部长度**：两者遵循同一约定 ——
    /// 缓冲首 8 字节是「下一轮起始位置」（前者是起始 USN，后者是起始 FRN），记录紧随其后。
    /// 官方文档 FSCTL_ENUM_USN_DATA 原文：*"retrieves the starting point for the subsequent call
    /// as the first entry in the output buffer"* —— 属**跨卷/跨设备不变的契约**，可以硬编码。
    /// </summary>
    private const int UsnOutputHeaderSize = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct READ_USN_JOURNAL_DATA_V1
    {
        public long StartUsn;
        public uint ReasonMask;          // 0xFFFFFFFF = 全部原因
        public uint ReturnOnlyOnClose;   // 0
        public uint Timeout;             // 0 = 立即返回
        public ulong BytesToWaitFor;     // 0
        public ulong UsnJournalID;
        public ushort MinMajorVersion;   // 2
        public ushort MaxMajorVersion;   // 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_ENUM_DATA_V0
    {
        public long StartFileReferenceNumber;
        public long LowUsn;    // USN 过滤下界，0 = 不过滤
        public long HighUsn;   // USN 过滤上界，必须传 MaxUsn（见该常量的说明）。
                               // ⚠ 不是「0 = 不过滤」：它是有符号上界，
                               // 传 0 会排除全部记录、枚举瞬间 EOF「成功」返回空；
                               // 传 -1（全 1）同样是负上界，症状一样。
                               // 三者共 24 字节，只传 8 字节会收到 ERROR_INVALID_PARAMETER(87)。
    }

    /// <summary>
    /// MFT_ENUM_DATA_V1（28 字节有效字段，本机实测驱动按自然对齐接受 32 字节）。
    ///
    /// 唯一存在的理由：**把记录版本钉死**。V0 不携带版本字段，版本由驱动自行决定——
    /// 2026-09-24 实测：同一卷上 min=max=2 得到 9755 条 V2、min=max=3 得到 8522 条 V3
    /// （128 位文件 ID），而 V2/V3 的字段偏移完全不同。V0 等于把「拿到哪种记录」交给驱动，
    /// 一旦驱动改了默认值，解析端会静默失效。
    ///
    /// ⚠ 兼容性与实测结论（本机 Win11 26200 / NTFS / 管理员，见 _optdb/mftprobe）：
    ///   · Min=Max=2 → 正常返回 V2（与 V0 结果逐字节相同：head8=0x29C8、9755 条）；
    ///   · 28 字节与 32 字节两种尺寸都被接受；
    ///   · Min=Max=0 **被拒绝**（ERROR_INVALID_PARAMETER(87)）——「0 = 不过滤」的说法不成立；
    ///   · Min=1/Max=4 与 Min=Max=3 都返回 V3（本工具不解析 ⇒ 由调用方回退）。
    ///   结构本身是 Win8 / WS2012 起才认识，更早系统会以 87 拒绝 ⇒ 调用方必须能退回 V0。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_ENUM_DATA_V1
    {
        public long StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
        public ushort MinMajorVersion;
        public ushort MaxMajorVersion;
    }

    /// <summary>MFT 枚举的终止/结果状态。把「0 条记录」拆开：它既可能是真没目录，
    /// 也可能是**内容不认识**——后者若被当成「空」会造成静默错数（分母 1、误判无变更）。</summary>
    public enum MftEnumOutcome
    {
        /// <summary>正常走到枚举末尾（可能确实 0 条：该卷除元数据外没有目录）。</summary>
        Completed = 0,
        /// <summary>设备返回了字节，但记录布局与版本都识别不出（如 ReFS/V3 的 128 位记录）。</summary>
        UnrecognizedRecord,
        /// <summary>DeviceIoControl 失败（错误码见 lastError）。</summary>
        DeviceError,
        /// <summary>批次超限，已中止（lastError = ErrorBatchLimit）。</summary>
        BatchLimit
    }

    /// <summary>
    /// MFT 枚举的自证/诊断信息。**不参与判定**，只为排障提供「当时到底发生了什么」：
    /// 缓冲布局是自证出来的还是自测出来的、记录是什么版本、输入结构用没用上 V1。
    /// </summary>
    public readonly record struct MftEnumInfo(
        MftEnumOutcome Outcome,
        int LayoutOffset,
        ushort RecordMajor,
        bool UsedV1,
        int V1Error)
    {
        /// <summary>一句话描述（供 --dev 与日志输出）。</summary>
        public string Describe()
        {
            // 只有真的取到过一条可识别记录（RecordMajor != 0）自证才算完成，
            // 否则不能声称「布局已确认」—— 那正是过去误导读者的老毛病。
            string layout = RecordMajor != 0
                ? (LayoutOffset == UsnOutputHeaderSize
                    ? Lang.F("缓冲布局=头在开头(记录起于偏移 {0})",
                             "buffer layout = header first (records start at offset {0})", UsnOutputHeaderSize)
                    : Lang.F("缓冲布局=记录紧贴首部(偏移 {0}，偏离文档契约)",
                             "buffer layout = records at the very start (offset {0}; deviates from the documented contract)",
                             LayoutOffset))
                : Lang.T("缓冲布局=未判定(未取到可识别的记录)",
                         "buffer layout = undetermined (no recognizable record obtained)");
            string version = RecordMajor == 0
                ? Lang.T("记录版本=未知", "record version = unknown")
                : Lang.F("记录版本=USN_RECORD_V{0}", "record version = USN_RECORD_V{0}", RecordMajor);
            string input = UsedV1
                ? Lang.T("输入结构=MFT_ENUM_DATA_V1(min=max=2)", "input struct = MFT_ENUM_DATA_V1(min=max=2)")
                : Lang.F("输入结构=MFT_ENUM_DATA_V0(V1 不可用：Win32 {0})",
                         "input struct = MFT_ENUM_DATA_V0(V1 unavailable: Win32 {0})", V1Error);
            string outcome = Outcome switch
            {
                MftEnumOutcome.Completed => Lang.T("枚举正常结束", "enumeration completed normally"),
                MftEnumOutcome.UnrecognizedRecord =>
                    Lang.T("记录版本无法识别（仅有非 V2 记录）",
                           "unrecognized record version (only non-V2 records present)"),
                MftEnumOutcome.DeviceError => Lang.T("设备调用失败", "device call failed"),
                MftEnumOutcome.BatchLimit => Lang.T("批次超限中止", "aborted: batch limit exceeded"),
                _ => Lang.T("未知状态", "unknown state")
            };
            return string.Join(Lang.T("；", "; "), new[] { outcome, layout, version, input });
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>输出缓冲为数组的重载：封送器在调用期间自动钉住数组，无需 fixed/unsafe。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize,
        byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>USN 变更记录（由 USN_RECORD_V2 解析而来）。</summary>
    public readonly record struct UsnRecord(
        ulong FileReferenceNumber,
        ulong ParentReferenceNumber,
        long Usn,
        uint Reason,
        uint FileAttributes,
        string Name);

    /// <summary>
    /// 一批 USN 记录的附带信息：本批中**结构完整但没有被采纳**的其他版本记录。
    /// 用途：把「区间内真的没有记录」与「有记录但版本不认识」区分开 —— 后者若被当成
    /// 「无变更」，会静默复用上一轮快照（错报且不留痕迹），比直接失败难查得多。
    /// </summary>
    public readonly record struct UsnBatchInfo(ushort ForeignMajor, int ForeignCount)
    {
        /// <summary>本批存在无法解析的其他版本记录。</summary>
        public bool HasForeignRecords => ForeignCount > 0;
    }

    /// <summary>卷的 USN 日志查询结果。</summary>
    public sealed record VolumeJournalInfo(ulong JournalId, long FirstUsn, long NextUsn);

    /// <summary>打开卷设备（\\.\C: 形式，需管理员权限）。失败返回 null。</summary>
    public static SafeFileHandle? TryOpenVolume(char letter, out int lastError)
    {
        var h = CreateFileW(
            $"\\\\.\\{letter}:",
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);
        lastError = Marshal.GetLastWin32Error();
        return h.IsInvalid ? null : h;
    }

    /// <summary>查询卷的 USN 日志信息（日志标识、有效范围、当前写入位置）。</summary>
    public static bool TryQueryJournal(SafeFileHandle volume, out VolumeJournalInfo? info, out int lastError)
    {
        info = null;
        int inSize = Marshal.SizeOf(typeof(USN_JOURNAL_DATA_V0));
        IntPtr outBuf = Marshal.AllocHGlobal(64);
        try
        {
            bool ok = DeviceIoControl(volume, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0,
                outBuf, 64, out int returned, IntPtr.Zero);
            lastError = Marshal.GetLastWin32Error();
            if (!ok || returned < inSize) return false;

            var data = PlatformCompat.PtrToStructure<USN_JOURNAL_DATA_V0>(outBuf);
            info = new VolumeJournalInfo(data.UsnJournalID, data.FirstUsn, data.NextUsn);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(outBuf);
        }
    }

    /// <summary>
    /// 从 startUsn 起读取一批 USN 记录。返回解析出的记录；startUsn 原地推进到本批最后一条的位置。
    /// 到达日志末尾后返回空列表。
    ///
    /// batchInfo 报告「本批有几条结构完整但版本不认识」——调用方**必须**据此把
    /// 「真的没有记录」与「有记录但解不了」区别对待，否则会错报「无变更」。
    /// </summary>
    public static bool TryReadUsnRecords(
        SafeFileHandle volume,
        ulong journalId,
        ref long startUsn,
        byte[] buffer,
        List<UsnRecord> records,
        out int lastError,
        out UsnBatchInfo batchInfo)
    {
        records.Clear();
        lastError = 0;
        batchInfo = default;

        var inData = new READ_USN_JOURNAL_DATA_V1
        {
            StartUsn = startUsn,
            ReasonMask = 0xFFFFFFFF,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalID = journalId,
            MinMajorVersion = RequiredRecordMajorVersion,
            MaxMajorVersion = RequiredRecordMajorVersion
        };

        IntPtr inBuf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(READ_USN_JOURNAL_DATA_V1)));
        try
        {
            Marshal.StructureToPtr(inData, inBuf, false);
            bool ok = DeviceIoControl(volume, FSCTL_READ_USN_JOURNAL,
                inBuf, Marshal.SizeOf(typeof(READ_USN_JOURNAL_DATA_V1)),
                buffer, buffer.Length, out int returned, IntPtr.Zero);
            lastError = Marshal.GetLastWin32Error();
            if (!ok) return false;

            // FSCTL_READ_USN_JOURNAL 的输出缓冲：前 8 字节是「下次读取起始 USN」头部，
            // 之后才是 USN_RECORD_V2 记录（与 MFT 枚举同一约定，见 UsnOutputHeaderSize）。
            if (returned <= UsnOutputHeaderSize) return true; // 只有头部或空 = 无新记录

            // 头部值是文档规定的权威续读位置：无论本批解析出多少条（含截断/为空），
            // 直接采用它作为下次 StartUsn，天然规避重读与死循环。
            long nextUsn = BitConverter.ToInt64(buffer, 0);
            ParseUsnRecordBuffer(buffer, UsnOutputHeaderSize, returned - UsnOutputHeaderSize, records,
                out int foreignCount, out ushort foreignMajor);
            batchInfo = new UsnBatchInfo(foreignMajor, foreignCount);
            startUsn = nextUsn;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
        }
    }

    /// <summary>解析一段 USN 记录缓冲（journal 与 MFT 枚举共用同一 USN_RECORD_V2 布局）。
    /// 严格边界检查：任何越界/截断/非法记录一律跳过或终止本批解析，绝不抛异常。
    /// foreignCount/foreignMajor 统计「结构完整、但主版本不是 V2」的记录（如 V3/V4），
    /// 它们必须被计数而不是被当作解析失败。</summary>
    private static void ParseUsnRecordBuffer(byte[] buffer, int start, int length, List<UsnRecord> records,
        out int foreignCount, out ushort foreignMajor)
    {
        foreignCount = 0;
        foreignMajor = 0;
        int offset = 0;
        while (offset + 4 <= length)
        {
            uint recLen = BitConverter.ToUInt32(buffer, start + offset);
            if (recLen == 0) break;

            // USN_RECORD_V2 头固定 60 字节；记录必须完整落在缓冲内。
            // 截断时终止本批解析（startUsn 保持上一条记录位置，下一轮读取会重取），绝不越界切片。
            if (recLen < UsnRecordV2HeaderSize || offset + (long)recLen > length)
                break;

            int len = (int)recLen;
            ushort major = BitConverter.ToUInt16(buffer, start + offset + 4);
            if (major != RequiredRecordMajorVersion)
            {
                // 不是 V2：只计数、跳过，**绝不**按 V2 的偏移去解 ——
                // V3 的 128 位文件 ID 会让 52/56/58 三个偏移落到别的字段上，
                // 解出来的「路径」看似合理却是垃圾，比直接失败危险得多。
                foreignCount++;
                if (foreignMajor == 0) foreignMajor = major;
                offset += len;
                continue;
            }

            ulong frn = BitConverter.ToUInt64(buffer, start + offset + 8);
            ulong parent = BitConverter.ToUInt64(buffer, start + offset + 16);
            long usn = BitConverter.ToInt64(buffer, start + offset + 24);
            uint reason = BitConverter.ToUInt32(buffer, start + offset + 40);
            uint attrs = BitConverter.ToUInt32(buffer, start + offset + 52);
            ushort nameLen = BitConverter.ToUInt16(buffer, start + offset + 56);
            ushort nameOff = BitConverter.ToUInt16(buffer, start + offset + 58);

            string name = string.Empty;
            if (nameLen > 0 && nameOff + (long)nameLen <= len && offset + nameOff + nameLen <= length)
                name = Encoding.Unicode.GetString(buffer, start + offset + nameOff, nameLen);

            records.Add(new UsnRecord(frn, parent, usn, reason, attrs, name));
            offset += len;
        }
    }

    /// <summary>
    /// MFT_ENUM_DATA_V0.HighUsn 的上界。
    ///
    /// 必须给 <see cref="long.MaxValue"/>（即 MSDN 示例里的 MAXLONGLONG），<b>不能给 0</b>：
    /// 该字段是「只返回 USN ≤ 此值 的记录」的有符号上界，而真实记录的 USN 恒为正数，
    /// 传 0 的结果是**一条都取不到** —— DeviceIoControl 返回 ERROR_HANDLE_EOF(38)，
    /// 函数照常返回 true 但 recordCount = 0。
    ///
    /// ⚠ 归因更正（2026-09-24，已实测）：本参数**不是**「无法枚举 MFT」事故的根因 ——
    /// 当时它已经是 MaxUsn，事故真因是<b>输出缓冲布局读错</b>（见 <see cref="UsnOutputHeaderSize"/>
    /// 与卷根 FRN 的说明）。此处保留该常量是因为传 0 / 传 -1 确实会造成同样的
    /// 「成功却 0 条记录」假象，属必须堵住的第二条通道，而非历史根因。
    /// 真正的教训是：三种原因（参数错 / 布局错 / 权限不足）症状完全相同，
    /// 所以诊断文案必须报告「断在哪一步」+ 真实错误码，不能把某一原因硬编码进提示。
    /// </summary>
    private const long MaxUsn = long.MaxValue;

    /// <summary>
    /// 枚举整卷 MFT 记录。每条记录回调 (FRN, 父FRN, 属性, 名称)；回调返回 false 提前终止。
    /// recordCount 返回实际枚举到的记录总数；lastError 为最后一次 DeviceIoControl 的 Win32 错误码。
    ///
    /// ⚠ 返回 true 只表示「设备层没有故障」，**不表示内容可用**：务必检查
    /// <paramref name="info"/> 的 Outcome —— 尤其 UnrecognizedRecord（设备正常返回了字节，
    /// 但记录不是 V2，一条都解不出）。把它当成「空」，会让调用方得到静默错误的结论
    /// （分母算成 1、或把「有变更」判成「无变更」而复用过期快照）。
    ///
    /// 注意：枚举**不返回 MFT 记录 0..26**（$MFT/$LogFile/$Root 等元数据文件），
    /// 所以「卷根」本身不会出现在结果里；根级子目录的 Parent 字段是完整 64 位 FRN
    /// （实测 0x0005000000000005，即序列号 5 + 记录号 5），不是裸的 5。
    /// 需要卷根本身的 FRN 时请用 <see cref="TryGetRootFileId"/>（问 API，不猜记录号）。
    /// </summary>
    public static bool TryEnumMftRecords(
        SafeFileHandle volume,
        Func<ulong, ulong, uint, string, bool> onRecord,
        out long recordCount,
        out int lastError,
        out MftEnumInfo info)
    {
        recordCount = 0;
        lastError = 0;
        info = default;

        var buffer = new byte[1024 * 1024];
        var records = new List<UsnRecord>(4096);

        // 输入结构：先用 V1（Min=Max=2，把记录版本钉死），被拒则退回 V0。
        bool useV1 = true;
        int v1Error = 0;
        int v1Size = Marshal.SizeOf(typeof(MFT_ENUM_DATA_V1));
        int v0Size = Marshal.SizeOf(typeof(MFT_ENUM_DATA_V0));

        // 记录起始偏移：由首批缓冲运行时自证（方案 B），不再依赖「头在开头」这一文档假设
        int layoutOffset = UsnOutputHeaderSize;
        bool layoutDecided = false;
        ushort observedMajor = 0;
        MftEnumOutcome outcome = MftEnumOutcome.Completed;

        IntPtr inBuf = Marshal.AllocHGlobal(v1Size);
        try
        {
            long startFrn = 0;
            long batches = 0;
            WriteMftEnumInput(inBuf, useV1, startFrn);

            while (true)
            {
                if (++batches > MaxEnumBatches)
                {
                    lastError = ErrorBatchLimit;
                    outcome = MftEnumOutcome.BatchLimit;
                    return false;
                }

                bool ok = DeviceIoControl(volume, FSCTL_ENUM_USN_DATA,
                    inBuf, useV1 ? v1Size : v0Size,
                    buffer, buffer.Length, out int returned, IntPtr.Zero);
                int err = Marshal.GetLastWin32Error();

                // V1 只在**首批**失败时回退：V1 是 Win8 / WS2012 起才认识的结构，
                // 更早的系统会以 ERROR_INVALID_PARAMETER(87) 拒绝。批次 >1 的失败与结构
                // 版本无关，若也回退只会把已解析的记录重读一遍（并重复回调）。
                if (!ok && useV1 && batches == 1)
                {
                    v1Error = err;
                    useV1 = false;
                    batches = 0;          // 探测失败不消耗批次配额
                    startFrn = 0;         // 尚未产出任何记录，可以安全从头再来
                    WriteMftEnumInput(inBuf, false, startFrn);
                    continue;
                }

                lastError = err;
                if (!ok)
                {
                    // ERROR_HANDLE_EOF(38) 表示枚举完成，是正常收尾而非错误：
                    // 归一化错误码为 0，否则调用方会把「枚举结束」误读成设备故障
                    if (err == ErrorHandleEof) { lastError = 0; return true; }
                    outcome = MftEnumOutcome.DeviceError;
                    return false;
                }

                // 只有头部 = 本批没有记录（一条记录至少 60 字节）
                if (returned <= UsnOutputHeaderSize) { lastError = 0; return true; }

                if (!layoutDecided)
                {
                    layoutDecided = true;
                    if (!TryDecideLayout(buffer, returned, out layoutOffset, out observedMajor))
                    {
                        // 有字节返回，但两种候选偏移都走不通 ⇒ 不是设备故障，是**内容不认识**
                        // （例如 ReFS 的 USN_RECORD_V3：128 位文件 ID，字段偏移与 V2 不同）。
                        // 必须显式报出，绝不能让调用方把「0 条」当作「这个卷没有目录」。
                        lastError = 0;
                        outcome = MftEnumOutcome.UnrecognizedRecord;
                        return true;
                    }
                }

                long prevFrn = startFrn;
                records.Clear();
                ParseUsnRecordBuffer(buffer, layoutOffset, returned - layoutOffset, records,
                    out int foreignCount, out ushort foreignMajor);

                if (foreignCount > 0)
                {
                    // 出现结构完整、但主版本不是 V2 的记录 ⇒ 驱动没有按 Min=Max=2 只给 V2。
                    // 继续按 V2 解析会把它们**静默丢掉**（少算目录 / 漏判变更），
                    // 所以这里明确报「版本不认识」，交由调用方回退，绝不悄悄少算。
                    // 结构自证已确认链式合法，因此这不可能是「缓冲被读乱」。
                    if (foreignMajor != 0) observedMajor = foreignMajor;
                    lastError = 0;
                    outcome = MftEnumOutcome.UnrecognizedRecord;
                    return true;
                }
                recordCount += records.Count;

                // 先把本批交给调用方，再决定是否继续 —— 这样即使续读点异常
                // （布局偏离契约 / 设备不再前移），这一批也不会白解析
                foreach (var r in records)
                    if (!onRecord(r.FileReferenceNumber, r.ParentReferenceNumber, r.FileAttributes, r.Name))
                        return true;

                // 续读位置：缓冲首 8 字节（文档契约，且已被自证选中）。
                // 若自证选中的是「记录紧贴首部」的旧假设，则尾部指针位置无从得知 ——
                // 此时不猜：按「无进展」正常收尾，已解析的记录照常返回。
                if (layoutOffset < UsnOutputHeaderSize || returned < UsnOutputHeaderSize)
                {
                    lastError = 0;
                    return true;
                }
                startFrn = BitConverter.ToInt64(buffer, 0);

                // 无进展保护：下一轮起始 FRN 未前移说明枚举已到末尾（或参数异常），
                // 绝不能让循环空转——缺失这道保护会表现为调用方永久挂起
                if (startFrn <= prevFrn) { lastError = 0; return true; }

                WriteMftEnumInput(inBuf, useV1, startFrn);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            info = new MftEnumInfo(outcome, layoutOffset, observedMajor, useV1, v1Error);
        }
    }

    /// <summary>按选定的输入结构写入枚举参数（startFrn = 下一轮的起始 FRN）。</summary>
    private static void WriteMftEnumInput(IntPtr buf, bool useV1, long startFrn)
    {
        if (useV1)
        {
            Marshal.StructureToPtr(new MFT_ENUM_DATA_V1
            {
                StartFileReferenceNumber = startFrn,
                LowUsn = 0,
                HighUsn = MaxUsn,
                MinMajorVersion = RequiredRecordMajorVersion,
                MaxMajorVersion = RequiredRecordMajorVersion
            }, buf, false);
        }
        else
        {
            Marshal.StructureToPtr(new MFT_ENUM_DATA_V0
            {
                StartFileReferenceNumber = startFrn,
                LowUsn = 0,
                HighUsn = MaxUsn
            }, buf, false);
        }
    }

    /// <summary>
    /// 运行时判定输出缓冲里记录的起始偏移（方案 B）。**不依赖任何文档假设**。
    ///
    /// 判据是「把缓冲按记录长度链式走一遍能否走得通」：USN 记录的长度必为 8 的倍数、
    /// 必 ≥ 60（V2 头长）、必不越过缓冲末尾、主版本必在已知区间 [2,4]，且走完后**正好**
    /// 落在缓冲末尾（容差 &lt; 8 字节）。只有完整、无洞、无越界的一条记录序列才能同时满足。
    ///
    /// 先试 <see cref="UsnOutputHeaderSize"/>（文档契约：头在开头），不通过再试 0
    /// （旧代码的假设：记录紧贴首部）。两者都不通过即判「无法识别的记录版本」——
    /// 这个分支正好覆盖 ReFS / 高版本记录（V3 的 128 位 ID 会让 V2 的字段偏移全部错位）。
    ///
    /// 实测（2026-09-24，Win11 26200 / NTFS，见 _optdb/mftprobe）：
    ///   offset 8 → valid=True records=9755 major=2 consumedTo=1048552/1048552；
    ///   offset 0 → valid=False records=0 major=0（那里是 8 字节头，被当作记录后
    ///   RecordLength=10697 不是 8 的倍数 ⇒ 立即判否 —— 这正是当年「0 条记录」的现场指纹）。
    /// </summary>
    private static bool TryDecideLayout(byte[] buffer, int returned, out int offset, out ushort major)
    {
        if (TryWalkRecordChain(buffer, UsnOutputHeaderSize, returned, out _, out major))
        {
            offset = UsnOutputHeaderSize;
            return true;
        }

        const int RecordsAtBufferStart = 0;
        if (TryWalkRecordChain(buffer, RecordsAtBufferStart, returned, out _, out major))
        {
            offset = RecordsAtBufferStart;
            return true;
        }

        offset = UsnOutputHeaderSize;
        major = 0;
        return false;
    }

    /// <summary>
    /// 沿「记录长度」链式走一遍，判断从 <paramref name="start"/> 起是否是一份完整合法的
    /// USN 记录序列。判据全部来自记录格式本身（与设备、卷、文件系统无关）。
    /// </summary>
    private static bool TryWalkRecordChain(byte[] buffer, int start, int end, out int count, out ushort major)
    {
        count = 0;
        major = 0;
        if (end - start < UsnRecordV2HeaderSize) return false;

        int p = start;
        while (p + UsnRecordV2HeaderSize <= end)
        {
            uint recLen = BitConverter.ToUInt32(buffer, p);
            if (recLen < UsnRecordV2HeaderSize || (recLen & 7) != 0) return false;
            if (p + (long)recLen > end) return false;

            ushort v = BitConverter.ToUInt16(buffer, p + 4);
            if (v < MinKnownUsnMajorVersion || v > MaxKnownUsnMajorVersion) return false;

            if (count == 0) major = v;
            p += (int)recLen;
            count++;
        }

        return count > 0 && end - p < UsnOutputHeaderSize;
    }


    /// <summary>从 "C:" 或 "C:\path\to\dir" 中提取盘符字母（不含冒号），失败返回 null。</summary>
    private static string? ResolveDriveLetter(string rootKeyOrPath)
    {
        if (string.IsNullOrEmpty(rootKeyOrPath)) return null;
        if (rootKeyOrPath.Length >= 2 && rootKeyOrPath[1] == ':')
            return char.ToUpperInvariant(rootKeyOrPath[0]).ToString();
        return null;
    }
}
