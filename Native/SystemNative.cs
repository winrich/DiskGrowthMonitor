using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskGrowthMonitor.Native;

/// <summary>
/// 系统能力探测的原生层（纯 P/Invoke，不含业务判断）。
///
/// 分两组，**权限要求不同**，这是调用方必须先想清楚的事：
///   · 免提权（任何进程都能调）：OS 版本、产品类型（工作站/服务器）、物理核数、物理内存；
///   · 需提权：卷 → 物理磁盘号映射、物理磁盘介质类型（SSD/HDD）。
///     这两个都要打开 <c>\\.\X:</c> 卷设备或 <c>\\.\PhysicalDriveN</c>，
///     非管理员一律 ERROR_ACCESS_DENIED(5) —— 与 USN 日志读取是同一道门槛。
///
/// 所有方法都不抛异常：探测失败返回 false，由调用方决定降级策略。
/// 「探测不到」与「探测到否」必须区分清楚，绝不能把失败当成否定结论。
/// </summary>
internal static class SystemNative
{
    // ================================================================ 1. 操作系统版本

    /// <summary>
    /// RTL_OSVERSIONINFOEXW（ntdll 版本，比 kernel32 的 OSVERSIONINFOEXW 多出 wProductType 等尾部字段，
    /// 但二者布局一致，ntdll 会按本结构体大小填充）。共 284 字节。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RTL_OSVERSIONINFOEXW
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;

        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;   // VER_NT_WORKSTATION=1 / VER_NT_DOMAIN_CONTROLLER=2 / VER_NT_SERVER=3
        public byte wReserved;
    }

    [DllImport("ntdll.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEXW lpVersionInformation);

    /// <summary>
    /// 读取**真实**操作系统版本号与产品类型。
    ///
    /// 为什么用 ntdll 的 RtlGetVersion 而不是 kernel32 的 GetVersionExW 或 Environment.OSVersion：
    /// 后两者自 Windows 8.1 起被「应用程序兼容性清单」劫持 —— 进程没有 manifest 声明支持
    /// Win8.1/Win10 时，它们会把真实版本**谎报**成 6.2.9200（Windows 8）。本工程刻意不引入
    /// manifest（要保证 WS2012 上零前置依赖），所以 Environment.OSVersion 在本机 Win11 上
    /// 会报 6.2 —— 拿它做「按 OS 选策略」必然踩空，还会把 Win10/11 误判成低于
    /// FileIdExtdDirectoryInfo 的可用门槛。RtlGetVersion 不受清单影响，任何进程都能拿到真值。
    ///
    /// 顺带回传 wProductType：该字段同样不受清单影响，是区分工作站/服务器的可靠依据
    /// （比读注册表 ProductName 稳，那玩意在 Win11 上至今仍写着 "Windows 10"）。
    /// </summary>
    /// <returns>false = 取不到（理论上不会发生在 Windows 上），调用方需按「版本未知」保守处理。</returns>
    public static bool TryGetOsVersion(out uint major, out uint minor, out uint build, out byte productType)
    {
        major = 0;
        minor = 0;
        build = 0;
        productType = 0;

        try
        {
            var v = new RTL_OSVERSIONINFOEXW
            {
                dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(RTL_OSVERSIONINFOEXW)),
                szCSDVersion = string.Empty
            };

            if (RtlGetVersion(ref v) != 0) return false;   // NTSTATUS：0 = STATUS_SUCCESS

            major = v.dwMajorVersion;
            minor = v.dwMinorVersion;
            build = v.dwBuildNumber;
            productType = v.wProductType;
            return major != 0;
        }
        catch
        {
            return false;
        }
    }

    // ================================================================ 2. 物理核数

    /// <summary>LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore。</summary>
    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType, IntPtr buffer, ref uint returnedLength);

    /// <summary>
    /// 物理核数（开了超线程或大小核时，每个物理核只算一次）。
    ///
    /// Environment.ProcessorCount 给的是**逻辑**处理器数（本机 8 核 16 线程 → 16），
    /// 而 IO 密集型扫描的合理并行度跟物理核关系更近：超线程对「等磁盘」几乎无增益。
    ///
    /// 每条记录形如 { DWORD Relationship; DWORD Size; union{...} }，步长由各条自带的 Size 给出。
    /// 因此这里用 unsafe 指针按步长前进即可，无需为每个 Relationship 定义对应的 union 结构体
    /// —— 后者要写十几个结构体，且容易因对齐写错而静默读到垃圾值。
    /// </summary>
    /// <returns>失败时回退为逻辑处理器数（宁可高估并发度，也不返回 0 让调用方除零）。</returns>
    public static unsafe int GetPhysicalCoreCount()
    {
        try
        {
            uint len = 0;
            // 首次调用只为取所需缓冲长度，必然失败（ERROR_INSUFFICIENT_BUFFER=122），属预期行为
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len == 0) return Environment.ProcessorCount;

            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len))
                    return Environment.ProcessorCount;

                int cores = 0;
                byte* p = (byte*)buf;
                uint remaining = len;
                while (remaining >= 8)
                {
                    uint relationship = *(uint*)p;
                    uint size = *(uint*)(p + 4);

                    // 步长非法的唯一可能是结构损坏；此时立即收工，绝不让指针越过缓冲末尾
                    if (size < 8 || size > remaining) break;

                    if (relationship == RelationProcessorCore) cores++;
                    p += size;
                    remaining -= size;
                }
                return cores > 0 ? cores : Environment.ProcessorCount;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            return Environment.ProcessorCount;
        }
    }

    // ================================================================ 3. 物理内存

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>物理内存总量（字节）；失败返回 0。</summary>
    public static long GetTotalPhysicalMemory()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            return GlobalMemoryStatusEx(ref m) ? (long)m.ullTotalPhys : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ================================================================ 4. 卷 → 物理磁盘号

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;

    /// <summary>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = CTL_CODE(0x56, 0, METHOD_BUFFERED, FILE_ANY_ACCESS)。</summary>
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize,
        byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>
    /// 盘符 → 物理磁盘号。
    ///
    /// **绝不能假设 C: 就是 PhysicalDrive0** —— 多盘机上系统盘完全可能是 1/2/3（NVMe 与 SATA 混装时
    /// 尤其常见）。猜错会拿另一块盘的介质类型去决定本盘的并行策略，比不探测更糟。
    /// 正解是问 IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS（在 <c>\\.\C:</c> 卷句柄上），
    /// 它由卷管理器直接给出该卷跨越的物理盘号，需管理员权限（与 USN 同一道门槛）。
    ///
    /// 输出缓冲布局：NumberOfDiskExtents(4) + Reserved(4) + Extents[0].DiskNumber(4)，
    /// 其中 DISK_EXTENT 因含两个 LARGE_INTEGER 而 8 字节对齐，故 DiskNumber 位于偏移 8。
    /// 卷跨多块盘（跨区卷/带区卷）时只取第一块 —— 这种布局下「介质类型」本身就无单一答案。
    /// </summary>
    public static bool TryGetPhysicalDiskNumber(char letter, out uint diskNumber, out int lastError)
    {
        diskNumber = 0;
        lastError = 0;

        var buf = new byte[1024];
        try
        {
            using var volume = CreateFileW(
                $"\\\\.\\{letter}:",
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (volume.IsInvalid)
            {
                lastError = Marshal.GetLastWin32Error();
                return false;
            }

            if (!DeviceIoControl(volume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                    IntPtr.Zero, 0, buf, buf.Length, out int returned, IntPtr.Zero))
            {
                lastError = Marshal.GetLastWin32Error();
                return false;
            }

            if (returned < 12) return false;                       // 连一个 extent 都没有
            if (BitConverter.ToUInt32(buf, 0) < 1) return false;   // NumberOfDiskExtents == 0

            diskNumber = BitConverter.ToUInt32(buf, 8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ================================================================ 5. 介质类型（SSD / HDD）

    /// <summary>IOCTL_STORAGE_QUERY_PROPERTY = CTL_CODE(0x2D, 0x0500, METHOD_BUFFERED, FILE_ANY_ACCESS)。</summary>
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    /// <summary>STORAGE_PROPERTY_ID.StorageDeviceSeekPenaltyProperty。</summary>
    private const uint StorageDeviceSeekPenaltyProperty = 7;

    /// <summary>STORAGE_QUERY_TYPE.PropertyStandardQuery。</summary>
    private const uint PropertyStandardQuery = 0;

    /// <summary>输入：STORAGE_PROPERTY_QUERY。AdditionalParameters 为 1 字节，结构体按 4 字节对齐补齐到 12。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    /// <summary>
    /// 物理磁盘是否为「寻道有代价」的介质：true = 机械硬盘，false = 固态（SSD / NVMe）。
    ///
    /// 判据是 StorageDeviceSeekPenaltyProperty（Vista 起的官方介质探测接口），
    /// 由存储驱动栈直接回答，比读注册表、猜型号名、或测一次随机读延迟都可靠。
    /// 打开 <c>\\.\PhysicalDriveN</c> 需管理员权限 —— 这是「介质探测必须放在提权之后」的原因。
    ///
    /// 关于返回值语义：false 有**两种**含义 ——「确定是固态」和「探测失败」。
    /// 调用方必须靠 <paramref name="lastError"/> 区分：为 0 时才是确定结论。
    /// </summary>
    public static bool TryGetSeekPenalty(uint diskNumber, out bool incursSeekPenalty, out int lastError)
    {
        incursSeekPenalty = false;
        lastError = 0;

        var outBuf = new byte[64];
        try
        {
            using var drive = CreateFileW(
                $"\\\\.\\PhysicalDrive{diskNumber}",
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (drive.IsInvalid)
            {
                lastError = Marshal.GetLastWin32Error();
                return false;
            }

            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery
            };

            int inSize = Marshal.SizeOf(typeof(STORAGE_PROPERTY_QUERY));
            IntPtr inBuf = Marshal.AllocHGlobal(inSize);
            try
            {
                Marshal.StructureToPtr(query, inBuf, false);
                if (!DeviceIoControl(drive, IOCTL_STORAGE_QUERY_PROPERTY,
                        inBuf, inSize, outBuf, outBuf.Length, out int returned, IntPtr.Zero))
                {
                    lastError = Marshal.GetLastWin32Error();
                    return false;
                }

                // 输出：DEVICE_SEEK_PENALTY_DESCRIPTOR = Version(4) + Size(4) + IncursSeekPenalty(1)
                if (returned < 9) return false;

                incursSeekPenalty = outBuf[8] != 0;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(inBuf);
            }
        }
        catch
        {
            return false;
        }
    }

    // ================================================================ 6. 界面语言与控制台代码页

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    [DllImport("kernel32.dll")]
    private static extern ushort GetSystemDefaultUILanguage();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleCP();

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint codePage);

    /// <summary>
    /// 读取界面语言与控制台**输入 / 输出**代码页。四个 API 均免提权，
    /// 自 Windows 2000 起可用（WS2012 自然满足）。
    ///
    /// 🔴 <b>调用时机有硬约束</b>：必须在**任何**改变控制台输出代码页的操作之前。
    /// 本工程里那个操作是 <c>Program.Run</c> 开头的 <c>Console.OutputEncoding = Encoding.UTF8</c>，
    /// 它会把控制台输出代码页改成 65001（UTF-8）。切过之后再读，读到的恒为 65001，
    /// 「中文代码页」那一路判定就永远不成立 —— 这一点 2026-09-24 第 57 轮实施时实测确认。
    ///
    /// <para><b>为什么要连输入代码页一起读（第 57 轮 R3 修法）</b>：
    /// <c>Console.OutputEncoding</c> 只改**输出**代码页，本程序从不碰**输入**代码页，
    /// 因此输入代码页是一份「没被自己污染过」的独立证据。裁决时两者取「或」，
    /// 这样即便上一次运行是被强杀（来不及恢复代码页）、或用户控制台早已被旧版永久改成 65001，
    /// 仍然能得出正确结论。两个值都会打进入 <c>--show-env</c>，便于当场核对。</para>
    ///
    /// 🔴 另一个不可忽略的细节：本方法<b>不得触碰任何 <c>Console</c> 成员</b>。
    /// <c>Console.OutputEncoding</c> / <c>Console.IsOutputRedirected</c> 都会在首次访问时
    /// 按当时状态判定并缓存，提前读一次会让后续「接管父控制台」的判定失真
    /// —— 第 50 轮屏读探针就因同样的原因出现过假阳性结论。
    /// 这里只调 kernel32，不经过托管 Console 层，因此安全。
    /// </summary>
    /// <returns>false = 四个值都没取到（正常 Windows 上不会发生），调用方按「非中文」保守处理。</returns>
    public static bool TryGetLanguageInfo(out uint userLangId, out uint systemLangId,
        out uint consoleOutputCodePage, out uint consoleInputCodePage)
    {
        userLangId = 0;
        systemLangId = 0;
        consoleOutputCodePage = 0;
        consoleInputCodePage = 0;

        try
        {
            userLangId = GetUserDefaultUILanguage();
            systemLangId = GetSystemDefaultUILanguage();
            consoleOutputCodePage = GetConsoleOutputCP();
            // 必须紧跟输出代码页之后、同样在切换编码之前读：两者是同一次「未被污染的现场」
            consoleInputCodePage = GetConsoleCP();
            return userLangId != 0 || systemLangId != 0
                || consoleOutputCodePage != 0 || consoleInputCodePage != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把控制台输出代码页恢复成 <paramref name="codePage"/>（第 57 轮 R3 修法之一）。
    ///
    /// <para>为什么要恢复：<c>Console.OutputEncoding = Encoding.UTF8</c> 是 <b>进程级</b> 设置，
    /// 但它生效的载体是<b>控制台窗口</b>：一旦切到 65001，同一个窗口里之后启动的进程
    /// 读到的都是 65001，本程序对用户环境的这次修改会一直留着。
    /// 退出时恢复，既是不污染用户终端，也顺带保证「同窗口第二次运行」读到的是原始值。</para>
    ///
    /// <para>调用约束：必须是退出前的<b>最后一步</b>。控制台代码页与托管 <c>Console.OutputEncoding</c>
    /// 是两个必须一致的量，恢复之后不应再有任何输出（否则 UTF-8 字节会被按恢复后的代码页解码）。
    /// 调用方（<c>Program.Main</c> 的 finally）已保证这一点。</para>
    ///
    /// <para>失败无害：输出被重定向时本进程根本没改过代码页，此处写回同值等于空操作；
    /// 无控制台的进程返回 false 也无需处理。</para>
    /// </summary>
    public static void RestoreConsoleOutputCodePage(uint codePage)
    {
        if (codePage == 0) return; // 探查失败（或本无控制台）：没有可信的原值，宁可不写
        try { SetConsoleOutputCP(codePage); }
        catch { /* 恢复失败只是留下副作用，不影响本次结果，也不再冒险 */ }
    }
}
