using DiskGrowthMonitor.Models;
using DiskGrowthMonitor.Native;
using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// 运行环境探测与策略推导（阶段 P）。
///
/// 目标运行环境是三档异构的：Windows Server 2012（NT 6.2，最老，无 LongPathsEnabled）、
/// Windows 10、Windows 11。同一份 exe 要在三档上跑，靠固定参数只能取交集（最保守），
/// 白白丢掉新系统与新硬件的收益。所以这里做两件事：
///   1. <see cref="Probe"/> —— 启动时把 OS 能力、CPU、内存、介质类型、**界面语言**探测出来；
///   2. <see cref="Decide"/> —— 按能力而非「猜」来决定枚举引擎、并行度候选、缓冲大小。
///
/// 关键纪律：**探测不到 ≠ 探测到否**。介质未知时必须收窄候选集交给实测，
/// 绝不能默认成机械盘（那会把 16 线程的 NVMe 机器按 1 线程跑）。
///
/// 输出语言：本类的全部提示文本都经 <see cref="Lang.T"/> 取词（第 57 轮加入），
/// 因此中英文两套输出共用同一份逻辑，不存在两份会漂移的代码路径。
/// </summary>
public static class EnvironmentProbe
{
    /// <summary>
    /// 探测运行环境。<paramref name="rootPaths"/> 为本次要扫描的根路径（用于确定要探测哪些卷）；
    /// 传空集合时只做第一段（不涉及任何卷）。
    ///
    /// 第一段（免提权）任何进程都能完成；第二段的介质类型探测需要管理员权限，
    /// 非管理员下 <see cref="SystemProfile.ProbeComplete"/> 为 false，卷的
    /// <see cref="VolumeProfile.IsSolidState"/> 保持 null（未知），而不是 false。
    /// </summary>
    /// <param name="rootPaths">本次扫描根（用于确定探测哪些卷）。</param>
    /// <param name="language">
    /// 语言探查结果（由 <c>Program.Main</c> 在一切输出之前算好）。本类只负责把它带进
    /// <see cref="SystemProfile"/> 供展示，不重复探测 —— 重复探测不仅浪费，更危险的是
    /// 「此刻再读控制台代码页」会读到已被切成 UTF-8 的值，结论与真实判定不一致。
    /// </param>
    public static SystemProfile Probe(IEnumerable<string> rootPaths, LanguageInfo? language = null)
    {
        var p = new SystemProfile { Language = language };

        // ---------------------------------------------- 第一段：免提权
        if (SystemNative.TryGetOsVersion(out uint major, out uint minor, out uint build, out byte productType))
        {
            p.OsMajor = major;
            p.OsMinor = minor;
            p.OsBuild = build;
            p.ProductType = productType;
            p.OsName = OsDisplayName(major, minor, build, productType);
            p.OsVersionReliable = true;
        }
        else
        {
            // 兜底：Environment.OSVersion 在没有兼容性清单时会把 Win8.1+ 谎报成 6.2，
            // 所以这条路径拿到的值只用来「有比没有好」，必须标记为不可靠。
            var v = Environment.OSVersion.Version;
            p.OsMajor = (uint)v.Major;
            p.OsMinor = (uint)v.Minor;
            p.OsBuild = (uint)v.Build;
            p.OsName = OsDisplayName(p.OsMajor, p.OsMinor, p.OsBuild, 0)
                       + Lang.T("（版本来源为 BCL，可能不准）", " (version from BCL, may be inaccurate)");
            p.OsVersionReliable = false;
            p.Warnings.Add(Lang.T(
                "RtlGetVersion 调用失败，OS 版本由 Environment.OSVersion 兜底（可能被兼容性清单劫持）。",
                "RtlGetVersion failed; OS version fell back to Environment.OSVersion (may be hijacked by a compatibility manifest)."));
        }

        p.PhysicalCores = SystemNative.GetPhysicalCoreCount();
        p.LogicalCores = Environment.ProcessorCount;
        p.TotalMemoryBytes = SystemNative.GetTotalPhysicalMemory();
        p.IsElevated = ElevationHelper.IsElevated();

        // ---------------------------------------------- 第二段：卷（介质探测需提权）
        var letters = ExtractVolumeLetters(rootPaths);
        bool allMediaKnown = letters.Count > 0;
        bool permissionDenied = false;

        foreach (var letter in letters)
        {
            var vol = ProbeVolume(letter, out bool denied);
            if (denied) permissionDenied = true;
            if (vol.IsSolidState == null) allMediaKnown = false;
            p.Volumes.Add(vol);
        }

        p.ProbeComplete = allMediaKnown;

        if (permissionDenied)
            p.Warnings.Add(Lang.T(
                "介质类型探测需要管理员权限（要打开 \\\\.\\PhysicalDriveN）：当前非管理员，介质未知 —— 并行度将交由实测决定。",
                "Media-type probing needs administrator rights (it opens \\\\.\\PhysicalDriveN): not elevated, so media type is unknown - parallelism will be decided by measurement."));
        else if (letters.Count == 0)
            p.Warnings.Add(Lang.T(
                "未指定扫描根，未探测卷信息。",
                "No scan root specified; volume information was not probed."));

        return p;
    }

    /// <summary>探测单个卷。denied 回传「是否因权限不足而失败」，供调用方给出针对性提示。</summary>
    private static VolumeProfile ProbeVolume(char letter, out bool denied)
    {
        denied = false;
        var vol = new VolumeProfile { Letter = letter };

        try
        {
            var di = new DriveInfo(letter.ToString());
            if (di.IsReady)
            {
                vol.FileSystem = di.DriveFormat;
                vol.TotalBytes = di.TotalSize;
                vol.FreeBytes = di.AvailableFreeSpace;
            }
        }
        catch
        {
            // 卷未就绪（如刚拔掉的移动盘）：保留「未知」，不影响后续判定
        }

        // 是否支持硬链接：走 GetVolumeInformationW，不需要管理员。
        // 不支持时（FAT32/exFAT）整个去重链路可以直接关掉，省下每文件一次句柄开销。
        try
        {
            vol.SupportsHardLinks = FileSystemNative.VolumeSupportsHardLinks(letter + ":");
        }
        catch
        {
            vol.SupportsHardLinks = true;   // 判不了就按支持处理，宁可多算不漏算
        }

        // 介质类型：盘符 → 物理磁盘号 → 寻道代价
        if (SystemNative.TryGetPhysicalDiskNumber(letter, out uint disk, out int vErr))
        {
            vol.DiskNumber = (int)disk;
            if (SystemNative.TryGetSeekPenalty(disk, out bool incursSeekPenalty, out int pErr))
            {
                vol.IsSolidState = !incursSeekPenalty;
            }
            else
            {
                denied = IsAccessDenied(pErr);
            }
        }
        else
        {
            denied = IsAccessDenied(vErr);
        }

        return vol;
    }

    /// <summary>ERROR_ACCESS_DENIED(5)：非管理员打开卷/物理盘设备时的必然结果。</summary>
    private static bool IsAccessDenied(int lastError) => lastError == 5;

    /// <summary>
    /// 把三个目标平台映射成可读名称。服务器版另按 build 号细分。
    /// 产品名（Windows 11 / Windows Server 2012）是专有名词，不随输出语言变化。
    /// </summary>
    private static string OsDisplayName(uint major, uint minor, uint build, byte productType)
    {
        bool server = productType == 2 || productType == 3;

        if (major == 6 && minor == 1) return server ? "Windows Server 2008 R2" : "Windows 7";
        if (major == 6 && minor == 2) return server ? "Windows Server 2012" : "Windows 8";
        if (major == 6 && minor == 3) return server ? "Windows Server 2012 R2" : "Windows 8.1";

        if (major == 10)
        {
            if (server)
            {
                if (build >= 20348) return "Windows Server 2022";
                if (build >= 17763) return "Windows Server 2019";
                return "Windows Server 2016";
            }
            return build >= 22000 ? "Windows 11" : "Windows 10";
        }

        return $"Windows {major}.{minor} (build {build})";
    }

    /// <summary>从扫描根路径里取出涉及的盘符（去重、升序）。</summary>
    private static List<char> ExtractVolumeLetters(IEnumerable<string> rootPaths)
    {
        var set = new SortedSet<char>();
        foreach (var path in rootPaths)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':') continue;
            if (!char.IsLetter(path[0])) continue;
            set.Add(char.ToUpperInvariant(path[0]));
        }
        return new List<char>(set);
    }

    // ================================================================ 策略推导（决策表）

    /// <summary>
    /// 按探测结果决定扫描策略。这样做的意义在于把「这台机器上应该怎么扫」
    /// 从编译期常量变成运行期判断 —— 三档目标环境可以各自跑到自己能力上限，
    /// 而不是被最老那档（WS2012）拖到同一个保守配置。
    /// </summary>
    /// <param name="p">环境探测结果。</param>
    /// <param name="requested">命令行指定的枚举引擎档位（默认 auto）。</param>
    public static ScanStrategy Decide(SystemProfile p, EnumEngineKind requested = EnumEngineKind.Auto)
    {
        var s = new ScanStrategy();
        int phys = p.PhysicalCores > 0 ? p.PhysicalCores : Math.Max(1, p.LogicalCores);
        int logical = Math.Max(1, p.LogicalCores);

        // ---- ① 枚举引擎 ----
        // 关键结论（阶段 B 前置 PoC 实证）：
        //   · FileIdExtdDirectoryInformation(60) 的可用门槛**文档口径互相矛盾**
        //     （MSDN 称 Server 2012 起，NtDoc 称 Win10 起），而 WS2012 恰在争议区间内，
        //     所以绝不能凭版本号猜 —— 一律按卷运行时实测，失败再降级。
        //   · FileIdBothDirectoryInformation(37) 自 Vista / Server 2008 起就可用，
        //     同样能拿到 64 位 FileId（NTFS 上即 MFT 记录号），是 WS2012 的**保险绳**：
        //     即便 60 不可用，也不会退回「逐文件开句柄」那条慢 8 倍的路径。
        //   · 降级链最末一档 Win32 即改动前的实现，语义等价、只是慢。
        bool osSupportsNt = p.OsMajor > 6 || (p.OsMajor == 6 && p.OsMinor >= 2);

        if (requested == EnumEngineKind.Win32)
        {
            s.EnumEngine = Lang.T(
                "FindFirstFileExW + 逐文件开句柄（--enum-engine=win32 指定）",
                "FindFirstFileExW + per-file handles (forced by --enum-engine=win32)");
            s.Reasons.Add(Lang.T(
                "命令行指定 --enum-engine=win32：使用改动前的枚举与去重路径，结果口径不变（用于对照与回退）",
                "--enum-engine=win32 given: uses the pre-change enumeration and dedup path; results are identical (for comparison and rollback)"));
        }
        else if (!osSupportsNt)
        {
            s.EnumEngine = Lang.T(
                "FindFirstFileExW（该系统低于 NT 6.2，仅 2008 R2 及更早）",
                "FindFirstFileExW (OS below NT 6.2 - Server 2008 R2 and earlier only)");
            s.Reasons.Add(Lang.F(
                "OS {0}.{1} < 6.2：仅 Windows 2008 R2 及更早系统，退化为 FindFirstFileExW",
                "OS {0}.{1} < 6.2: Server 2008 R2 and earlier only; falls back to FindFirstFileExW",
                p.OsMajor, p.OsMinor));
        }
        else
        {
            s.EnumEngine = requested == EnumEngineKind.Auto
                ? Lang.T(
                    "NtQueryDirectoryFile 目录项直出 FileId（按卷实测降级 60 → 37 → Win32）",
                    "NtQueryDirectoryFile returning FileId in directory entries (per-volume probing, 60 -> 37 -> Win32)")
                : Lang.F(
                    "NtQueryDirectoryFile 目录项直出 FileId（强制 {0}）",
                    "NtQueryDirectoryFile returning FileId in directory entries (forced: {0})",
                    EnumEngineSelector.Describe(requested));
            s.Reasons.Add(Lang.T(
                "目录项直出文件标识：硬链接去重无需逐文件开句柄（实测占旧路径约 69% 耗时）",
                "File IDs come straight from directory entries, so hardlink dedup needs no per-file handle (measured as ~69% of the old path's time)"));
            s.Reasons.Add(Lang.T(
                "按卷运行时实测信息类可用性（60 → 37 → Win32），不按 OS 版本猜测 —— 文档对 60 的门槛口径矛盾",
                "Information-class availability is measured per volume at runtime (60 -> 37 -> Win32), never guessed from the OS version - the documented threshold for 60 is contradictory"));
        }

        // 枚举缓冲固定 64 KB。PoC 实测：NTFS 每轮 NtQueryDirectoryFile 约 64 KB 封顶
        // （64 / 256 / 1024 KB 三者的轮次完全相同），而 1 MB 反而慢 2.9~3.1 倍。
        s.EnumBufferBytes = EnumEngineSelector.DefaultBufferSize;

        // ---- ② 介质 → 并行度候选集 ----
        // 取「最弱介质」：只要目标卷里有一块机械盘（或有一块介质未知），
        // 就不能按固态去铺并行度 —— 一块慢盘会把整个并行池拖垮。
        bool? solid = null;
        foreach (var v in p.Volumes)
        {
            if (v.IsSolidState == null) { solid = null; break; }        // 未知 ⇒ 整体未知
            if (!v.IsSolidState.Value) { solid = false; break; }        // 有机械盘 ⇒ 按机械盘
            solid = true;
        }

        if (solid == true)
        {
            // 固态：候选覆盖「少而稳」到「逻辑核数」的区间。
            // 上界只到逻辑核数（不越界加码），下界从物理核的一半起 ——
            // 并发度过高时内核锁与内存带宽会成为新瓶颈，未必更快，所以必须实测。
            s.ParallelCandidates = Distinct(BuildCandidates(phys / 2, phys, phys + 4, logical));
            s.Reasons.Add(Lang.F(
                "介质为固态：候选并行度按物理核展开（{0}），交给阶段 D 实测固化",
                "Media is solid-state: parallelism candidates spread around the physical core count ({0}); stage D will measure and fix them",
                Describe(s.ParallelCandidates)));
        }
        else if (solid == false)
        {
            // 机械盘：并行读多个目录会让磁头来回寻道，吞吐反而下降。
            // 上限压到 4 —— 在 HDD 上这是「还能有点收益」与「寻道惩罚吃掉收益」的分水岭。
            s.ParallelCandidates = new[] { 1, 2, 3, 4 };
            s.Reasons.Add(Lang.T(
                "检测到机械硬盘：并行度上限压到 4（随机寻道代价高，高并发反而更慢）",
                "Mechanical disk detected: parallelism capped at 4 (random seeks are expensive; more concurrency is slower)"));
        }
        else
        {
            // 介质未知（非管理员 / 探测被拒）：把候选集收窄到两个极端，
            // 让阶段 D 的同口径实测来决定 —— 这比凭猜测选一个值稳妥得多。
            s.ParallelCandidates = Distinct(BuildCandidates(1, phys));
            s.Reasons.Add(Lang.T(
                "介质未知（需管理员权限才能探测）：候选集收窄为 {1, 物理核数}，交由实测决定",
                "Media type unknown (probing needs administrator rights): candidates narrowed to {1, physical core count}, decided by measurement"));
        }

        s.Reasons.Add(Lang.T(
            "枚举缓冲 64 KB：实测 NTFS 每轮约 64 KB 封顶（64/256/1024 KB 轮次相同），1 MB 反而慢 2.9~3.1 倍",
            "Enum buffer 64 KB: measured ~64 KB per NTFS round trip (64/256/1024 KB give the same round count), while 1 MB is 2.9-3.1x slower"));

        // ---- ③ 去重 ----
        if (p.Volumes.Count > 0)
        {
            bool anySupports = false;
            foreach (var v in p.Volumes)
                if (v.SupportsHardLinks) { anySupports = true; break; }

            if (!anySupports)
            {
                s.Dedup = false;
                s.Reasons.Add(Lang.T(
                    "目标卷均不支持硬链接（FAT32/exFAT）：关闭去重，省掉每文件一次句柄开销",
                    "No target volume supports hardlinks (FAT32/exFAT): dedup disabled, saving one handle per file"));
            }
        }

        return s;
    }

    private static int[] BuildCandidates(params int[] values) => values;

    private static int[] Distinct(int[] values)
    {
        var set = new SortedSet<int>();
        foreach (var v in values)
            if (v >= 1) set.Add(v);
        return new List<int>(set).ToArray();
    }

    private static string Describe(int[] values)
    {
        var parts = new List<string>(values.Length);
        foreach (var v in values) parts.Add(v.ToString());
        return string.Join(" / ", parts);
    }

    // ================================================================ 输出

    /// <summary>单行环境摘要（扫描时打印，够用且不刷屏）。</summary>
    public static string FormatBrief(SystemProfile p, ScanStrategy s)
    {
        if (Lang.IsEnglish)
        {
            var en = new System.Text.StringBuilder();
            en.Append("OS: ").Append(p.OsName).Append(" (").Append(p.OsMajor).Append('.').Append(p.OsMinor)
              .Append('.').Append(p.OsBuild).Append(')');
            en.Append("  CPU: ").Append(p.PhysicalCores).Append(" cores/").Append(p.LogicalCores).Append(" threads");
            if (p.TotalMemoryBytes > 0)
                en.Append("  Memory: ").Append(FormatUtil.Bytes(p.TotalMemoryBytes));
            en.Append("  Media: ").Append(DescribeMedia(p));
            en.Append("  Language: ").Append(Lang.DescribeLanguage(
                p.Language?.Language ?? Lang.Current));
            return en.ToString();
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("系统：").Append(p.OsName).Append(" (").Append(p.OsMajor).Append('.').Append(p.OsMinor)
          .Append('.').Append(p.OsBuild).Append(')');
        sb.Append("　CPU：").Append(p.PhysicalCores).Append(" 物理核/").Append(p.LogicalCores).Append(" 线程");
        if (p.TotalMemoryBytes > 0)
            sb.Append("　内存：").Append(FormatUtil.Bytes(p.TotalMemoryBytes));
        sb.Append("　介质：").Append(DescribeMedia(p));
        sb.Append("　语言：").Append(Lang.DescribeLanguage(
            p.Language?.Language ?? Lang.Current));
        return sb.ToString();
    }

    /// <summary>介质一句话描述：全部固态 / 全部机械 / 混合 / 未知。</summary>
    private static string DescribeMedia(SystemProfile p)
    {
        if (p.Volumes.Count == 0) return Lang.T("未探测", "not probed");

        bool anySsd = false, anyHdd = false, anyUnknown = false;
        foreach (var v in p.Volumes)
        {
            if (v.IsSolidState == null) anyUnknown = true;
            else if (v.IsSolidState.Value) anySsd = true;
            else anyHdd = true;
        }

        if (anyUnknown)
            return anySsd || anyHdd
                ? Lang.T("部分未知（需管理员）", "partly unknown (needs administrator)")
                : Lang.T("未知（需管理员权限）", "unknown (needs administrator rights)");
        if (anySsd && anyHdd) return Lang.T("固态 + 机械混合", "SSD + HDD mixed");
        return anySsd ? Lang.T("固态", "solid-state") : Lang.T("机械硬盘", "mechanical disk");
    }

    /// <summary>详细环境与策略输出（--show-env）。</summary>
    public static void PrintSummary(SystemProfile p, ScanStrategy s)
    {
        Console.WriteLine(Lang.T("--- 操作系统 ---", "--- Operating system ---"));
        Console.WriteLine("  " + Pad(Lang.T("名称", "Name")) + p.OsName);
        Console.WriteLine("  " + Pad(Lang.T("版本", "Version"))
                          + p.OsMajor + "." + p.OsMinor + "." + p.OsBuild
                          + (p.OsVersionReliable
                              ? Lang.T("（RtlGetVersion 真值）", " (RtlGetVersion, authoritative)")
                              : Lang.T("（来源不可靠）", " (unreliable source)")));
        Console.WriteLine("  " + Pad(Lang.T("类型", "Type"))
                          + (p.IsServer ? Lang.T("服务器", "Server") : Lang.T("工作站", "Workstation"))
                          + Lang.F("（wProductType = {0}）", " (wProductType = {0})", p.ProductType));
        Console.WriteLine();

        PrintLanguageSection(p);

        Console.WriteLine(Lang.T("--- 硬件 ---", "--- Hardware ---"));
        Console.WriteLine("  " + Pad(Lang.T("处理器", "CPU"))
                          + Lang.F("{0} 物理核 / {1} 逻辑处理器",
                                   "{0} physical cores / {1} logical processors",
                                   p.PhysicalCores, p.LogicalCores));
        Console.WriteLine("  " + Pad(Lang.T("内存", "Memory"))
                          + (p.TotalMemoryBytes > 0 ? FormatUtil.Bytes(p.TotalMemoryBytes) : Lang.T("未知", "unknown")));
        Console.WriteLine("  " + Pad(Lang.T("当前权限", "Elevated"))
                          + (p.IsElevated ? Lang.T("管理员", "Administrator") : Lang.T("普通用户", "Standard user")));
        Console.WriteLine();

        Console.WriteLine(Lang.T("--- 卷 ---", "--- Volumes ---"));
        if (p.Volumes.Count == 0)
        {
            Console.WriteLine("  " + Lang.T("（未指定扫描根，未探测）", "(no scan root specified, not probed)"));
        }
        else
        {
            // 表头与数据行共用同一套列宽（Cell 按显示宽度补齐），否则中英两档、以及汉字/ASCII 混排
            // 都会错列 —— 用 PadRight 按字符数补是最容易踩的坑（一个汉字占两列，见 Pad 的说明）。
            Console.WriteLine("  " + FormatUtil.Cell(Lang.T("盘符", "Drive"), 7) + FormatUtil.Cell(Lang.T("文件系统", "FileSystem"), 13)
                              + FormatUtil.Cell(Lang.T("介质", "Media"), 13) + FormatUtil.Cell(Lang.T("物理磁盘", "Disk"), 9)
                              + FormatUtil.Cell(Lang.T("总容量", "Total"), 15) + FormatUtil.Cell(Lang.T("可用空间", "Free"), 15)
                              + Lang.T("硬链接", "HardLinks"));
            foreach (var v in p.Volumes)
            {
                string media = v.IsSolidState == null
                    ? Lang.T("未知", "unknown")
                    : (v.IsSolidState.Value ? Lang.T("固态", "SSD") : Lang.T("机械", "HDD"));
                string disk = v.DiskNumber >= 0 ? v.DiskNumber.ToString() : "-";
                string total = v.TotalBytes > 0 ? FormatUtil.Bytes(v.TotalBytes) : "-";
                string free = v.TotalBytes > 0 ? FormatUtil.Bytes(v.FreeBytes) : "-";
                Console.WriteLine("  " + FormatUtil.Cell(v.Letter + ":", 7)
                                  + FormatUtil.Cell(v.FileSystem.Length > 0 ? v.FileSystem : "-", 13)
                                  + FormatUtil.Cell(media, 13) + FormatUtil.Cell(disk, 9)
                                  + FormatUtil.Cell(total, 15) + FormatUtil.Cell(free, 15)
                                  + (v.SupportsHardLinks ? Lang.T("支持", "yes") : Lang.T("不支持", "no")));
            }
        }
        Console.WriteLine();

        Console.WriteLine(Lang.T("--- 推导出的扫描策略 ---", "--- Derived scan strategy ---"));
        Console.WriteLine("  " + Pad(Lang.T("枚举引擎", "Enum engine")) + s.EnumEngine);
        Console.WriteLine("  " + Pad(Lang.T("并行度", "Parallelism"))
                          + (s.ParallelCandidates.Length > 0 ? Describe(s.ParallelCandidates) : "-")
                          + Lang.T("（候选集；阶段 D 逐档实测后固化）",
                                   " (candidates; stage D will measure each before fixing)"));
        Console.WriteLine("  " + Pad(Lang.T("枚举缓冲", "Enum buffer"))
                          + (s.EnumBufferBytes > 0 ? FormatUtil.Bytes(s.EnumBufferBytes) : "-"));
        Console.WriteLine("  " + Pad(Lang.T("硬链接去重", "Hardlink dedup"))
                          + (s.Dedup ? Lang.T("启用", "enabled") : Lang.T("关闭", "disabled")));
        Console.WriteLine();
        Console.WriteLine("  " + Lang.T("决策依据：", "Decision basis:"));
        foreach (var r in s.Reasons) Console.WriteLine("    · " + r);

        if (p.Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  " + Lang.T("提示：", "Notes:"));
            foreach (var w in p.Warnings) Console.WriteLine("    ! " + w);
        }
    }

    /// <summary>
    /// 语言探查小节（--show-env 专用，第 57 轮新增）。
    ///
    /// 打印「两个 API 的原始值 + 控制台代码页 + 结论 + 依据」四件套，
    /// 让「为什么这次是中文/英文」当场可核对 —— 语言判错的表现是「全篇英文」，
    /// 不把原始值打出来就只能靠猜（而猜不出「界面中文 + 代码页 65001 会判英文」这种组合）。
    /// </summary>
    private static void PrintLanguageSection(SystemProfile p)
    {
        var info = p.Language;
        Console.WriteLine(Lang.T("--- 语言 ---", "--- Language ---"));

        if (info == null)
        {
            Console.WriteLine("  " + Lang.T("（本次未探查，按默认档输出）", "(not probed this run; default language in use)"));
            Console.WriteLine();
            return;
        }

        Console.WriteLine("  " + Pad(Lang.T("界面语言", "UI language"))
                          + Display(info.UiCultureName)
                          + Lang.F("（LANGID 0x{0:X4}）", " (LANGID 0x{0:X4})", info.UserLanguageId));
        Console.WriteLine("  " + Pad(Lang.T("系统语言", "System language"))
                          + Lang.F("0x{0:X4}", "0x{0:X4}", info.SystemLanguageId));
        Console.WriteLine("  " + Pad(Lang.T("控制台代码页", "Console code page"))
                          + Lang.F("输出 {0} · 输入 {1}", "output {0} / input {1}",
                                   info.ConsoleOutputCodePage, info.ConsoleInputCodePage)
                          + (info.ConsoleIsChinese
                              ? Lang.T("（中文代码页）", " (Chinese code page)")
                              : string.Empty));
        Console.WriteLine("  " + Pad(Lang.T("输出语言", "Output language"))
                          + Lang.DescribeLanguage(info.Language));
        Console.WriteLine("  " + Pad(Lang.T("判定依据", "Decision basis"))
                          + Lang.DescribeDecision(info));
        if (!info.ProbeSucceeded)
            Console.WriteLine("  " + Lang.T("! 语言探测 API 全部失败，按非中文处理。",
                                            "! All language-probing APIs failed; treated as non-Chinese."));
        Console.WriteLine();
    }

    /// <summary>取值或占位符（避免打出空白，两个语言各自给占位文本）。</summary>
    private static string Display(string value)
        => string.IsNullOrEmpty(value) ? Lang.T("未知", "unknown") : value;

    /// <summary>
    /// 标签列对齐（中文档与英文档各一个目标显示宽度）。
    ///
    /// 补齐实现下沉到 <see cref="FormatUtil.Cell"/>：Program 的磁盘表（<c>PrintDrives</c>）
    /// 与这里的卷表要用同一套「按显示宽度补齐」的规则，各写一份必然漂移，故只留一份。
    /// </summary>
    private static string Pad(string label) => FormatUtil.Cell(label, Lang.IsEnglish ? 21 : 14);
}
