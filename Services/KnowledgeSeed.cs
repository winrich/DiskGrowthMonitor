using DiskGrowthMonitor.Models;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// 目录用途知识库的内置种子数据。
///
/// 全部 pattern 采用<b>后缀对齐</b>语义（见 <c>Models/DirKnowledge.Pattern</c> 的说明），
/// 因此不写盘符与用户名，可跨机器、跨用户命中。
///
/// 写入策略：<c>INSERT OR IGNORE</c> —— 已存在的 pattern 一律跳过，
/// 用户在数据库里对条目的任何修改（改 note / 调 priority / 停用）都不会被程序覆盖。
///
/// 覆盖范围：Windows 系统目录（临时/缓存/日志/转储/组件）+ 三大浏览器数据目录 +
/// 本机常见的第三方应用与开发工具缓存。
///
/// <para><b>英文列（第 57 轮新增；见 <see cref="DirKnowledge.CategoryEn"/> 等）</b>：
/// 用途说明属于<b>数据</b>而不是提示文本，因此不放进 <c>Lang</c> 的双语取词表，
/// 而是每条自带英文（<c>category_en</c> / <c>title_en</c> / <c>note_en</c>）。
/// 这样做的直接好处是<b>英文与中文写在同一处</b>，改一条不会漏掉另一条 ——
/// 66 条若分散到两张表，迟早会对不上号。
/// 老库升级时由 <c>DatabaseService.SeedKnowledge</c> 只对「英文列为空」的行回填，
/// 不会覆盖用户自行改过的内容。</para>
///
/// <para><b>刻意不翻译的两处</b>：
/// ① <c>Cleanable</c>（安全/谨慎/禁止）是<b>被程序比较的数据键</b>（<c>CleanupAdvisor</c> 用
/// <c>"安全"</c> 判等），显示文本由 <see cref="DirKnowledge.CleanableText"/> 按语言给出；
/// ② <c>Source</c> 只入库留痕、从不展示，因此没有英文列。</para>
/// </summary>
internal static class KnowledgeSeed
{
    private const string SrcMsLearn = "Microsoft Learn：Windows 目录与组件说明";
    private const string SrcChrome = "Chrome 官方文档 / Chromium 用户数据目录说明";
    private const string SrcEdge = "Microsoft Edge 官方文档（用户数据目录）";
    private const string SrcFirefox = "Mozilla 支持文档（配置文件与缓存）";
    private const string SrcHtg = "How-To Geek：Windows 目录用途指南";
    private const string SrcVendor = "软件厂商文档（客户端数据目录）";

    /// <summary>内置条目。数值越大优先级越高；同一目录命中多条时取优先级最高者。</summary>
    public static readonly DirKnowledge[] Entries =
    {
        // ---------------------------------------------------------- A. 浏览器
        new()
        {
            Pattern = @"*\Google\Chrome\User Data", Category = "浏览器", CategoryEn = "Browser",
            Title = "Chrome 用户数据目录", TitleEn = "Chrome user data directory",
            Cleanable = "谨慎", Priority = 30, Source = SrcChrome,
            Note = "浏览器全部数据的根目录：缓存、历史、Cookie、扩展、书签都在这下面",
            NoteEn = "Root of all browser data: caches, history, cookies, extensions and bookmarks all live below this"
        },
        new()
        {
            Pattern = @"*\Google\Chrome\User Data\*\Cache", Category = "浏览器", CategoryEn = "Browser",
            Title = "Chrome 网页缓存", TitleEn = "Chrome web cache",
            Cleanable = "安全", Priority = 60, Source = SrcChrome,
            Note = "网页图片/脚本/样式等缓存副本，删除后首次访问会稍慢",
            NoteEn = "Cached copies of page images, scripts and styles; pages load a little slower the first time afterwards"
        },
        new()
        {
            Pattern = @"*\Google\Chrome\User Data\*\Code Cache", Category = "浏览器", CategoryEn = "Browser",
            Title = "Chrome 代码缓存", TitleEn = "Chrome code cache",
            Cleanable = "安全", Priority = 60, Source = SrcChrome,
            Note = "已编译的 JavaScript 缓存，可安全清理",
            NoteEn = "Cache of compiled JavaScript; safe to clean"
        },
        new()
        {
            Pattern = @"*\Google\Chrome\User Data\*\GPUCache", Category = "浏览器", CategoryEn = "Browser",
            Title = "Chrome GPU 缓存", TitleEn = "Chrome GPU cache",
            Cleanable = "安全", Priority = 60, Source = SrcChrome,
            Note = "显卡着色器缓存，可安全清理",
            NoteEn = "Graphics shader cache; safe to clean"
        },
        new()
        {
            Pattern = @"*\Google\Chrome\User Data\*\Service Worker", Category = "浏览器", CategoryEn = "Browser",
            Title = "Chrome 站点后台脚本", TitleEn = "Chrome service worker scripts",
            Cleanable = "谨慎", Priority = 55, Source = SrcChrome,
            Note = "Service Worker 脚本与缓存，删除后部分网页需重新加载",
            NoteEn = "Service worker scripts and caches; some pages have to be reloaded after deletion"
        },
        new()
        {
            Pattern = @"*\Google\Chrome\User Data\*\IndexedDB", Category = "浏览器", CategoryEn = "Browser",
            Title = "Chrome 站点本地数据库", TitleEn = "Chrome site local databases",
            Cleanable = "谨慎", Priority = 55, Source = SrcChrome,
            Note = "网页应用（如在线文档）写入的本地数据，删除会丢站点内数据",
            NoteEn = "Local data written by web apps such as online documents; deleting it loses data held by the site"
        },
        new()
        {
            Pattern = @"*\Google\Chrome\User Data\*\Local Storage", Category = "浏览器", CategoryEn = "Browser",
            Title = "Chrome 本地存储", TitleEn = "Chrome local storage",
            Cleanable = "谨慎", Priority = 55, Source = SrcChrome,
            Note = "网站写入的键值数据，可能影响登录状态",
            NoteEn = "Key-value data written by websites; may affect signed-in state"
        },
        new()
        {
            Pattern = @"*\Google\Chrome\User Data\*\Session Storage", Category = "浏览器", CategoryEn = "Browser",
            Title = "Chrome 会话存储", TitleEn = "Chrome session storage",
            Cleanable = "安全", Priority = 50, Source = SrcChrome,
            Note = "仅当前会话有效的网页数据",
            NoteEn = "Page data that is only valid for the current session"
        },
        new()
        {
            Pattern = @"*\Google\Chrome\User Data\*\Network", Category = "浏览器", CategoryEn = "Browser",
            Title = "Chrome Cookie 与网络状态", TitleEn = "Chrome cookies and network state",
            Cleanable = "谨慎", Priority = 60, Source = SrcChrome,
            Note = "含 Cookies、传输安全状态等（登录凭据相关）",
            NoteEn = "Contains cookies and transport security state (related to sign-in credentials)"
        },
        new()
        {
            Pattern = @"*\Google\Chrome\User Data\*\History", Category = "浏览器", CategoryEn = "Browser",
            Title = "Chrome 浏览历史", TitleEn = "Chrome browsing history",
            Cleanable = "谨慎", Priority = 60, Source = SrcChrome,
            Note = "SQLite 数据库：访问历史与下载记录（隐私数据）",
            NoteEn = "SQLite database: visit history and download records (private data)"
        },
        new()
        {
            Pattern = @"*\Google\Chrome\User Data\*\Web Data", Category = "浏览器", CategoryEn = "Browser",
            Title = "Chrome 表单自动填充", TitleEn = "Chrome form autofill data",
            Cleanable = "谨慎", Priority = 55, Source = SrcChrome,
            Note = "自动填充与搜索关键词等数据",
            NoteEn = "Autofill entries and search keywords"
        },
        new()
        {
            Pattern = @"*\Microsoft\Edge\User Data", Category = "浏览器", CategoryEn = "Browser",
            Title = "Edge 用户数据目录", TitleEn = "Edge user data directory",
            Cleanable = "谨慎", Priority = 30, Source = SrcEdge,
            Note = "浏览器全部数据的根目录（Cache/History/Cookies/扩展）",
            NoteEn = "Root of all browser data (cache, history, cookies, extensions)"
        },
        new()
        {
            Pattern = @"*\Microsoft\Edge\User Data\*\Cache", Category = "浏览器", CategoryEn = "Browser",
            Title = "Edge 网页缓存", TitleEn = "Edge web cache",
            Cleanable = "安全", Priority = 60, Source = SrcEdge,
            Note = "与 Chrome 结构相同，可安全清理",
            NoteEn = "Same layout as Chrome; safe to clean"
        },
        new()
        {
            Pattern = @"*\Mozilla\Firefox\Profiles", Category = "浏览器", CategoryEn = "Browser",
            Title = "Firefox 配置文件目录", TitleEn = "Firefox profile directory",
            Cleanable = "谨慎", Priority = 30, Source = SrcFirefox,
            Note = "每个 *.default* 子目录为一个配置文件（含历史、书签、Cookie）",
            NoteEn = "Each *.default* subdirectory is one profile (history, bookmarks, cookies)"
        },
        new()
        {
            Pattern = @"*\Mozilla\Firefox\Profiles\*\cache2", Category = "浏览器", CategoryEn = "Browser",
            Title = "Firefox 网页缓存", TitleEn = "Firefox web cache",
            Cleanable = "安全", Priority = 60, Source = SrcFirefox,
            Note = "网页缓存文件，可安全清理",
            NoteEn = "Page cache files; safe to clean"
        },
        new()
        {
            Pattern = @"*\Microsoft\Windows\INetCache", Category = "浏览器", CategoryEn = "Browser",
            Title = "系统级网络缓存", TitleEn = "System-level web cache",
            Cleanable = "安全", Priority = 50, Source = SrcMsLearn,
            Note = "旧式 Internet 临时文件（IE / 系统组件共用）",
            NoteEn = "Legacy Internet temporary files (shared by IE and system components)"
        },

        // ---------------------------------------- B. 系统临时文件与缓存
        new()
        {
            Pattern = @"C:\Windows\Temp", Category = "系统缓存", CategoryEn = "System cache",
            Title = "Windows 系统临时文件", TitleEn = "Windows system temporary files",
            Cleanable = "安全", Priority = 70, Source = SrcMsLearn,
            Note = "系统与安装程序产生的临时文件，正在占用的文件会跳过",
            NoteEn = "Temporary files created by the system and by installers; files still in use are skipped"
        },
        new()
        {
            Pattern = @"*\AppData\Local\Temp", Category = "系统缓存", CategoryEn = "System cache",
            Title = "用户临时文件", TitleEn = "User temporary files",
            Cleanable = "安全", Priority = 70, Source = SrcMsLearn,
            Note = "各程序运行期临时文件；Windows 会自动重建该目录",
            NoteEn = "Scratch files from running programs; Windows recreates this directory automatically"
        },
        new()
        {
            Pattern = @"*\AppData\Local\Temp\Low", Category = "系统缓存", CategoryEn = "System cache",
            Title = "低权限临时目录", TitleEn = "Low-integrity temp directory",
            Cleanable = "安全", Priority = 65, Source = SrcMsLearn,
            Note = "受保护模式（浏览器等）低权限进程写入",
            NoteEn = "Written by low-integrity processes such as protected-mode browsers"
        },
        new()
        {
            Pattern = @"C:\Windows\Prefetch", Category = "系统缓存", CategoryEn = "System cache",
            Title = "预读取缓存", TitleEn = "Prefetch cache",
            Cleanable = "安全", Priority = 60, Source = SrcMsLearn,
            Note = "记录程序加载布局以加速启动；清理后首次启动会变慢",
            NoteEn = "Records program load layout to speed up start-up; the first launch after cleaning is slower"
        },
        new()
        {
            Pattern = @"C:\Windows\SoftwareDistribution\Download", Category = "系统缓存", CategoryEn = "System cache",
            Title = "Windows 更新下载缓存", TitleEn = "Windows Update download cache",
            Cleanable = "安全", Priority = 65, Source = SrcMsLearn,
            Note = "已安装更新的安装包残留，建议用「磁盘清理」释放",
            NoteEn = "Leftover packages from updates that are already installed; freeing them through Disk Cleanup is recommended"
        },
        new()
        {
            Pattern = @"C:\Windows\SoftwareDistribution", Category = "系统缓存", CategoryEn = "System cache",
            Title = "Windows 更新工作目录", TitleEn = "Windows Update working directory",
            Cleanable = "谨慎", Priority = 40, Source = SrcMsLearn,
            Note = "更新服务的数据存储（DataStore / Download），勿整目录删除",
            NoteEn = "Data store of the update service (DataStore / Download); do not delete the whole directory"
        },
        new()
        {
            Pattern = @"C:\ProgramData\Microsoft\Windows\DeliveryOptimization", Category = "系统缓存", CategoryEn = "System cache",
            Title = "传递优化缓存", TitleEn = "Delivery Optimization cache",
            Cleanable = "安全", Priority = 60, Source = SrcMsLearn,
            Note = "更新与商店应用的 P2P 分片缓存",
            NoteEn = "Peer-to-peer fragment cache for updates and Store apps"
        },
        new()
        {
            Pattern = @"C:\ProgramData\Microsoft\Windows\Caches", Category = "系统缓存", CategoryEn = "System cache",
            Title = "系统级配置缓存", TitleEn = "System-level configuration caches",
            Cleanable = "谨慎", Priority = 40, Source = SrcMsLearn,
            Note = "系统组件共用的小型缓存库",
            NoteEn = "Small cache stores shared by system components"
        },
        new()
        {
            Pattern = @"*\AppData\Local\Microsoft\Windows\Explorer", Category = "系统缓存", CategoryEn = "System cache",
            Title = "资源管理器缩略图/图标缓存", TitleEn = "Explorer thumbnail and icon caches",
            Cleanable = "安全", Priority = 65, Source = SrcHtg,
            Note = "thumbcache_*.db（缩略图）与 iconcache_*.db（图标）缓存",
            NoteEn = "thumbcache_*.db (thumbnails) and iconcache_*.db (icons)"
        },

        // ------------------------------------------------ C. 字体与图形缓存
        new()
        {
            Pattern = @"C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache", Category = "系统缓存", CategoryEn = "System cache",
            Title = "字体缓存", TitleEn = "Font cache",
            Cleanable = "安全", Priority = 50, Source = SrcMsLearn,
            Note = "字体渲染缓存；显示异常时可删除让其自动重建",
            NoteEn = "Font rendering cache; safe to delete when text renders incorrectly, Windows rebuilds it"
        },
        new()
        {
            Pattern = @"C:\Windows\System32\FNTCACHE.DAT", Category = "系统缓存", CategoryEn = "System cache",
            Title = "字体缓存数据文件", TitleEn = "Font cache data file",
            Cleanable = "谨慎", Priority = 40, Source = SrcMsLearn,
            Note = "系统字体缓存数据库",
            NoteEn = "System font cache database"
        },

        // ------------------------------------------------------------ D. 日志
        new()
        {
            Pattern = @"C:\Windows\Logs", Category = "日志", CategoryEn = "Logs",
            Title = "Windows 组件日志", TitleEn = "Windows component logs",
            Cleanable = "安全", Priority = 55, Source = SrcMsLearn,
            Note = "CBS / DISM / WindowsUpdate 等组件日志，故障排查后可清理",
            NoteEn = "Logs from CBS, DISM, WindowsUpdate and similar components; can be cleaned once troubleshooting is over"
        },
        new()
        {
            Pattern = @"C:\Windows\System32\winevt\Logs", Category = "日志", CategoryEn = "Logs",
            Title = "系统事件日志", TitleEn = "System event logs",
            Cleanable = "谨慎", Priority = 60, Source = SrcMsLearn,
            Note = "系统/应用/安全事件（.evtx）；排查问题必需，勿随意删除",
            NoteEn = "System, application and security events (.evtx); needed for troubleshooting, do not delete casually"
        },
        new()
        {
            Pattern = @"C:\Windows\Panther", Category = "日志", CategoryEn = "Logs",
            Title = "系统安装日志", TitleEn = "System setup logs",
            Cleanable = "谨慎", Priority = 45, Source = SrcMsLearn,
            Note = "安装与升级过程日志，安装失败时用于诊断",
            NoteEn = "Logs from installation and upgrade, used to diagnose failed setups"
        },
        new()
        {
            Pattern = @"C:\Windows\inf", Category = "日志", CategoryEn = "Logs",
            Title = "驱动安装信息", TitleEn = "Driver installation information",
            Cleanable = "禁止", Priority = 60, Source = SrcMsLearn,
            Note = "驱动安装所需的信息文件，删除会导致设备无法正常安装驱动",
            NoteEn = "Information files needed to install drivers; deleting them stops devices from installing drivers properly"
        },
        new()
        {
            Pattern = @"*\AppData\Local\Microsoft\Windows\WebCache", Category = "日志", CategoryEn = "Logs",
            Title = "系统 Web 缓存与历史", TitleEn = "System web cache and history",
            Cleanable = "谨慎", Priority = 45, Source = SrcHtg,
            Note = "系统级网络访问记录（WinINET 历史与缓存索引）",
            NoteEn = "System-level network access records (WinINET history and cache index)"
        },

        // ------------------------------------------ E. 错误报告与内存转储
        new()
        {
            Pattern = @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive", Category = "日志", CategoryEn = "Logs",
            Title = "错误报告归档", TitleEn = "Error report archive",
            Cleanable = "安全", Priority = 60, Source = SrcMsLearn,
            Note = "程序崩溃报告的归档，可清理，不影响程序运行",
            NoteEn = "Archive of application crash reports; safe to clean and it does not affect how programs run"
        },
        new()
        {
            Pattern = @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue", Category = "日志", CategoryEn = "Logs",
            Title = "错误报告队列", TitleEn = "Error report queue",
            Cleanable = "安全", Priority = 60, Source = SrcMsLearn,
            Note = "待上传的崩溃报告",
            NoteEn = "Crash reports waiting to be uploaded"
        },
        new()
        {
            Pattern = @"C:\Windows\Minidump", Category = "日志", CategoryEn = "Logs",
            Title = "蓝屏小转储", TitleEn = "Blue-screen minidumps",
            Cleanable = "谨慎", Priority = 55, Source = SrcMsLearn,
            Note = "蓝屏故障分析用的小型转储文件",
            NoteEn = "Small dump files used to analyse blue screens"
        },
        new()
        {
            Pattern = @"C:\Windows\LiveKernelReports", Category = "日志", CategoryEn = "Logs",
            Title = "活动内核报告", TitleEn = "Live kernel reports",
            Cleanable = "谨慎", Priority = 50, Source = SrcMsLearn,
            Note = "内核异常（未蓝屏）的诊断报告",
            NoteEn = "Diagnostic reports for kernel faults that did not blue-screen"
        },
        new()
        {
            Pattern = @"C:\Windows\MEMORY.DMP", Category = "日志", CategoryEn = "Logs",
            Title = "内核完整转储", TitleEn = "Full kernel dump",
            Cleanable = "谨慎", Priority = 55, Source = SrcMsLearn,
            Note = "体积常达数 GB；仅供故障分析，确认无需排查后可删",
            NoteEn = "Often several GB; only needed for fault analysis, delete it once no investigation is pending"
        },

        // ------------------------------------------------ F. 系统组件（禁删）
        new()
        {
            Pattern = @"C:\Windows\WinSxS", Category = "系统组件", CategoryEn = "System component",
            Title = "组件存储", TitleEn = "Component store",
            Cleanable = "禁止", Priority = 80, Source = SrcMsLearn,
            Note = "系统组件多版本备份；与系统其他目录大量文件硬链接共享同一份物理数据，手工删除会破坏系统，须用 DISM / 磁盘清理",
            NoteEn = "Multi-version backup of system components; many of its files share physical data with other system "
                   + "directories through hardlinks, so deleting by hand breaks the system. Use DISM or Disk Cleanup instead"
        },
        new()
        {
            Pattern = @"C:\Windows\System32", Category = "系统组件", CategoryEn = "System component",
            Title = "系统核心文件", TitleEn = "Core system files",
            Cleanable = "禁止", Priority = 80, Source = SrcMsLearn,
            Note = "系统运行必需的核心文件与驱动",
            NoteEn = "Core files and drivers required for the system to run"
        },
        new()
        {
            Pattern = @"C:\Windows\SysWOW64", Category = "系统组件", CategoryEn = "System component",
            Title = "32 位兼容层", TitleEn = "32-bit compatibility layer",
            Cleanable = "禁止", Priority = 80, Source = SrcMsLearn,
            Note = "供 32 位程序调用的系统文件",
            NoteEn = "System files called by 32-bit programs"
        },
        new()
        {
            Pattern = @"C:\Windows\Installer", Category = "系统组件", CategoryEn = "System component",
            Title = "MSI 安装缓存", TitleEn = "MSI installer cache",
            Cleanable = "禁止", Priority = 75, Source = SrcMsLearn,
            Note = "软件修复/卸载/更新所需的安装包缓存，删除会导致后续无法卸载或修复",
            NoteEn = "Package cache needed to repair, uninstall or update software; deleting it later leaves those actions impossible"
        },
        new()
        {
            Pattern = @"C:\Windows\System32\DriverStore", Category = "系统组件", CategoryEn = "System component",
            Title = "驱动仓库", TitleEn = "Driver store",
            Cleanable = "禁止", Priority = 75, Source = SrcMsLearn,
            Note = "驱动安装与回滚依赖，勿手工删除",
            NoteEn = "Required for driver installation and rollback; do not delete by hand"
        },
        new()
        {
            Pattern = @"C:\Windows\Microsoft.NET", Category = "系统组件", CategoryEn = "System component",
            Title = ".NET 运行时程序集", TitleEn = ".NET runtime assemblies",
            Cleanable = "禁止", Priority = 75, Source = SrcMsLearn,
            Note = ".NET Framework 运行时与程序集，大量软件依赖",
            NoteEn = ".NET Framework runtime and assemblies, which a great deal of software depends on"
        },

        // ------------------------------------------ G. 用户数据与系统保留区
        new()
        {
            Pattern = @"*\Downloads", Category = "用户数据", CategoryEn = "User data",
            Title = "下载目录", TitleEn = "Downloads",
            Cleanable = "谨慎", Priority = 40, Source = SrcMsLearn,
            Note = "用户下载的文件，含安装包/文档等，需自行确认",
            NoteEn = "Files the user downloaded, including installers and documents; check them yourself before deleting"
        },
        new()
        {
            Pattern = @"*\Documents", Category = "用户数据", CategoryEn = "User data",
            Title = "文档目录", TitleEn = "Documents",
            Cleanable = "谨慎", Priority = 40, Source = SrcMsLearn,
            Note = "用户文档",
            NoteEn = "User documents"
        },
        new()
        {
            Pattern = @"*\Pictures", Category = "用户数据", CategoryEn = "User data",
            Title = "图片目录", TitleEn = "Pictures",
            Cleanable = "谨慎", Priority = 40, Source = SrcMsLearn,
            Note = "用户图片",
            NoteEn = "User pictures"
        },
        new()
        {
            Pattern = @"*\Videos", Category = "用户数据", CategoryEn = "User data",
            Title = "视频目录", TitleEn = "Videos",
            Cleanable = "谨慎", Priority = 40, Source = SrcMsLearn,
            Note = "用户视频（通常是占用最大的用户目录）",
            NoteEn = "User videos (usually the largest of the user directories)"
        },
        new()
        {
            Pattern = @"*\Desktop", Category = "用户数据", CategoryEn = "User data",
            Title = "桌面", TitleEn = "Desktop",
            Cleanable = "谨慎", Priority = 40, Source = SrcMsLearn,
            Note = "桌面文件",
            NoteEn = "Files on the desktop"
        },
        new()
        {
            Pattern = @"C:\$Recycle.Bin", Category = "用户数据", CategoryEn = "User data",
            Title = "回收站", TitleEn = "Recycle Bin",
            Cleanable = "谨慎", Priority = 40, Source = SrcMsLearn,
            Note = "已删除文件的暂存区；清空后不可恢复（属默认排除项）",
            NoteEn = "Staging area for deleted files; emptying it cannot be undone (excluded by default)"
        },
        new()
        {
            Pattern = @"C:\System Volume Information", Category = "系统组件", CategoryEn = "System component",
            Title = "系统还原与卷影副本", TitleEn = "System Restore and shadow copies",
            Cleanable = "禁止", Priority = 70, Source = SrcMsLearn,
            Note = "还原点与卷影复制数据；须通过「系统属性 → 系统保护」管理（属默认排除项）",
            NoteEn = "Restore points and volume shadow copy data; manage it from System Properties -> System Protection "
                   + "(excluded by default)"
        },

        // -------------------------------------------- H. 常见第三方应用
        new()
        {
            Pattern = @"*\Documents\WeChat Files", Category = "应用数据", CategoryEn = "Application data",
            Title = "微信聊天记录与文件", TitleEn = "WeChat chat history and files",
            Cleanable = "禁止", Priority = 80, Source = SrcVendor,
            Note = "含图片、视频、文档、语音；删除等于删除聊天记录",
            NoteEn = "Holds images, videos, documents and voice messages; deleting it deletes the chat history"
        },
        new()
        {
            Pattern = @"*\Documents\xwechat_files", Category = "应用数据", CategoryEn = "Application data",
            Title = "微信（新版）聊天数据", TitleEn = "WeChat (new version) chat data",
            Cleanable = "禁止", Priority = 80, Source = SrcVendor,
            Note = "微信 4.x 的数据目录，内容与旧版 WeChat Files 同类",
            NoteEn = "Data directory of WeChat 4.x; the same kind of content as the older WeChat Files"
        },
        new()
        {
            Pattern = @"*\AppData\Roaming\Tencent", Category = "应用数据", CategoryEn = "Application data",
            Title = "腾讯软件数据", TitleEn = "Tencent software data",
            Cleanable = "谨慎", Priority = 50, Source = SrcVendor,
            Note = "QQ / 微信等客户端的配置与本地缓存",
            NoteEn = "Configuration and local caches of clients such as QQ and WeChat"
        },
        new()
        {
            Pattern = @"*\AppData\Local\Tencent", Category = "应用数据", CategoryEn = "Application data",
            Title = "腾讯软件本地缓存", TitleEn = "Tencent software local cache",
            Cleanable = "谨慎", Priority = 50, Source = SrcVendor,
            Note = "含聊天文件缓存、图片缓存等",
            NoteEn = "Includes chat file caches and image caches"
        },
        new()
        {
            Pattern = @"*\AppData\Roaming\DingTalk", Category = "应用数据", CategoryEn = "Application data",
            Title = "钉钉数据目录", TitleEn = "DingTalk data directory",
            Cleanable = "谨慎", Priority = 50, Source = SrcVendor,
            Note = "聊天记录、文件缓存、日志",
            NoteEn = "Chat history, file caches and logs"
        },
        new()
        {
            Pattern = @"*\AppData\Local\DingTalk", Category = "应用数据", CategoryEn = "Application data",
            Title = "钉钉本地缓存", TitleEn = "DingTalk local cache",
            Cleanable = "谨慎", Priority = 50, Source = SrcVendor,
            Note = "安装与运行缓存",
            NoteEn = "Installation and runtime caches"
        },
        new()
        {
            Pattern = @"*\AppData\Roaming\Microsoft\Teams", Category = "应用数据", CategoryEn = "Application data",
            Title = "Teams 缓存", TitleEn = "Teams cache",
            Cleanable = "安全", Priority = 50, Source = SrcVendor,
            Note = "会议与聊天缓存，客户端会自动重建",
            NoteEn = "Meeting and chat caches; the client rebuilds them automatically"
        },
        new()
        {
            Pattern = @"*\AppData\Roaming\Slack", Category = "应用数据", CategoryEn = "Application data",
            Title = "Slack 缓存", TitleEn = "Slack cache",
            Cleanable = "安全", Priority = 45, Source = SrcVendor,
            Note = "消息与文件缓存",
            NoteEn = "Message and file caches"
        },
        new()
        {
            Pattern = @"*\AppData\Roaming\Doubao", Category = "应用数据", CategoryEn = "Application data",
            Title = "豆包客户端数据", TitleEn = "Doubao client data",
            Cleanable = "谨慎", Priority = 45, Source = SrcVendor,
            Note = "客户端配置与缓存",
            NoteEn = "Client configuration and caches"
        },
        new()
        {
            Pattern = @"*\AppData\Local\Doubao", Category = "应用数据", CategoryEn = "Application data",
            Title = "豆包本地缓存", TitleEn = "Doubao local cache",
            Cleanable = "谨慎", Priority = 45, Source = SrcVendor,
            Note = "模型与运行缓存",
            NoteEn = "Model and runtime caches"
        },

        // ------------------------------------------------ I. 开发工具链
        new()
        {
            Pattern = @"*\.nuget\packages", Category = "开发工具", CategoryEn = "Developer tools",
            Title = "NuGet 本机包缓存", TitleEn = "NuGet local package cache",
            Cleanable = "谨慎", Priority = 55, Source = SrcMsLearn,
            Note = "解压后的包缓存；删除后需重新还原（离线环境慎删）",
            NoteEn = "Extracted package cache; deleting it forces a restore (think twice on offline machines)"
        },
        new()
        {
            Pattern = @"*\AppData\Local\pip\Cache", Category = "开发工具", CategoryEn = "Developer tools",
            Title = "pip 下载缓存", TitleEn = "pip download cache",
            Cleanable = "安全", Priority = 55, Source = SrcVendor,
            Note = "Python 包下载缓存，可安全清理",
            NoteEn = "Python package download cache; safe to clean"
        },
        new()
        {
            Pattern = @"*\AppData\Local\npm-cache", Category = "开发工具", CategoryEn = "Developer tools",
            Title = "npm 缓存", TitleEn = "npm cache",
            Cleanable = "安全", Priority = 55, Source = SrcVendor,
            Note = "Node.js 包缓存",
            NoteEn = "Node.js package cache"
        },
        new()
        {
            Pattern = @"*\AppData\Roaming\npm-cache", Category = "开发工具", CategoryEn = "Developer tools",
            Title = "npm 缓存（旧版路径）", TitleEn = "npm cache (legacy path)",
            Cleanable = "安全", Priority = 50, Source = SrcVendor,
            Note = "旧版 Node.js 的包缓存位置",
            NoteEn = "Package cache location used by older Node.js versions"
        },
        new()
        {
            Pattern = @"*\AppData\Roaming\Code\Cache", Category = "开发工具", CategoryEn = "Developer tools",
            Title = "VS Code 缓存", TitleEn = "VS Code cache",
            Cleanable = "安全", Priority = 55, Source = SrcVendor,
            Note = "编辑器缓存，可安全清理",
            NoteEn = "Editor cache; safe to clean"
        },
        new()
        {
            Pattern = @"*\AppData\Roaming\Code\CachedData", Category = "开发工具", CategoryEn = "Developer tools",
            Title = "VS Code 编译缓存", TitleEn = "VS Code compiled data cache",
            Cleanable = "安全", Priority = 55, Source = SrcVendor,
            Note = "语言服务的编译产物缓存",
            NoteEn = "Cache of compiled output from language services"
        }
    };
}
