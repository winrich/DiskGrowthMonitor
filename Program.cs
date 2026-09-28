using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DiskGrowthMonitor.Cli;
using DiskGrowthMonitor.Models;
using DiskGrowthMonitor.Native;
using DiskGrowthMonitor.Services;
using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor;

internal static class Program
{
    /// <summary>
    /// 入口只做一件事：兜底。任何逃出 <see cref="Run"/> 的异常都在这里落盘 ——
    /// 2026-09-24 的 0xE0434352 事故（提权档接管父控制台后崩溃，窗口全黑、日志只剩握手行）
    /// 正是因为异常输出既进不了窗口、也没进日志，事后只能靠推断定位根因。
    /// </summary>
    private static int Main(string[] args)
    {
        // 🔴 语言判定必须是本进程的**第一件事**，而且必须只走 P/Invoke：
        //   ① 它决定之后所有提示文本的措辞，包括「参数错误」与崩溃报告，因此不能晚于任何输出；
        //   ② Run() 开头会把控制台输出代码页切成 UTF-8(65001)，一旦切过，GetConsoleOutputCP
        //      就再也读不到中文代码页（936/950/54936），「界面中文 且 代码页中文」这个双条件
        //      将永远不成立 —— 2026-09-24 第 57 轮实施时实测确认，故顺序不可调换；
        //   ③ 不得触碰任何 Console 成员（理由见 SystemNative.TryGetLanguageInfo 的注释）。
        try { Lang.Initialize(LanguageProbe.Resolve(args)); }
        catch { /* 语言探查失败：保留默认档（英文），不影响扫描主流程 */ }

        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            ReportFatal(args, ex);
            return 1;
        }
        finally
        {
            // 第 57 轮 R3 修法：Run() 开头的 Console.OutputEncoding=UTF8 会把**控制台窗口**的输出
            // 代码页永久改成 65001（同一个窗口里之后再启动的进程都会读到 65001），退出前写回原值。
            // 🔴 必须是本进程的最后一步：恢复之后 Console 的编码器与代码页就不一致了，再输出会乱码。
            //   ReportFatal 在此**之前**已完成（它在 catch 里，finally 之后才轮到这一行）。
            try { LanguageProbe.RestoreConsoleCodePage(); } catch { }
        }
    }

    /// <summary>
    /// 崩溃兜底：把「类型 + 消息 + 堆栈」写到所有还活着的输出目标上。自身绝不抛异常，
    /// 否则连退出码都保不住。
    /// </summary>
    private static void ReportFatal(string[] args, Exception ex)
    {
        string text = Lang.T("[崩溃] ", "[CRASH] ") + ex.GetType().FullName + Lang.T("：", ": ") + ex.Message
                      + Environment.NewLine + ex.StackTrace + Environment.NewLine;

        // 1) 屏幕：控制台可能已 FreeConsole 或句柄已失效，打印失败属预期情况，忽略
        try { Console.Error.WriteLine(); Console.Error.WriteLine(text); } catch { }

        // 2) 文件：提权档写进父进程正在中继的日志（父进程见到退出码非 0 会把它打出来）；
        //    普通档没有该参数，退化写 exe 同级 crash.log —— 保证任何崩溃都留下证据
        try
        {
            string? path = FindArgValue(args, "--console-log");
            if (path == null) path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }
        catch { /* 磁盘满 / 无权限：已尽力，不再冒险 */ }
    }

    /// <summary>取 <c>--name value</c> 形式的开关取值（只认两参数写法）。</summary>
    private static string? FindArgValue(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    /// <summary>
    /// 开发者模式（<c>--dev</c>）是否开启。由 <see cref="Run"/> 在解析参数后立即赋值，
    /// 供后续各处的 <see cref="Dev(string)"/> 判定。
    /// </summary>
    private static bool DevMode;

    /// <summary>
    /// 仅在开发者模式下输出的诊断行。
    ///
    /// 这些信息面向开发与排障（环境明细、增量决策依据、估算与探测过程、知识库/跳过清单记账），
    /// 对「这次扫了多少、涨在哪」这个核心问题没有贡献，却能把关键数字淹没在几十行里 ——
    /// 因此默认关闭，用 <c>--dev</c> 打开。
    /// 注意：这里只是不打印，统计与判定逻辑一律照常执行，报告与数据库内容不受影响。
    /// </summary>
    private static void Dev(string message)
    {
        if (DevMode) Console.WriteLine(message);
    }

    private static int Run(string[] args)
    {
        // 把控制台切到 UTF-8，保证中文输出不依赖代码页。
        // ⚠ 这一行**必须**留在 Main 的语言探查之后：它会改变控制台输出代码页（→ 65001），
        //   而语言判定要用「控制台本来的代码页是否是中文代码页」当条件之一。
        //   退出前由 Main 的 finally 恢复原代码页（见 LanguageProbe.RestoreConsoleCodePage）。
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { /* 部分终端不支持切换编码，忽略即可 */ }

        var opt = Options.Parse(args);
        if (opt.Error != null)
        {
            Console.Error.WriteLine(Lang.T("参数错误：", "Argument error: ") + opt.Error);
            Console.Error.WriteLine();
            Console.Error.Write(Options.Help());
            return 2;
        }
        if (opt.ShowHelp)
        {
            Console.Write(Options.Help());
            return 0;
        }

        // 开发者模式须尽早生效：后面从「环境」诊断行开始就要按它决定是否打印
        DevMode = opt.DevMode;

        // ---------- 提权档输出接管（子进程侧，方案 B：AttachConsole 回父窗口） ----------
        // 带 --attach-parent 时：先按 PoC（_optdb/attachpoc/）验证过的四步接管父进程控制台
        // （FreeConsole → AttachConsole → SetStdHandle → Console.SetOut 重挂）。
        // 成功 ⇒ Console.Out 直达父窗口，进度条保持完整交互；日志文件只写一行 [attach] ok
        // 握手标记（父进程据此转为静默等待，避免心跳行插进子进程的原地重绘块）。
        // 失败 ⇒ 写 [attach] fail，退回下面的「输出落盘 + 父进程中继」（方案 A）行为。
        bool attachAttempted = false;
        bool attachOk = false;
        if (opt.AttachParentPid is { Length: > 0 } && int.TryParse(opt.AttachParentPid, out int attachPid))
        {
            attachAttempted = true;
            attachOk = ConsoleAttach.TryAttach(attachPid);
            if (opt.ConsoleLogPath is { Length: > 0 })
            {
                try { File.WriteAllText(opt.ConsoleLogPath, attachOk ? "[attach] ok\n" : "[attach] fail\n", new UTF8Encoding(false)); }
                catch { /* 标记写不进：父进程等不到标记会保持中继模式；attach 已成功时输出本身直达父窗口，无碍 */ }
            }
        }

        // ---------- 提权档日志中继（子进程侧） ----------
        // 仅在 attach 未接管成功时才需要：本进程输出无法进入父进程的窗口
        // （Verb="runas" 只能 UseShellExecute=true，.NET Framework 禁止该模式重定向流）。
        // 因此把全部控制台输出转写进日志文件，由父进程轮询回显；同时隐藏自带的新控制台
        // 窗口（内容都进日志了，留着空壳只会让人以为程序卡死）。
        // 打不开日志时退回「写自己的控制台」= 修复前的行为，父进程会检测到日志不增长并提示。
        StreamWriter? consoleLog = null;
        if (!attachOk && opt.ConsoleLogPath is { Length: > 0 })
        {
            try
            {
                // 显式不带 BOM：父进程按 UTF-8 增量解码，BOM 字节会被当成正文首字符。
                // 已尝试过 attach 时用追加模式：文件里已有握手标记行，截断会让父进程读取位置错位
                consoleLog = new StreamWriter(opt.ConsoleLogPath, attachAttempted, new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(consoleLog);
                Console.SetError(consoleLog);
                ConsoleWindow.TryHide();
            }
            catch (Exception ex)
            {
                consoleLog = null;
                // 走到这里说明「输出既没接管进父窗口、也写不进日志」：若之前已 FreeConsole，
                // 本进程此刻没有任何控制台，直接写 Console 会二次失败。补一个自带窗口兜底。
                if (attachAttempted && !attachOk) ConsoleAttach.EnsureConsole();
                try
                {
                    Console.Error.WriteLine(Lang.F("[提权] 无法创建输出日志 {0}：{1}",
                        "[Elevate] Could not create the output log {0}: {1}", opt.ConsoleLogPath, ex.Message));
                    Console.Error.WriteLine(Lang.T("       将改用本进程自带窗口直接输出（父窗口将看不到本次结果）。",
                        "          Falling back to this process's own window (the parent window will not see the result)."));
                }
                catch { /* 连兜底控制台也不可用：宁可静默，也不能因打印错误信息而崩 */ }
            }
        }

        // ---------- 以管理员身份自举 ----------
        // 只在「本次确实要扫描」时尝试：--list-* / --reset-db 这类管理命令不需要管理员权限，
        // 无谓地弹一次 UAC 只会打扰用户。
        // 三个跳过条件（任一成立即不重启）：
        //   · --no-elevate：用户显式关闭；
        //   · --elevated：本进程已是自举产物（硬保险，防御「提权未生效」时无限重启）；
        //   · 输出被重定向：提权后进程运行在新控制台，日志进不了当前重定向流 ——
        //     与其让脚本拿到「有退出码但没日志」的结果，不如明确提示后按原权限跑完。
        bool willScan = opt.DriveSpecs.Count > 0 || opt.RootDirs.Count > 0;
        if (willScan && !opt.NoElevate && !opt.ElevatedChild && !ElevationHelper.IsElevated())
        {
            if (Console.IsOutputRedirected)
            {
                Console.WriteLine(Lang.T("[提权] 检测到输出已重定向（脚本调用），跳过自举：",
                                         "[Elevate] Output is redirected (script invocation); skipping self-elevation:"));
                Console.WriteLine(Lang.T("       提权后的进程运行在新控制台，日志无法进入当前重定向流。",
                                         "          The elevated process runs in a new console, so its log cannot reach the current redirected stream."));
                Console.WriteLine(Lang.T("       如需管理员权限，请手动以管理员身份运行本程序。",
                                         "          To run with administrator rights, start this program as administrator manually."));
            }
            else if (ElevationHelper.TryRelaunchElevated(args, out int childExit))
            {
                // 子进程已在新窗口跑完；本进程的唯一职责就是把退出码原样传回去
                return childExit;
            }
            Console.WriteLine();
        }

        // 数据库固定放在 exe 所在目录（不提供命令行开关），
        // 保证无论从哪个工作目录启动都指向同一份数据。
        // 用 AppDomain.CurrentDomain.BaseDirectory 而非 Assembly.Location：后者在影子复制等
        // 场景下不是 exe 目录。注意不能用 AppContext.BaseDirectory —— 它是 .NET Framework 4.6
        // 才引入的，net45 目标取不到（编译期即报错）。
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string dbPath = Path.Combine(baseDir, "disk_growth.db");

        // 颜色先于首行输出启用：交互模式下才开启（日志中继/重定向会禁用，避免 ANSI 码污染），
        // 终端不支持 VT（WS2012 等）时 ConsoleStyle 内部自动降级为纯文本。
        ConsoleStyle.Enable(consoleLog == null);
        Console.WriteLine(ConsoleStyle.Section(Lang.T("DiskGrowthMonitor · 磁盘空间增长监控",
                                                        "DiskGrowthMonitor - disk space growth monitor")));
        Console.WriteLine();

        if (opt.ShowEnv)
        {
            RunShowEnv(opt);
            return 0;
        }

        if (opt.ListDrives)
        {
            PrintDrives();
            return 0;
        }

        using var db = new DatabaseService(dbPath);
        Dev(Lang.F("[环境] 数据库：{0}", "[Env] Database: {0}", db.DatabasePath));
        // 权限状态显式打印：它是「USN 增量能否生效」的唯一决定因素。
        // 以前只能从「状态与上一轮不对应」这类间接提示里倒推，现在第一眼就能看到。
        Dev(Lang.F("[环境] 权限：{0}", "[Env] Privilege: {0}",
            ElevationHelper.IsElevated()
                ? Lang.T("管理员（可读 USN 变更日志 ⇒ 增量可用）",
                         "Administrator (the USN change journal is readable => incremental available)")
                : Lang.T("普通用户（USN 增量不可用 ⇒ 每轮全量扫描）",
                         "Standard user (USN incremental unavailable => full scan every run)")));
        if (opt.DbArgSeen)
            Console.WriteLine(Lang.T(
                "[提示] --db 开关已取消：数据库固定为 exe 目录下的 disk_growth.db，本次已忽略你传入的路径。",
                "[Note] The --db switch has been removed: the database is fixed to disk_growth.db next to the exe; "
                + "the path you passed was ignored."));

        // 目录用途知识库：首次运行/程序升级时补齐缺失的内置条目（只增不改，用户编辑不会被覆盖）
        var knowledge = new KnowledgeService(db);
        if (knowledge.SeedInserted > 0)
            Dev(Lang.F("[知识库] 已补齐 {0} 条内置目录用途条目，当前生效 {1} 条。",
                       "[Knowledge] Seeded {0} built-in directory-purpose entries; {1} in effect.",
                       knowledge.SeedInserted, knowledge.EffectiveCount));

        // ---------- --reset-db：清空数据库（交互确认 → 重置 → 退出，不扫描） ----------
        if (opt.ResetDb)
            return RunReset(db);

        if (opt.ResetSkips)
        {
            int removed = db.ResetPersistentSkips();
            Console.WriteLine(Lang.F(
                "[跳过清单] 已清空 {0} 条「权限不足 / IO 错误」记录，本次将重新尝试扫描这些目录。",
                "[Skip list] Cleared {0} \"access denied / IO error\" records; these directories will be retried this run.",
                FormatUtil.Count(removed)));
        }

        if (opt.ListSkips)
        {
            PrintSkips(db, opt.NoKnowledgeRescan ? null : knowledge);
            return 0;
        }

        if (opt.ListKnowledge)
        {
            PrintKnowledge(db);
            return 0;
        }

        // ---------- 解析扫描根 ----------
        if (opt.DriveSpecs.Count == 0 && opt.RootDirs.Count == 0)
        {
            Console.Error.WriteLine(Lang.T(
                "错误：必须用 -d/--drive 指定盘符，或用 -r/--root 指定扫描目录。使用 --help 查看用法。",
                "Error: specify a drive with -d/--drive, or a directory with -r/--root. Use --help for usage."));
            return 2;
        }

        var roots = ResolveRoots(opt);
        if (roots.Count == 0)
        {
            Console.Error.WriteLine(Lang.T("错误：没有解析到任何有效的扫描根。",
                                           "Error: no valid scan root could be resolved."));
            return 2;
        }

        Console.WriteLine(Lang.F("  扫描范围   {0}", "  Scan roots   {0}",
            string.Join(Lang.T("、", ", "), roots.Select(r => r.DisplayName))));

        // 环境探测：把「这台机器上应该怎么扫」从编译期常量变成运行期判断。
        // 三档目标环境（WS2012 / Win10 / Win11）能力差异不小，写死参数只能取三者交集、
        // 白白丢掉新系统与新硬件的收益。此处已完成提权自举，介质探测能拿到真实结果。
        // 语言探查结果由 Main 传入（此处不重新探测：控制台代码页早已被切成 UTF-8，重探会得到错的结论）。
        var sysProfile = EnvironmentProbe.Probe(roots.Select(r => r.Path), Lang.Info);
        var sysStrategy = EnvironmentProbe.Decide(sysProfile, opt.EnumEngine);
        Dev(Lang.T("[环境] ", "[Env] ") + EnvironmentProbe.FormatBrief(sysProfile, sysStrategy));
        // 语言行把「结论 + 原始探测值」一并打出（下放 --dev）：语言判错的唯一表现就是「整篇都是英文」，
        // 不给出原始值就只能靠猜（尤其猜不到「界面中文 + 代码页 65001 → 判英文」这种组合）。
        var langInfo = sysProfile.Language;
        Dev(Lang.F(
            "[环境] 语言：{0}（界面 {1} · 控制台代码页 输出 {2} / 输入 {4}{3}）",
            "[Env] Language: {0} (UI {1} - console code page out {2} / in {4}{3})",
            Lang.DescribeLanguage(langInfo?.Language ?? Lang.Current),
            string.IsNullOrEmpty(langInfo?.UiCultureName) ? Lang.T("未知", "unknown") : langInfo!.UiCultureName,
            langInfo?.ConsoleOutputCodePage ?? 0,
            (langInfo?.IsForced ?? false) ? Lang.T(" · 由 --lang 指定", " - forced by --lang") : string.Empty,
            langInfo?.ConsoleInputCodePage ?? 0));

        // 运行方式一行：管理员 + 三个开关。默认排除必须点明「知识库豁免」的存在——
        // 默认排除里的系统目录一旦命中知识库就会被放行，只说「默认排除」会与实际行为对不上
        // （2026-09-24 用户反馈第 3 条）。
        string modeAdmin = ElevationHelper.IsElevated()
            ? Lang.T("管理员（增量可用）", "admin (incremental available)")
            : Lang.T("普通用户（全量扫描）", "standard user (full scan)");
        string modeExcl = opt.NoDefaultExclude
            ? Lang.T("无默认排除", "no default exclusion")
            : opt.NoKnowledgeRescan
                ? Lang.T("默认排除", "default exclusions")
                : Lang.T("默认排除（知识库命中仍扫描）", "default exclusions (knowledge-base hits still scanned)");
        string modeDedup = opt.NoDedup ? Lang.T("不去重", "no dedup") : Lang.T("硬链去重", "hardlink dedup");
        string modeReparse = opt.FollowReparse ? Lang.T("跟随符号链接", "follow reparse points") : Lang.T("不跟随符号链接", "no reparse follow");
        Console.WriteLine(Lang.F("  运行方式   {0} · {1} · {2} · {3}",
            "  Mode   {0} - {1} - {2} - {3}", modeAdmin, modeExcl, modeDedup, modeReparse));
        Dev(Lang.F("[环境] 枚举引擎：{0}", "[Env] Enum engine: {0}", sysStrategy.EnumEngine));
        Console.WriteLine();

        // ---------- 阶段一：建立「上一轮」对比基准 ----------
        var prevRun = db.GetLatestRun();
        DateTime? prevScanTime = prevRun?.ScanTime;
        long previousRunId = prevRun?.Id ?? 0;

        Console.WriteLine(ConsoleStyle.Section(Lang.T("▸ 1/5  建立对比基准", "▸ 1/5  Establish comparison baseline")));
        var baselines = db.BeginPrevSnapshot(roots);
        long prevRows = baselines.Sum(b => b.CopiedRows);
        bool isFirstRun = prevRows == 0;
        // 按根交代基准来源：dir_snapshots 按「根」轮换，每个根的基准都是它自己最近一次扫描
        // 的快照 —— 多盘轮换（-d c,d → -d c → -d c,d）时，扩回的根基准可能来自更早的批次，
        // 这里与报告一样如实标注，绝不把它说成「上一轮」。
        foreach (var b in baselines)
        {
            var root = roots.First(r => r.Key.Equals(b.RootKey, StringComparison.OrdinalIgnoreCase));
            if (b.CopiedRows > 0)
            {
                string when = b.BaselineRunId.HasValue
                    ? Lang.F("第 {0} 轮 · {1}", "run #{0} - {1}",
                             b.BaselineRunId.Value, FormatUtil.Timestamp(b.BaselineTime ?? DateTime.MinValue))
                    : Lang.T("时间未知（旧版本数据库）", "unknown (database from an older version)");
                Console.WriteLine(Lang.F(
                    "   {0}   已固化 {1} 条基准（上次 {2}）",
                    "   {0}   pinned {1} directory records as baseline (last {2})",
                    root.DisplayName, FormatUtil.Count(b.CopiedRows), when));
            }
            else
            {
                Console.WriteLine(Lang.F(
                    "   {0}   首次运行（本次结果将作为基准）",
                    "   {0}   first run (the result becomes its baseline)",
                    root.DisplayName));
            }
        }
        Console.WriteLine();

        // ---------- 增量（USN）与复用策略准备 ----------
        // 关键约束：USN 对比区间 [基准位置, 现在] 必须覆盖「该根基准行采集之后」的全部变更。
        // USN 状态在扫描完成后记录（state.RunId = 记录它的批次），而 dir_snapshots 按根轮换、
        // 每根的基准行来自该根最近一次扫描（root_last_scan.run_id）。因此按根放宽后的判据是：
        //   state.RunId ≤ 该根最近扫描批次
        // USN 区间起点早于基准采集点时只是「多含一段变更」（多余子树会被重扫，安全）；
        // 反之若 USN 基准晚于基准行，中间的变更不在区间内，复用会漏检 ⇒ 必须全量。
        var settings = ScanSettings.From(opt);
        var stageWriter = new StageWriter(db);
        var policies = new Dictionary<string, ReusePolicy>(StringComparer.OrdinalIgnoreCase);
        var pendingUsn = new List<(char Letter, ulong JournalId, long NextUsn)>();

        if (isFirstRun)
        {
            Dev(Lang.T("[增量] 首次运行无对比基准，本次全量扫描（扫描完成后记录 USN 状态，下次运行起可增量）。",
                       "[Incremental] First run has no baseline; full scan this time (USN state is recorded "
                       + "afterwards, so the next run can be incremental)."));
        }
        else if (settings.ForceFullScan)
        {
            Dev(Lang.T("[增量] 已指定 --full，本次强制全量扫描。",
                       "[Incremental] --full was specified; a full scan is forced for this run."));
        }
        else
        {
            var lastScans = db.GetRootLastScans();

            bool UsnCoversRoot(ScanRoot root, DatabaseService.VolumeStateInfo s) =>
                lastScans.TryGetValue(root.Key, out var ls) && s.RunId <= ls.RunId;

            foreach (var letter in roots
                         .Select(r => RootVolumeLetter(r.Path))
                         .Where(l => l.HasValue)
                         .Select(l => l!.Value)
                         .Distinct())
            {
                var saved = db.GetVolumeState(letter);
                if (saved == null)
                {
                    Dev(Lang.F("[增量] {0}: 暂无上次 USN 日志状态，本次全量扫描（下次运行起可增量）。",
                               "[Incremental] {0}: no saved USN journal state; full scan this time "
                               + "(the next run can be incremental).", letter));
                    continue;
                }

                var volRoots = roots.Where(r => RootVolumeLetter(r.Path) == letter).ToList();
                if (!volRoots.Any(r => UsnCoversRoot(r, saved)))
                {
                    // 注意：最常见的原因不是「根集合变化」，而是上一轮没有以管理员身份运行 ——
                    // 读 USN 变更日志要先打开卷设备 \\.\X:（需要管理员权限），打不开就拿不到新的
                    // (JournalId, NextUsn)，卷状态写不回去 ⇒ 保存值永远停在最后一次管理员运行的位置。
                    // 所以这里必须把这条原因排在前面，否则会把排查方向带偏。
                    Dev(Lang.F("[增量] {0}: USN 状态与各扫描根的最近扫描批次不对应，本次全量扫描。",
                               "[Incremental] {0}: the USN state does not line up with any scanned root's "
                               + "latest run; full scan this time.", letter));
                    Dev(Lang.T("      最常见的原因是上一轮未以管理员身份运行（读 USN 变更日志需要管理员权限，"
                               + "卷状态写不回去）；其次才是上次扫描曾回退或扫描根集合变化。",
                               "      The most common cause is that the previous run was not elevated (reading the USN "
                               + "change journal needs administrator rights, so the volume state could not be saved); "
                               + "less often, the last scan was rolled back or the set of scan roots changed."));
                    continue;
                }

                var change = UsnJournalService.Collect(letter, saved.JournalId, saved.NextUsn,
                    msg => Dev(Lang.F("      [增量:{0}:] {1}", "      [Incremental:{0}:] {1}", letter, msg)));
                if (change == null) continue; // 卷不可读，原因已输出

                pendingUsn.Add((letter, change.JournalId, change.NextUsn));

                if (change.FullRescan)
                {
                    // FullRescanReason 是 string?；`FullRescan = true` 的 11 处都同时赋了原因，
                    // 这里的 ?? 只为一处不落（旧写法是 $"" 插值，null 本来也渲染成空串，行为不变）
                    Dev(Lang.F("      [增量:{0}:] {1}，本次全量扫描。",
                               "      [Incremental:{0}:] {1}; full scan this time.",
                               letter, change.FullRescanReason ?? string.Empty));
                    continue;
                }

                foreach (var root in volRoots)
                {
                    if (!UsnCoversRoot(root, saved))
                    {
                        Dev(Lang.F("      [增量:{0}:] 「{1}」的最近一次扫描早于该卷的 USN 基准批次（或无记录），"
                                   + "该根本次全量扫描。",
                                   "      [Incremental:{0}:] \"{1}\" was last scanned before this volume's USN "
                                   + "baseline run (or has no record); this root is fully rescanned.",
                                   letter, root.DisplayName));
                        continue;
                    }

                    // 路径盘符 ≠ 真实卷（根位于 junction / 符号链接 / 挂载点之后）：按盘符读
                    // USN 日志会读到别的卷 ⇒ 变更集恒为空 ⇒ 会被误判「无变更」并整根复用
                    // 过期快照。故该根直接放弃增量（不登记复用策略 ⇒ 本轮全量扫描）。
                    if (!IsOnDriveRootVolume(root.Path, letter))
                    {
                        Dev(Lang.F("      [增量:{0}:] 「{1}」位于 junction / 符号链接 / 挂载点之后"
                                   + "（真实卷不是 {0}:），按盘符读 USN 日志会读到别的卷，该根本次全量扫描。",
                                   "      [Incremental:{0}:] \"{1}\" lies behind a junction / symlink / mount point "
                                   + "(its real volume is not {0}:), so reading the USN journal by drive letter would "
                                   + "read another volume; this root is fully rescanned.",
                                   letter, root.DisplayName));
                        continue;
                    }

                    if (change.NoChanges)
                        Dev(Lang.F("      [增量:{0}:] 自上次扫描以来无任何文件系统变更，「{1}」将整体复用上一轮快照。",
                                   "      [Incremental:{0}:] no file-system changes since the last scan; \"{1}\" "
                                   + "reuses the previous snapshot as a whole.", letter, root.DisplayName));
                    else
                        Dev(Lang.F("      [增量:{0}:] 检测到 {1} 处变更，仅重扫涉及的 {2} 个目录子树。",
                                   "      [Incremental:{0}:] {1} changes detected; only the {2} affected directory "
                                   + "subtrees are rescanned.",
                                   letter,
                                   FormatUtil.Count(change.ChangedRecords),
                                   FormatUtil.Count(change.DirtyPaths.Count)));

                    // 该根必须有基准行才允许复用（无基准时无从复用）
                    if (db.CountPrevRows(root) > 0)
                        policies[root.Key] = new ReusePolicy(change.DirtyPaths, root.Path);
                }
            }
        }
        Console.WriteLine();

        // ---------- 阶段二：扫描并写入临时表 ----------
        Console.WriteLine(ConsoleStyle.Section(Lang.T("▸ 2/5  扫描", "▸ 2/5  Scan")));
        db.CreateStageTable();

        var skip = new SkipListService(settings.UseDefaultExclude, settings.FollowReparse, opt.Excludes,
            opt.NoKnowledgeRescan ? null : knowledge);

        var loadedSkips = db.GetSkipRecords();
        skip.LoadPersistent(loadedSkips);
        if (skip.PersistentSkipCount > 0)
        {
            int knowledgeHit = skip.KnowledgeRescanEnabled
                ? loadedSkips.Count(r => SkipRecord.IsPersistentSkip(r.Reason) && skip.IsKnowledgeRescued(r.Path))
                : 0;
            Dev(knowledgeHit > 0
                ? Lang.F("      已加载 {0} 条历史跳过记录（权限不足/IO 错误）；其中 {1} 条命中知识库，本次仍会重新尝试。",
                         "      Loaded {0} historical skip records (access denied / IO error); {1} of them match the "
                         + "knowledge base and will still be retried.",
                         FormatUtil.Count(skip.PersistentSkipCount), FormatUtil.Count(knowledgeHit))
                : Lang.F("      已加载 {0} 条历史跳过记录（权限不足/IO 错误），本次将直接跳过。",
                         "      Loaded {0} historical skip records (access denied / IO error); they will be skipped.",
                         FormatUtil.Count(skip.PersistentSkipCount)));
        }
        if (skip.KnowledgeRescanEnabled)
            Dev(Lang.T("      知识库豁免：命中目录用途知识库的目录（含其祖先目录）每轮都重新尝试扫描。",
                       "      Knowledge-base rescue: directories matching the directory-purpose knowledge base "
                       + "(and their ancestors) are retried every run."));

        var startedAt = DateTime.Now;
        // 进度显示：每个扫描根 2 行（进度条+当前路径）+ 1 行总计，原地重绘不刷屏。
        // 分母优先级：上轮快照目录数（最准、零成本）→ MFT 精确估算（首次扫描 + 管理员权限）
        //             → 卷已用容量（首次扫描 + 非管理员，仅整卷根适用）。
        var progressRoots = BuildProgressEstimates(db, roots, opt, knowledge, out string? estimateNotice);
        if (estimateNotice != null) Console.WriteLine(estimateNotice);

        // 进度条只在「输出直达控制台」时绘制：日志中继模式下 Console.Out 指向文件，
        // 光标控制字符混进日志会被父进程原样回显成乱码碎片
        var progressDisplay = new ConsoleProgressDisplay(progressRoots, interactive: consoleLog == null);
        progressDisplay.Start();

        var scanner = new DiskScanner(settings, skip, stageWriter,
            p => progressDisplay.Update(p.RootKey, p.RootDirs, p.RootBytes, p.CurrentPath));
        var stats = scanner.Scan(roots, policies);
        progressDisplay.Finish();

        // 后台写库线程上报的错误：中止本轮提交（暂存表属一次性数据，下轮 CreateStageTable 会重建，
        // 上一轮快照不受影响），把失败原因明确告诉用户，而不是带着残缺数据继续对比
        if (!string.IsNullOrEmpty(stats.WriteError))
        {
            Console.Error.WriteLine("      " + stats.WriteError);
            Console.Error.WriteLine(Lang.T("      本次结果未入库，上一轮快照不受影响；请检查磁盘空间后重试。",
                                           "      The result was not committed; the previous snapshot is unaffected. "
                                           + "Check free disk space and retry."));
            return 121;
        }

        Console.WriteLine(ConsoleStyle.Key(Lang.F("  ✓ 扫描完成   {0} 目录 · {1} 文件 · {2} · {3}",
                                                   "  ✓ Scan complete   {0} dirs - {1} files - {2} - {3}",
                                                   FormatUtil.Count(stats.TotalDirs), FormatUtil.Count(stats.TotalFiles),
                                                   FormatUtil.Bytes(stats.TotalBytes), FormatUtil.Duration(stats.ElapsedMs))));
        // 并行 / 增量复用 / 硬链去重 合并为一行（各自条件成立才出现，用 · 连接）
        var meta = new List<string>();
        if (stats.UsedThreads > 1)
            meta.Add(Lang.F("并行 {0} 根", "parallel {0} roots", stats.UsedThreads));
        if (stats.ReusedDirs > 0)
            meta.Add(Lang.F("增量复用 {0} 目录", "incremental reuse {0} dirs", FormatUtil.Count(stats.ReusedDirs)));
        if (stats.DedupedFileCount > 0)
            meta.Add(Lang.F("硬链去重 {0} 文件（{1}）", "hardlink dedup {0} files ({1})",
                            FormatUtil.Count(stats.DedupedFileCount), FormatUtil.Bytes(stats.DedupedBytes)));
        if (meta.Count > 0)
            Console.WriteLine("     " + string.Join(Lang.T(" · ", " - "), meta));
        // 默认排除与自定义排除分开报：合并成「排除 N」时，一旦默认排除目录命中知识库被豁免放行，
        // 就会出现「环境行写着默认排除启用、这里却是 排除 0」的自相矛盾数字（2026-09-24 用户反馈第 3 条）
        Console.WriteLine(Lang.F(
            "     跳过 默认排除 {0} · 自定义 {1} · 权限不足 {2} · 符号链接 {3} · IO {4}",
            "     Skipped  default {0} - custom {1} - access denied {2} - reparse {3} - IO {4}",
            stats.DefaultExcludedCount, stats.ExcludedCount - stats.DefaultExcludedCount,
            stats.AccessDeniedCount, stats.ReparseCount, stats.ErrorCount));
        if (stats.KnowledgeRescuedCount > 0)
            Dev(Lang.F("      知识库豁免：{0} 个目录虽命中排除/跳过规则，但因命中目录用途知识库，本次已重新尝试扫描。",
                       "      Knowledge-base rescue: {0} directories matched exclusion/skip rules but were retried "
                       + "because they match the directory-purpose knowledge base.",
                       FormatUtil.Count(stats.KnowledgeRescuedCount)));
        if (stats.EnumEngineSummary.Length > 0)
            Dev(Lang.F("      枚举引擎：{0}", "      Enum engine: {0}", stats.EnumEngineSummary));
        foreach (var note in stats.EnumEngineNotes)
            Dev("      · " + note);

        // 「扫描统计 vs 卷已用」的口径对照：只对「整卷扫描、且数据真在该卷上」的根成立。
        // 谓词顺带排除了 junction / 符号链接之后的根（路径盘符 ≠ 真实卷）——缺了这个守卫，
        // 就会拿 A 卷的统计值去减 B 卷的已用量，得出一个看似精确的错误结论。
        var volumeGaps = VolumeGap.Build(roots, stats.Roots, p =>
        {
            char? letter = RootVolumeLetter(p);
            return letter.HasValue && IsOnDriveRootVolume(p, letter.Value);
        });
        PrintVolumeGap(volumeGaps, stats);
        Console.WriteLine();

        // ---------- 阶段三：单事务提交换入新快照 ----------
        Console.WriteLine(ConsoleStyle.Section(Lang.T("▸ 3/5  写入数据库（两阶段提交）",
                                                         "▸ 3/5  Commit to database (two-phase)")));
        var run = new ScanRun
        {
            ScanTime = startedAt,
            Roots = string.Join(",", roots.Select(r => r.Key)),
            TotalFiles = stats.TotalFiles,
            TotalDirs = stats.TotalDirs,
            TotalBytes = stats.TotalBytes,
            ElapsedMs = stats.ElapsedMs,
            IsFirstRun = isFirstRun
        };
        db.CommitStaging(roots, run);
        Console.WriteLine(ConsoleStyle.Success(Lang.F("  ✓ 已提交 · 批次 #{0}", "  ✓ Committed - run #{0}", run.Id)));
        // 只在扫描根集合与本轮不同时提示：其余根各自最近一轮的快照被保留，
        // 之后这些根再次被扫到时即可直接与自己的上次结果对比（不再判「首次运行」）。
        if (db.LastRetainedRows > 0)
            Console.WriteLine(ConsoleStyle.Muted(Lang.F(
                "  已保留其他扫描根的历史快照 {0} 行（各自对应其最近一次扫描，扩回扫描范围时继续作为基准）",
                "  Kept {0} snapshot rows of other scan roots (each from its own latest scan; they remain the baseline)",
                FormatUtil.Count(db.LastRetainedRows))));

        // ---------- 保存 USN 状态（供下次增量） ----------
        // 增量路径：保存 Collect 返回的新位置；全扫路径：查询当前位置保存。
        // 查询需要管理员权限（要先打开卷设备 \\.\X:）：失败时卷状态写不回去，会导致下一轮
        // 又判定「不对应」而继续全量扫描 —— 所以这里必须把 Collect 给出的原因原样输出，
        // 不能再用空 lambda 吞掉，否则用户完全看不到「其实是没有管理员权限」这个真因。
        var stateSavedLetters = new HashSet<char>();
        foreach (var (letter, journalId, nextUsn) in pendingUsn)
        {
            db.SaveVolumeState(letter, journalId, nextUsn, run.Id);
            stateSavedLetters.Add(letter);
        }
        foreach (var letter in roots
                     .Select(r => RootVolumeLetter(r.Path))
                     .Where(l => l.HasValue).Select(l => l!.Value).Distinct())
        {
            if (stateSavedLetters.Contains(letter)) continue;
            var cur = UsnJournalService.Collect(letter, 0, 0,
                msg => Dev(Lang.F("      [USN状态:{0}:] {1}", "      [USN state:{0}:] {1}", letter, msg)));
            if (cur != null)
                db.SaveVolumeState(letter, cur.JournalId, cur.NextUsn, run.Id);
        }

        var pendingSkips = skip.TakePending();
        if (pendingSkips.Count > 0)
        {
            db.SaveSkipRecords(pendingSkips, run.Id);
            Dev(Lang.F("      跳过清单已更新 {0} 条记录。",
                       "      Skip list updated with {0} records.", FormatUtil.Count(pendingSkips.Count)));
        }
        Console.WriteLine();

        // ---------- 阶段四：对比分析 ----------
        Console.WriteLine(ConsoleStyle.Section(Lang.T("▸ 4/5  对比上一轮快照", "▸ 4/5  Compare with previous snapshot")));
        var analyzer = new GrowthAnalyzer(db);
        var analysis = analyzer.Analyze(
            roots,
            opt.Top,
            opt.MinBytes,
            opt.MinPercent,
            opt.DetailTop,
            opt.DetailMinBytes,
            isFirstRun,
            prevScanTime,
            baselines);

        // 为进入报告候选集的目录（榜单 / 新增消失 / 明细）标注已知用途。
        // 只作用于少量候选行，不触碰扫描、快照与对比链路。
        int noted = knowledge.Annotate(analysis);
        if (noted > 0)
            Dev(Lang.F("      目录用途标注：{0} 个候选目录命中知识库。",
                       "      Directory-purpose annotation: {0} candidate directories matched the knowledge base.",
                       noted));

        // 首次运行的结果是基准而非增长，不写入增长历史，避免污染后续回溯
        if (!isFirstRun && analysis.GrowthTop.Count > 0)
            db.InsertGrowthHistory(run.Id, analysis.GrowthTop);
        Console.WriteLine();

        PrintConsoleSummary(analysis, isFirstRun, opt.MinBytes);

        if (opt.NoReport)
        {
            Console.WriteLine(ConsoleStyle.Section(Lang.T("▸ 5/5  已跳过报告生成（--no-report）",
                                                           "▸ 5/5  Report generation skipped (--no-report)")));
            return 0;
        }

        // ---------- 阶段五：生成 HTML 报告 ----------
        Console.WriteLine(ConsoleStyle.Section(Lang.T("▸ 5/5  生成 HTML 报告", "▸ 5/5  Generate HTML report")));
        string reportPath = opt.OutputPath is { Length: > 0 }
            ? Path.GetFullPath(opt.OutputPath)
            : Path.Combine(baseDir, "reports",
                $"disk_growth_report_{FormatUtil.FileStamp(DateTime.Now)}.html");

        string? reportDir = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(reportDir) && !Directory.Exists(reportDir))
            Directory.CreateDirectory(reportDir);

        // 跳过清单里的哪些记录会因命中知识库而每轮重试（供报告打标记）
        var finalSkips = db.GetSkipRecords();
        var rescuedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (skip.KnowledgeRescanEnabled)
        {
            foreach (var r in finalSkips)
                if (skip.IsKnowledgeRescued(r)) rescuedPaths.Add(r.Path);
        }

        // 「可安全清理」清单：复用知识库匹配遍历本轮快照，挑出可清理性为「安全」的目录并合计
        var cleanable = CleanupAdvisor.Find(db, knowledge, roots);

        var model = new ReportModel
        {
            Analysis = analysis,
            Statistics = stats,
            Roots = roots,
            SkipRecords = finalSkips,
            GeneratedAt = DateTime.Now,
            ScanTime = startedAt,
            StartedAt = startedAt,
            RunId = run.Id,
            PreviousRunId = previousRunId,
            MinBytes = opt.MinBytes,
            MinPercent = opt.MinPercent,
            Top = opt.Top,
            DetailTop = opt.DetailTop,
            DetailMinBytes = opt.DetailMinBytes,
            UseDefaultExclude = settings.UseDefaultExclude,
            DedupEnabled = settings.Dedup,
            FollowReparse = settings.FollowReparse,
            KnowledgeRescanEnabled = skip.KnowledgeRescanEnabled,
            KnowledgeRescuedPaths = rescuedPaths,
            Cleanable = cleanable,
            VolumeGaps = volumeGaps,
            DatabasePath = db.DatabasePath
        };

        string html = ReportBuilder.Build(model);
        File.WriteAllText(reportPath, html, new UTF8Encoding(false));
        Console.WriteLine(ConsoleStyle.Success(Lang.F("  ✓ 已生成  {0}  ({1})", "  ✓ Written  {0}  ({1})",
                                                       reportPath, FormatUtil.Bytes(new FileInfo(reportPath).Length))));

        if (opt.OpenAfterReport)
        {
            try
            {
                Process.Start(new ProcessStartInfo(reportPath) { UseShellExecute = true });
                Console.WriteLine(Lang.T("      已用默认浏览器打开报告。",
                                         "      Report opened in the default browser."));
            }
            catch (Exception ex)
            {
                Console.WriteLine(Lang.T("      打开浏览器失败：", "      Failed to open the browser: ") + ex.Message);
            }
        }

        Console.WriteLine();
        Console.WriteLine(Lang.T("完成。", "Done."));
        // 输出标记行，便于外部脚本/自动化抓取报告路径（机器可读，恒定 ASCII、不随语言变化）
        Console.WriteLine("REPORT_PATH=" + reportPath);
        return 0;
    }

    // ------------------------------------------------------------- 增量辅助

    /// <summary>从完整路径提取盘符字母（"C:\…" → 'C'）；非盘符路径返回 null。</summary>
    private static char? RootVolumeLetter(string path)
        => path.Length >= 2 && path[1] == ':' ? char.ToUpperInvariant(path[0]) : null;

    /// <summary>
    /// 扫描根是否**真的**位于 <paramref name="letter"/> 卷上（按 junction / 符号链接 /
    /// 挂载点解析后的真实卷判断，不是看路径里的盘符）。
    ///
    /// 为什么必须查：增量（USN）是按盘符读变更日志的。若扫描根位于 junction 之后，
    /// 路径盘符与数据实际所在卷不符，就会去读**别的卷**的日志 ⇒ 变更集恒为空 ⇒
    /// 整根复用并上报「无变更」，是静默错报（2026-09-24 usntest 实测：改了 1 MB 仍报 0 B）。
    /// 这类根本轮放弃增量、直接全量扫描 —— 宁可慢，也不能报错数。
    /// 解析失败时同样返回 false（保守：宁可不增量）。
    /// </summary>
    private static bool IsOnDriveRootVolume(string path, char letter)
        => FileSystemNative.TryGetRealVolumeLetter(path, out char real) && real == letter;

    private static string BuildScanModeNote(ScanSettings settings, ScanStatistics stats)
    {
        string note;
        if (settings.ForceFullScan)
            note = Lang.T("全量扫描（--full 强制）", "full scan (forced by --full)");
        else if (stats.ReusedDirs > 0)
            note = Lang.F("USN 增量：复用 {0} 个目录的上一轮快照，其余部分实扫",
                          "USN incremental: {0} directories reused from the previous snapshot, the rest scanned",
                          FormatUtil.Count(stats.ReusedDirs));
        else
            note = Lang.T("全量扫描", "full scan");

        if (stats.ReusedDirs > 0 && settings.Dedup)
            note += Lang.T(
                "（提示：增量复用与硬链接去重并用时，跨「扫描区/复用区」的同一硬链接组存在理论上的重复计数可能，如需绝对精确请用 --full）",
                " (note: when incremental reuse and hardlink dedup are combined, a hardlink group spanning the "
                + "scanned/reused regions can theoretically be double-counted; use --full for absolute precision)");
        return note;
    }

    // ------------------------------------------------------------- 扫描根解析

    /// <summary>
    /// 为每个扫描根确定进度百分比的分母。优先级：
    ///   1) 上一轮快照的目录行数（最准，零成本）；
    ///   2) MFT 精确估算的目录总数（首次扫描 + 管理员权限，子目录根同样支持）；
    ///   3) 卷已用容量（首次扫描 + 非管理员，仅整卷根适用——子目录根与卷容量无关，
    ///      强行套用只会得到一个永远接近 0 的假进度）。
    /// </summary>
    private static List<(string Key, string Label, long EstimatedDirs, long VolumeUsedBytes)>
        BuildProgressEstimates(DatabaseService db, List<ScanRoot> roots, Options opt, KnowledgeService knowledge,
            out string? notice)
    {
        var list = new List<(string Key, string Label, long EstimatedDirs, long VolumeUsedBytes)>(roots.Count);
        var estimatedParts = new List<string>();
        var estimateDetails = new List<string>();
        var unavailableParts = new List<string>();
        long estimatedMs = 0;

        foreach (var r in roots)
        {
            long prevRows = db.CountPrevRows(r);
            if (prevRows > 0)
            {
                list.Add((r.Key, r.DisplayName, prevRows, 0));
                continue;
            }

            // 首次扫描（该根无历史基准）才需要估算；先探测卷能否打开，再给提示
            if (VolumeDirEstimator.CanEnumerate(r))
                Dev(Lang.F("      正在枚举 MFT 统计 {0} 的目录总数（用于进度百分比，约 1~3 秒）…",
                           "      Enumerating the MFT to count directories on {0} (for the progress percentage, ~1-3 s)...",
                           r.Key));

            // 豁免口径必须与扫描器一致：扫描器会因命中知识库而重新扫描默认排除的目录
            // （如 C:\Windows），分母也必须计入它们，否则进度条会提前冲到 100%（实测差 3.3 倍）
            var est = VolumeDirEstimator.TryEstimate(r, !opt.NoDefaultExclude, opt.Excludes, knowledge.ShouldRescan);
            if (est.Ok)
            {
                list.Add((r.Key, r.DisplayName, est.DirCount, 0));
                estimatedParts.Add(Lang.F("{0} {1} 个", "{0} {1} dirs", r.Key, FormatUtil.Count(est.DirCount)));
                estimateDetails.Add(r.Key + Lang.T("：", ": ") + est.Detail);
                estimatedMs += est.ElapsedMs;
                continue;
            }

            unavailableParts.Add(r.Key + DescribeEstimatorFailure(est.Stage, est.LastError, est.Detail));
            list.Add((r.Key, r.DisplayName, 0, r.IsDrive ? GetVolumeUsedBytes(r.Key) : 0));
        }

        var notices = new List<string>();
        // 成功是常态，只在 --dev 下回显分母来源与枚举自证信息（缓冲布局 / 记录版本 / 输入结构）
        if (estimatedParts.Count > 0 && DevMode)
        {
            notices.Add(Lang.F("      已按 MFT 统计目录总数：{0}（耗时 {1}），用作本次进度分母。",
                               "      Directory total counted from the MFT: {0} (took {1}); used as the progress denominator.",
                               string.Join(Lang.T("、", ", "), estimatedParts),
                               FormatUtil.Duration(estimatedMs)));
            notices.Add(Lang.F("      枚举自证：{0}", "      Enumeration self-proof: {0}",
                               string.Join(" | ", estimateDetails)));
        }
        if (unavailableParts.Count > 0)
        {
            // 可见部分只讲「后果」：百分比为什么是近似值、为什么干脆没有百分比。
            // 具体错误码属排障信息，收进 --dev —— 过去把原因写死成「需要管理员权限」，
            // 于是管理员档也在报权限不足，把诊断变成了误导（2026-09-24 用户反馈）。
            notices.Add(roots.Any(r => r.IsDrive)
                ? Lang.T("  进度百分比：有整卷根无法精确估算目录总数，改用「已扫字节 / 卷已用空间」近似（带 ~ 前缀）；子目录根只显示已扫量。",
                         "  Progress percentage: a whole-volume root could not be estimated precisely, so it falls "
                         + "back to \"scanned bytes / volume used space\" (shown with a ~ prefix); subdirectory roots "
                         + "show the scanned volume only.")
                : Lang.T("  进度百分比：本次无法精确估算目录总数，只显示已扫量。",
                         "  Progress percentage: the directory total could not be estimated this run; only the "
                         + "scanned volume is shown."));
            if (DevMode)
                notices.Add(Lang.F("      估算失败原因：{0}。", "      Estimation failure reasons: {0}.",
                                   string.Join(Lang.T("；", "; "), unavailableParts)));
        }

        notice = notices.Count > 0 ? string.Join(Environment.NewLine, notices) : null;
        return list;
    }

    /// <summary>
    /// 把 MFT 估算的失败原因翻译成一句可核对的话。绝不替失败下结论。
    ///
    /// 过去这里把「该操作需要管理员权限」写死在文案里，而真正的原因可能是别的
    /// （2026-09-24 实测：输出缓冲读错偏移 + 续读 FRN 取错位置导致 0 条记录，
    /// 症状与权限不足一模一样），于是管理员权限下也照打「需要管理员权限」，
    /// 把诊断变成了误导 —— 先看「断在哪一步」，再看错误码，最后才决定措辞。
    /// detail 是枚举器给出的自证信息（缓冲布局/记录版本/输入结构），有则附上。
    /// </summary>
    private static string DescribeEstimatorFailure(VolumeDirEstimator.EstimateStage stage, int lastError, string detail)
    {
        const int ERROR_ACCESS_DENIED = 5;

        string cause = stage switch
        {
            VolumeDirEstimator.EstimateStage.NotDrivePath =>
                Lang.T("扫描根不是盘符路径", "the scan root is not a drive-letter path"),
            VolumeDirEstimator.EstimateStage.OpenVolume =>
                Lang.T("打不开卷设备", "the volume device could not be opened"),
            VolumeDirEstimator.EstimateStage.EnumRecords =>
                Lang.T("MFT 枚举失败", "MFT enumeration failed"),
            VolumeDirEstimator.EstimateStage.NoDirRecords =>
                Lang.T("MFT 枚举未返回任何目录记录", "MFT enumeration returned no directory records"),
            VolumeDirEstimator.EstimateStage.RecordVersion =>
                Lang.T("设备返回的记录版本无法识别（本工具仅解析 USN_RECORD_V2）",
                       "the device returned an unrecognized record version (this tool only parses USN_RECORD_V2)"),
            VolumeDirEstimator.EstimateStage.PathNotFound =>
                Lang.T("枚举到目录树但目标路径不在其中",
                       "a directory tree was enumerated but the target path is not in it"),
            VolumeDirEstimator.EstimateStage.RootNotFound =>
                Lang.T("找不到卷根目录的 FRN（问 API 与反查枚举结果都失败）",
                       "the volume root directory's FRN could not be found (both the API query and the "
                       + "reverse lookup over the enumeration failed)"),
            VolumeDirEstimator.EstimateStage.Exception =>
                Lang.T("估算过程异常中止", "the estimation aborted with an exception"),
            _ => Lang.T("未知原因", "unknown cause")
        };

        // 错误码与阶段是两个独立维度：阶段说「断在哪」，错误码说「设备怎么答的」。
        // 有自证信息时优先附上它（它能直接区分「布局读错 / 版本不认识 / 参数被拒」）。
        string detailText = lastError switch
        {
            ERROR_ACCESS_DENIED => Lang.T("权限不足（Win32 5）", "access denied (Win32 5)"),
            0 => string.IsNullOrEmpty(detail) ? Lang.T("无 Win32 错误", "no Win32 error") : detail,
            -3 => Lang.T("枚举批次超过上限，已中止", "aborted: the enumeration batch limit was exceeded"),
            < 0 => Lang.T("枚举过程出现未预期异常", "the enumeration hit an unexpected exception"),
            _ => Lang.F("Win32 错误 {0}：", "Win32 error {0}: ", lastError)
                 + new Win32Exception(lastError).Message
                 + (string.IsNullOrEmpty(detail) ? string.Empty : Lang.T("；", "; ") + detail)
        };

        return Lang.F(" 无法估算目录总数（{0}；{1}）",
                      " could not estimate the directory total ({0}; {1})", cause, detailText);
    }

    /// <summary>卷已用字节（容量口径的进度分母）；不可用或读取失败返回 0。</summary>
    private static long GetVolumeUsedBytes(string key)
    {
        try
        {
            var d = new DriveInfo(key);
            if (!d.IsReady) return 0;
            long used = d.TotalSize - d.AvailableFreeSpace;
            return used > 0 ? used : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static List<ScanRoot> ResolveRoots(Options opt)
    {
        var list = new List<ScanRoot>();

        foreach (var spec in opt.DriveSpecs)
        {
            if (spec.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var d in DriveInfo.GetDrives()
                             .Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                {
                    list.Add(ScanRoot.FromDrive(d.Name));
                }
            }
            else if (spec.Length >= 1 && char.IsLetter(spec[0]))
            {
                list.Add(ScanRoot.FromDrive(spec));
            }
            else
            {
                Console.Error.WriteLine(Lang.F("警告：无法识别的盘符「{0}」，已忽略。",
                                               "Warning: unrecognized drive spec \"{0}\"; ignored.", spec));
            }
        }

        foreach (var dir in opt.RootDirs)
        {
            try
            {
                list.Add(ScanRoot.FromDirectory(dir));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(Lang.F("警告：无法解析目录「{0}」：{1}",
                                               "Warning: could not resolve directory \"{0}\": {1}", dir, ex.Message));
            }
        }

        // 去重
        var unique = new List<ScanRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in list)
            if (seen.Add(r.Key)) unique.Add(r);

        // 剔除被其他根覆盖的根，避免同一路径出现两条 root_key 记录
        unique.Sort((a, b) => a.Path.Length.CompareTo(b.Path.Length));
        var kept = new List<ScanRoot>();
        foreach (var r in unique)
        {
            bool covered = kept.Any(k =>
                r.Path.Equals(k.Path, StringComparison.OrdinalIgnoreCase) ||
                r.Path.StartsWith(k.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (covered)
            {
                var owner = kept.First(k =>
                    r.Path.StartsWith(k.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine(Lang.F("      提示：扫描根「{0}」已被「{1}」覆盖，已自动合并。",
                                         "      Note: scan root \"{0}\" is already covered by \"{1}\"; merged automatically.",
                                         r.DisplayName, owner.DisplayName));
                continue;
            }
            if (!Directory.Exists(r.Path))
            {
                Console.Error.WriteLine(Lang.F("警告：扫描根「{0}」不存在或不可访问，已忽略。",
                                               "Warning: scan root \"{0}\" does not exist or is inaccessible; ignored.",
                                               r.Path));
                continue;
            }
            kept.Add(r);
        }

        return kept;
    }

    // ---------------------------------------------------------------- 输出

    private static void PrintConsoleSummary(AnalysisResult a, bool isFirstRun, long minBytes)
    {
        Console.WriteLine(ConsoleStyle.Muted(Lang.T("   各扫描根", "   Per-root comparison")));
        Console.WriteLine(ConsoleStyle.Muted("     "
            + FormatUtil.Cell(Lang.T("盘", "Drive"), 6)
            + FormatUtil.CellRight(Lang.T("本次占用", "current"), 11)
            + FormatUtil.CellRight(Lang.T("变化量", "delta"), 12)
            + FormatUtil.CellRight(Lang.T("变化率", "change"), 10)));
        foreach (var r in a.Roots)
        {
            string delta, rate;
            if (isFirstRun)
            {
                delta = Lang.T("—", "-");
                rate = Lang.T("首次", "first");
            }
            else if (double.IsNaN(r.GrowthPercent) || double.IsInfinity(r.GrowthPercent))
            {
                delta = FormatUtil.SignedBytes(r.GrowthBytes);
                rate = Lang.T("新增", "new");
            }
            else
            {
                delta = FormatUtil.SignedBytes(r.GrowthBytes);
                rate = FormatUtil.SignedPercent(r.GrowthPercent);
            }
            Console.WriteLine("     "
                + FormatUtil.Cell(r.DisplayName, 6)
                + FormatUtil.CellRight(FormatUtil.Bytes(r.CurrBytes ?? 0), 11)
                + FormatUtil.CellRight(delta, 12)
                + FormatUtil.CellRight(rate, 10));
        }
        Console.WriteLine();

        if (isFirstRun)
        {
            Console.WriteLine(Lang.T("首次运行，暂无增长对比结果。再次运行本程序即可得到增长报告。",
                                     "First run: no growth comparison yet. Run the program again to get a growth report."));
            Console.WriteLine();
            return;
        }

        Console.WriteLine(Lang.F("   增长最快的目录（前 {0} / 共 {1}）",
                                 "   Fastest-growing directories (top {0} / {1})",
                                 Math.Min(10, a.GrowthTop.Count), FormatUtil.Count(a.GrowthTop.Count)));
        if (a.GrowthTop.Count == 0)
        {
            Console.WriteLine(Lang.T("  没有目录的增长量超过设定阈值。",
                                     "  No directory exceeded the configured growth threshold."));
        }
        else
        {
            if (a.GrowthFallbackUsed)
                Console.WriteLine(ConsoleStyle.Muted(Lang.F(
                    "  ※ 无目录达到 {0} 阈值，以下按未应用阈值的变化量降序（报告中有标注）",
                    "  * No directory reached the {0} threshold; sorted by unfiltered delta (flagged in the report)",
                    FormatUtil.MbThreshold(minBytes))));
            Console.WriteLine(ConsoleStyle.Muted("     "
                + FormatUtil.CellRight(Lang.T("变化量", "Delta"), 12)
                + FormatUtil.CellRight(Lang.T("变化率", "Change"), 10)
                + FormatUtil.CellRight(Lang.T("本次占用", "Current"), 11)
                + "  " + Lang.T("路径", "Path")));
            foreach (var it in a.GrowthTop.Take(10))
            {
                string rate = (double.IsNaN(it.GrowthPercent) || double.IsInfinity(it.GrowthPercent))
                    ? Lang.T("新增", "new")
                    : FormatUtil.SignedPercent(it.GrowthPercent);
                Console.WriteLine("     "
                    + FormatUtil.CellRight(FormatUtil.SignedBytes(it.GrowthBytes), 12)
                    + FormatUtil.CellRight(rate, 10)
                    + FormatUtil.CellRight(FormatUtil.Bytes(it.CurrBytes ?? 0), 11)
                    + "  " + it.Path);
            }
        }
        Console.WriteLine();
    }

    /// <summary>
    /// 「扫描统计 vs 卷已用」的口径对照 + 权限不足的影响提示。
    ///
    /// 起因（2026-09-24 用户反馈）：资源管理器显示 C 盘已用 293 GB，程序只报 285 GiB，
    /// 看起来像「漏扫了 8 GB」。实测证明是口径差异 —— 程序统计的是「去重后文件逻辑大小之和」，
    /// 资源管理器/卷属性显示的是「卷已分配的簇数」，后者包含不属任何目录的 NTFS 元数据与
    /// MFT 保留区（本机实测保留区 3.0 GiB + MFT 有效数据 2.1 GiB），因此前者必然更小。
    /// 差额过去完全不出现在输出里，用户只能猜；现在明确给出对照与「为什么」。
    ///
    /// 打印条件：差额 ≥1%（有需要解释的东西）或存在权限不足跳过（有需要提醒的漏算风险）。
    /// 其余情况不打印 —— 差额极小时这段文字只是噪声。本方法与 --dev 无关，属常规输出。
    /// </summary>
    private static void PrintVolumeGap(IReadOnlyList<VolumeGap> gaps, ScanStatistics stats)
    {
        var shown = new List<VolumeGap>();
        foreach (var g in gaps)
            if (Math.Abs(g.GapPercent) >= 1.0) shown.Add(g);

        if (shown.Count > 0)
        {
            Console.WriteLine(ConsoleStyle.Muted(Lang.T("   容量口径", "   Capacity check")));
            Console.WriteLine(ConsoleStyle.Muted("     "
                + FormatUtil.Cell(Lang.T("盘", "Drive"), 4)
                + FormatUtil.CellRight(Lang.T("卷已用", "used"), 11)
                + FormatUtil.CellRight(Lang.T("本次统计", "counted"), 11)
                + FormatUtil.CellRight(Lang.T("差额", "gap"), 11)
                + FormatUtil.CellRight(Lang.T("差额率", "rate"), 9)));
            foreach (var g in shown)
            {
                Console.WriteLine("     "
                    + FormatUtil.Cell(g.DriveLabel, 4)
                    + FormatUtil.CellRight(FormatUtil.Bytes(g.UsedBytes), 11)
                    + FormatUtil.CellRight(FormatUtil.Bytes(g.ScannedBytes), 11)
                    + FormatUtil.CellRight(FormatUtil.SignedBytes(g.GapBytes), 11)
                    + FormatUtil.CellRight("(" + FormatUtil.Percent(g.GapPercent) + ")", 9));
            }
            Console.WriteLine(ConsoleStyle.Muted(Lang.T(
                "   ※ 程序统计的是「去重后文件逻辑大小之和」，卷已用是「已分配簇数」；",
                "   *  Directory totals sum deduplicated logical file sizes; volume used is allocated clusters;")));
            Console.WriteLine(ConsoleStyle.Muted(Lang.T(
                "     差额来自 NTFS 元数据 / MFT 保留区 / 被排除目录，不代表有目录被漏扫。",
                "     the gap comes from NTFS metadata / the MFT reserved zone / excluded directories, not a missed directory.")));
        }

        if (stats.AccessDeniedCount <= 0) return;

        // 权限不足的两种档位后果完全不同：未提权会静默少算几个百分点（本机实测 12.5 GiB / 4.1%），
        // 提权后剩下的通常只是 SYSTEM 专属的系统保护区。只报数字不说后果，等于没说。
        //
        // 未提权档必须点明 --reset-skips：实测（2026-09-24）这些目录下次会被跳过清单**直接略过**
        // 而不重试 —— 提权后不加 --reset-skips，权限不足数会原样停在 99，用户会以为「提权没用」。
        if (ElevationHelper.IsElevated())
            Console.WriteLine(ConsoleStyle.Warning(Lang.F(
                "  ! 权限不足 {0} 个目录未统计（系统保护区，提权与 --reset-skips 均不放行）",
                "  ! Access denied: {0} directories not counted (system-reserved; elevation and --reset-skips won't help)",
                stats.AccessDeniedCount)));
        else
        {
            Console.WriteLine(ConsoleStyle.Warning(Lang.F(
                "  ! 本次未以管理员身份运行：{0} 个目录因权限不足未统计，占用会明显偏小。",
                "  ! Not running as administrator: {0} directories not counted due to access denial; the total is low.",
                stats.AccessDeniedCount)));
            Console.WriteLine(ConsoleStyle.Warning(Lang.T(
                "    以管理员身份运行可消除大部分；提权重跑时请加 --reset-skips 强制重试一次。",
                "    Running as administrator removes most of them; add --reset-skips when re-running elevated.")));
        }
    }

    /// <summary>
    /// --show-env：探测并打印运行环境与推导出的扫描策略后退出。
    /// 只读操作：不扫描、不写库，因此也不触发提权自举 —— 非管理员下介质一栏会显示「未知」，
    /// 提示中会说明原因（介质探测要打开 \\.\PhysicalDriveN）。
    /// </summary>
    private static void RunShowEnv(Options opt)
    {
        Console.WriteLine(Lang.T("=== 运行环境探测（--show-env）===", "=== Runtime environment probe (--show-env) ==="));
        Console.WriteLine();

        var targets = ProbeTargets(opt);
        // 语言探查结果从 Main 带过来（此处不重探：控制台代码页已被切成 UTF-8，重探会得出错结论）
        var profile = EnvironmentProbe.Probe(targets, Lang.Info);
        var strategy = EnvironmentProbe.Decide(profile, opt.EnumEngine);
        EnvironmentProbe.PrintSummary(profile, strategy);

        // 枚举引擎是唯一已接入执行链路的策略项，而且是**按卷实测**出来的结果 ——
        // 这里就地把实测值打出来，避免「文档说能用」与「这台机器实际能用」不一致。
        Console.WriteLine();
        Console.WriteLine(Lang.T("--- 枚举引擎实测（只读探测，不扫描、不写库）---",
                                  "--- Enum engine measurement (read-only; no scan, no database writes) ---"));
        if (opt.EnumEngine == EnumEngineKind.Win32)
        {
            Console.WriteLine("  " + Lang.T(
                "按 --enum-engine=win32 指定，跳过实测：使用改动前的 Win32 枚举路径。",
                "Skipped by --enum-engine=win32: using the pre-change Win32 enumeration path."));
        }
        else
        {
            var selector = new EnumEngineSelector(opt.EnumEngine, EnumEngineSelector.DefaultBufferSize);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                string key = target.Length >= 2 && target[1] == ':'
                    ? char.ToUpperInvariant(target[0]) + ":"
                    : target;
                if (!seen.Add(key)) continue;
                var kind = selector.Resolve(target, out _);
                Console.WriteLine($"  {key,-6} {EnumEngineSelector.Describe(kind)}");
            }
            foreach (var note in selector.Notes()) Console.WriteLine("         " + note);
        }

        Console.WriteLine();
        Console.WriteLine(Lang.T(
            "说明：枚举引擎已接入执行链路（上面是按卷实测所得，与实际扫描行为一致）；",
            "Note: the enum engine is already wired into the execution path (the values above are measured per volume"));
        Console.WriteLine(Lang.T(
            "      并行度候选集仍仅作展示，将在阶段 D 逐档实测后固化。",
            "      and match actual scan behaviour); the parallelism candidates are display-only until stage D."));
    }

    /// <summary>
    /// --show-env 要探测哪些卷：优先用命令行给出的 -d / -r；都没给则取全部本地固定磁盘，
    /// 这样单独敲 --show-env 也能看到完整环境。
    /// </summary>
    private static List<string> ProbeTargets(Options opt)
    {
        var list = new List<string>();

        foreach (var spec in opt.DriveSpecs)
        {
            if (spec.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var d in DriveInfo.GetDrives())
                    if (d.DriveType == DriveType.Fixed && d.IsReady) list.Add(d.Name);
            }
            else if (spec.Length >= 1 && char.IsLetter(spec[0]))
            {
                list.Add(char.ToUpperInvariant(spec[0]) + ":");
            }
        }

        list.AddRange(opt.RootDirs);

        // 一个都没指定时取全部本地固定磁盘：单独敲 --show-env 也能看到完整环境
        if (list.Count == 0)
        {
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed && d.IsReady) list.Add(d.Name);
        }

        return list;
    }

    /// <summary>
    /// --list-drives：列出本机磁盘。
    /// 列宽一律走 <see cref="FormatUtil.Cell"/>（按**显示宽度**补齐）：
    /// 中文标签占两列，用 <c>{x,-10}</c> 这类按字符数补齐会让列在两种语言下错开。
    /// </summary>
    private static void PrintDrives()
    {
        Console.WriteLine(Lang.T("本机磁盘：", "Local drives:"));
        Console.WriteLine("  " + FormatUtil.Cell(Lang.T("盘符", "Drive"), 7)
                          + FormatUtil.Cell(Lang.T("类型", "Type"), 12)
                          + FormatUtil.Cell(Lang.T("就绪", "Ready"), 7)
                          + FormatUtil.Cell(Lang.T("文件系统", "FileSystem"), 13)
                          + FormatUtil.Cell(Lang.T("总容量", "Total"), 15)
                          + FormatUtil.Cell(Lang.T("可用空间", "Free"), 15)
                          + Lang.T("已用率", "Used"));
        foreach (var d in DriveInfo.GetDrives())
        {
            string type = d.DriveType switch
            {
                DriveType.Fixed => Lang.T("固定", "Fixed"),
                DriveType.Removable => Lang.T("可移动", "Removable"),
                DriveType.Network => Lang.T("网络", "Network"),
                DriveType.CDRom => Lang.T("光驱", "CD-ROM"),
                DriveType.Ram => Lang.T("内存盘", "RAM disk"),
                _ => d.DriveType.ToString()
            };
            if (!d.IsReady)
            {
                Console.WriteLine("  " + FormatUtil.Cell(d.Name, 7) + FormatUtil.Cell(type, 12)
                                  + Lang.T("否", "no"));
                continue;
            }
            long used = d.TotalSize - d.AvailableFreeSpace;
            double usedPct = d.TotalSize > 0 ? used * 100.0 / d.TotalSize : 0;
            Console.WriteLine("  " + FormatUtil.Cell(d.Name, 7) + FormatUtil.Cell(type, 12)
                              + FormatUtil.Cell(Lang.T("是", "yes"), 7)
                              + FormatUtil.Cell(d.DriveFormat, 13)
                              + FormatUtil.Cell(FormatUtil.Bytes(d.TotalSize), 15)
                              + FormatUtil.Cell(FormatUtil.Bytes(d.AvailableFreeSpace), 15)
                              + $"{usedPct:0.0}%");
        }
        Console.WriteLine();
        Console.WriteLine(Lang.T(
            "提示：默认扫描范围（-d ALL）只包含「固定」磁盘；可移动磁盘请用 -d <盘符> 显式指定。",
            "Note: the default scan scope (-d ALL) covers fixed drives only; specify removable drives "
            + "explicitly with -d <letter>."));
    }

    /// <summary>
    /// --list-skips：列出跳过清单。
    /// 命中目录用途知识库的「默认排除 / 权限不足 / IO 错误」记录会打上 ★ —— 表示它们虽记录在清单中，
    /// 但每次扫描仍会重新尝试（知识库豁免）；<c>-x</c> 自定义排除与目录符号链接不受豁免，故不标记。
    /// 硬排除目录（System Volume Information）另标 <c>[系统保护区·不重试]</c> ——
    /// 它们命中知识库也不放行，因此绝不能打 ★，否则清单会声称一件不会发生的事。
    /// </summary>
    private static void PrintSkips(DatabaseService db, KnowledgeService? knowledge)
    {
        var records = db.GetSkipRecords();
        if (records.Count == 0)
        {
            Console.WriteLine(Lang.T("跳过清单为空。", "The skip list is empty."));
            return;
        }

        // 「原因代码 → 可读名称」的额外修饰（ReasonText 给的是短名，清单里要带上「下次会怎样」）
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEFAULT_EXCLUDE"] = Lang.T("默认排除", "Default exclusion"),
            ["USER_EXCLUDE"] = Lang.T("自定义排除", "Custom exclusion"),
            ["ACCESS_DENIED"] = Lang.T("权限不足（下次自动跳过）", "Access denied (skipped automatically next time)"),
            ["IO_ERROR"] = Lang.T("IO 错误（下次自动跳过）", "IO error (skipped automatically next time)"),
            ["REPARSE_POINT"] = Lang.T("符号链接（未跟随）", "Reparse point (not followed)")
        };

        Console.WriteLine(Lang.F("跳过清单共 {0} 条：", "Skip list: {0} records:", records.Count));
        int marked = 0;
        int hard = 0;
        foreach (var g in records.GroupBy(r => SkipRecord.ReasonCode(r.Reason)))
        {
            // 只有这三类才可能被知识库豁免；-x 自定义排除优先级最高、符号链接涉及去重正确性，均不豁免
            bool canRescue = SkipListService.ReasonCanBeRescued(SkipRecord.ParseReason(g.Key));
            Console.WriteLine();
            Console.WriteLine("  " + Lang.F("【{0}】{1} 项", "[{0}] {1} items",
                                              byKey.TryGetValue(g.Key, out var kn) ? kn : g.Key, g.Count()));
            foreach (var r in g.Take(50))
            {
                // 硬排除（System Volume Information）即便命中知识库也不会重试：不能打 ★。
                // 否则清单声称「每轮重试」而实际从不重试，展示本身就成了新的误导（2026-09-24）。
                bool isHard = SkipListService.IsHardExcludedPath(r.Path);
                bool star = canRescue && !isHard && knowledge?.ShouldRescan(r.Path) == true;
                if (star) marked++;
                if (isHard) hard++;
                Console.WriteLine("    " + (star ? Lang.T("★ ", "* ") : "  ") + r.Path
                                  + (isHard ? Lang.T("　[系统保护区·不重试]", " [System reserved - never retried]") : ""));
            }
            if (g.Count() > 50)
                Console.WriteLine(Lang.F("    …（其余 {0} 项已省略）", "    ... ({0} more omitted)", g.Count() - 50));
        }

        if (marked > 0)
        {
            Console.WriteLine();
            Console.WriteLine(Lang.F(
                "★ 共 {0} 条命中目录用途知识库（自身是已知目录，或其后代中含已知目录）：",
                "* {0} records match the directory-purpose knowledge base (either the directory itself or one of "
                + "its descendants is known):", marked));
            Console.WriteLine(Lang.T(
                "  它们虽然记录在本清单中，但每次扫描都会重新尝试；如确实不想扫描，",
                "  They stay in this list but are retried on every scan; if you really do not want to scan them,"));
            Console.WriteLine(Lang.T(
                "  可用 -x <路径前缀> 显式排除（-x 优先级最高，知识库豁免不会推翻它）。",
                "  exclude them explicitly with -x <path prefix> (-x has the highest priority and overrides the "
                + "knowledge-base rescue)."));
        }
        if (hard > 0)
        {
            Console.WriteLine();
            Console.WriteLine(Lang.F(
                "　共 {0} 条标有 [系统保护区·不重试]：这些目录的系统 ACL 连管理员也读不到，",
                "  {0} records are marked [System reserved - never retried]: their system ACLs are unreadable "
                + "even to administrators,", hard));
            Console.WriteLine(Lang.T(
                "　内容不是占用增长的可疑来源，故每轮直接跳过 —— 命中知识库或 --reset-skips 都不会让它们重试。",
                "  so they are not plausible sources of growth and are skipped every run -- neither the knowledge "
                + "base nor --reset-skips will retry them."));
        }
        if (knowledge == null)
        {
            Console.WriteLine();
            Console.WriteLine(Lang.T("（本次已用 --no-knowledge-rescan 关闭知识库豁免，清单内记录一律直接跳过。）",
                                     "(The knowledge-base rescue is disabled by --no-knowledge-rescan; every record "
                                     + "in this list is skipped.)"));
        }
    }

    /// <summary>
    /// --list-knowledge：列出目录用途知识库的全部条目。
    /// 用于核对条目内容、排查「某个目录为什么没被识别」，以及确认自定义条目是否生效。
    ///
    /// 用途短名与分类按当前输出语言取（英文档优先英文列，缺失回退中文原文），
    /// 因此英文档下自定义的中文条目会原样显示 —— 至少比一个空白用途列好。
    /// </summary>
    private static void PrintKnowledge(DatabaseService db)
    {
        var entries = db.GetKnowledgeEntries();
        if (entries.Count == 0)
        {
            Console.WriteLine(Lang.T("目录用途知识库为空。", "The directory-purpose knowledge base is empty."));
            return;
        }

        int builtin = entries.Count(e => e.IsBuiltin);
        int disabled = entries.Count(e => !e.Enabled);
        Console.WriteLine(Lang.F("目录用途知识库共 {0} 条（内置 {1} · 自定义 {2}）",
                                 "Directory-purpose knowledge base: {0} entries (built-in {1} - custom {2})",
                                 entries.Count, builtin, entries.Count - builtin)
                          + (disabled > 0
                              ? Lang.F("，其中 {0} 条已停用", ", {0} disabled", disabled)
                              : Lang.T("，全部启用", ", all enabled"))
                          + Lang.T("：", ":"));

        // 用途短名要补齐到本语言下的最宽值，否则「←」箭头列会参差不齐（中英两档各算一次）
        int titleWidth = 0;
        foreach (var e in entries)
            titleWidth = Math.Max(titleWidth, FormatUtil.DisplayWidth(e.TitleText));

        foreach (var g in entries.GroupBy(e => e.CategoryText))
        {
            Console.WriteLine();
            Console.WriteLine("  " + Lang.F("【{0}】{1} 条", "[{0}] {1} entries", g.Key, g.Count()));
            foreach (var e in g)
            {
                string flag = e.Enabled ? " " : Lang.T("停", "off");
                string clean = e.Cleanable switch
                {
                    "安全" => Lang.T("安全", "safe"),
                    "谨慎" => Lang.T("谨慎", "careful"),
                    "禁止" => Lang.T("禁止", "never"),
                    _ => "--"
                };
                Console.WriteLine($"    [{e.Priority,3}] {flag} "
                                  + FormatUtil.Cell(clean, Lang.IsEnglish ? 9 : 6)
                                  + FormatUtil.Cell(e.TitleText, titleWidth + 2)
                                  + Lang.T("←  ", "<-  ") + e.Pattern);
            }
        }

        Console.WriteLine();
        Console.WriteLine(Lang.T("说明：优先级数值越大越优先；同一目录命中多条时取优先级最高的那条。",
                                 "Notes: a larger priority number wins; when several entries match one directory, "
                                 + "the highest-priority one applies."));
        Console.WriteLine(Lang.T("      条目存于 disk_growth.db 的 dir_knowledge 表，可直接用 SQLite 工具增删改；",
                                 "      Entries live in the dir_knowledge table of disk_growth.db and can be edited with "
                                 + "any SQLite tool;"));
        Console.WriteLine(Lang.T("      模式为「后缀对齐」匹配（* 单段 / ** 多段 / %VAR% 环境变量），不含盘符与用户名。",
                                 "      patterns match on aligned path suffixes (* one segment / ** many segments / "
                                 + "%VAR% environment variables) and contain no drive letter or user name."));
        Console.WriteLine(Lang.T("      内置条目仅在缺失时补齐，你的修改不会被程序覆盖；--reset-db 也不会清空本表。",
                                 "      Built-in entries are only added when missing, so your edits are never "
                                 + "overwritten; --reset-db does not clear this table."));
        Console.WriteLine(Lang.T("      英文列 category_en / title_en / note_en 为空时，英文档回退显示中文原文。",
                                 "      When the English columns category_en / title_en / note_en are empty, the "
                                 + "English build falls back to the Chinese text."));
    }

    /// <summary>
    /// --reset-db：把数据库清空回「从未扫描」状态（批次/快照/对比历史/跳过清单/USN 状态全清）。
    /// 破坏性操作，必须在交互式终端中确认；输入或输出被重定向（bat / 脚本）时直接拒绝执行，
    /// 避免无人值守场景下被误触发。重置完成后即退出，不进行扫描。
    /// </summary>
    private static int RunReset(DatabaseService db)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(Lang.T(
                "[重置] 已拒绝执行：清空数据库需要交互式确认，但当前输入/输出被重定向。",
                "[Reset] Refused: clearing the database needs interactive confirmation, but input/output is redirected."));
            Console.Error.WriteLine(Lang.T(
                "       请在交互式终端（PowerShell / cmd 窗口）中直接运行本命令；数据库未做任何修改。",
                "          Run this command directly in an interactive terminal (PowerShell / cmd window); "
                + "the database was not modified."));
            return 3;
        }

        Console.WriteLine();
        Console.WriteLine(Lang.T("即将清空数据库中的全部记录：", "About to clear every record in the database:"));
        Console.WriteLine(Lang.T("  · 扫描批次 scan_runs 与 目录快照 dir_snapshots / dir_snapshots_prev",
                                 "  - scan runs (scan_runs) and directory snapshots (dir_snapshots / dir_snapshots_prev)"));
        Console.WriteLine(Lang.T("  · 增长对比历史 growth_history",
                                 "  - growth comparison history (growth_history)"));
        Console.WriteLine(Lang.T("  · 跳过清单 skip_paths 与 USN 增量状态 volume_state",
                                 "  - skip list (skip_paths) and USN incremental state (volume_state)"));
        Console.WriteLine(Lang.T("  · 目录用途知识库 dir_knowledge 属配置数据，<不在> 清空范围内，原样保留",
                                 "  - the directory-purpose knowledge base (dir_knowledge) is configuration data, "
                                 + "NOT cleared and kept as is"));
        Console.WriteLine(Lang.T("清空后程序回到「从未扫描」状态：下次运行即为全新首次扫描，需重新建立对比基准。",
                                 "Afterwards the program returns to the \"never scanned\" state: the next run is a "
                                 + "fresh first scan and a new baseline must be established."));
        Console.Write(Lang.T("确认清空？(y/N) ", "Confirm clearing? (y/N) "));

        string? answer = Console.ReadLine()?.Trim();
        bool yes = string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
        if (!yes)
        {
            Console.WriteLine(Lang.T("已取消：数据库未做任何修改。",
                                     "Cancelled: the database was not modified."));
            return 0;
        }

        var s = db.ResetAll();
        Console.WriteLine();
        Console.WriteLine(Lang.T("[重置] 数据库已清空：", "[Reset] Database cleared:"));
        Console.WriteLine("      " + FormatUtil.Cell(Lang.T("扫描批次 scan_runs", "scan runs (scan_runs)"), 34)
                          + Lang.F("{0} 行", "{0} rows", FormatUtil.Count(s.ScanRuns)));
        Console.WriteLine("      " + FormatUtil.Cell(Lang.T("目录快照 dir_snapshots", "snapshots (dir_snapshots)"), 34)
                          + Lang.F("{0} 行", "{0} rows", FormatUtil.Count(s.Snapshots)));
        Console.WriteLine("      " + FormatUtil.Cell(Lang.T("上一轮快照 dir_snapshots_prev", "previous snapshot (dir_snapshots_prev)"), 34)
                          + Lang.F("{0} 行", "{0} rows", FormatUtil.Count(s.PrevSnapshots)));
        Console.WriteLine("      " + FormatUtil.Cell(Lang.T("对比历史 growth_history", "growth history (growth_history)"), 34)
                          + Lang.F("{0} 行", "{0} rows", FormatUtil.Count(s.GrowthHistory)));
        Console.WriteLine("      " + FormatUtil.Cell(Lang.T("跳过清单 skip_paths", "skip list (skip_paths)"), 34)
                          + Lang.F("{0} 行", "{0} rows", FormatUtil.Count(s.SkipPaths)));
        Console.WriteLine("      " + FormatUtil.Cell(Lang.T("USN 状态 volume_state", "USN state (volume_state)"), 34)
                          + Lang.F("{0} 行", "{0} rows", FormatUtil.Count(s.VolumeStates)));
        Console.WriteLine(Lang.F("      合计 {0} 行；扫描暂存表已删除，批次自增序号已复位。",
                                 "      {0} rows in total; the scan staging table was dropped and the run "
                                 + "autoincrement counter was reset.", FormatUtil.Count(s.Total)));
        Console.WriteLine(Lang.T("      下次运行本程序即为全新首次扫描。",
                                 "      The next run of this program will be a fresh first scan."));
        return 0;
    }
}
