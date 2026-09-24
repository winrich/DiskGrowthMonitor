using System.Text;
using DiskGrowthMonitor.Models;
using DiskGrowthMonitor.Services;
using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Cli;

/// <summary>
/// 命令行参数解析结果。
/// </summary>
public sealed class Options
{
    /// <summary>要扫描的盘符（如 "C" / "ALL"）。与 Roots 合并使用。</summary>
    public List<string> DriveSpecs { get; } = new();

    /// <summary>-root 指定的自定义扫描目录（可多次指定）。用于只扫描某几个目录。</summary>
    public List<string> RootDirs { get; } = new();

    /// <summary>报告主榜条数（增长榜/缩减榜）。</summary>
    public int Top { get; set; } = 30;

    /// <summary>HTML 详细明细表的最大行数。</summary>
    public int DetailTop { get; set; } = 500;

    /// <summary>详细明细表的最小变化量（MB），默认 1 MB。</summary>
    public double DetailMinMb { get; set; } = 1;

    /// <summary>低于该增长量的目录不进入报告（MB）。</summary>
    public double MinMb { get; set; } = 100;

    /// <summary>低于该增长百分比的目录不进入报告。</summary>
    public double MinPercent { get; set; }

    /// <summary>命令行额外排除目录前缀（可多次指定）。</summary>
    public List<string> Excludes { get; } = new();

    /// <summary>禁用内置默认排除清单（Windows、$Recycle.Bin、System Volume Information）。</summary>
    public bool NoDefaultExclude { get; set; }

    /// <summary>清空历史「权限不足 / IO 错误」跳过记录，重新尝试扫描。</summary>
    public bool ResetSkips { get; set; }

    /// <summary>
    /// 关闭「知识库豁免」：不让命中目录用途知识库的目录突破内置默认排除与历史跳过记录。
    /// 默认开启豁免（知识库里的目录每轮都重新尝试扫描）。
    /// </summary>
    public bool NoKnowledgeRescan { get; set; }

    /// <summary>清空数据库全部记录，使程序回到「从未扫描」状态（确认后执行，随后退出）。</summary>
    public bool ResetDb { get; set; }

    /// <summary>关闭硬链接去重（速度优先，代价是硬链接数据可能被重复计数）。</summary>
    public bool NoDedup { get; set; }

    /// <summary>
    /// 关闭「以管理员身份自举」。
    /// 默认行为：本次确实要扫描且当前进程非管理员时，尝试用 UAC 重新以管理员身份启动自己
    /// —— 因为读 USN 变更日志（打开 \\.\X: 卷设备）与 MFT 目录数估算都要求管理员权限，
    /// 非管理员下不仅增量不可用（恒全量扫描），进度百分比也只能退化为「已扫字节 / 卷已用空间」。
    /// </summary>
    public bool NoElevate { get; set; }

    /// <summary>
    /// 内部标记：本次进程是由自举产生的子进程。
    /// 正常路径下子进程必然已提权（提权后 IsInRole(Administrator) 为真）而天然不再自举；
    /// 这个标记是针对「提权未生效」等极端环境的硬保险，避免陷入无限重启。
    /// 不出现在 --help 中。
    /// </summary>
    public bool ElevatedChild { get; set; }

    /// <summary>
    /// 内部开关：把控制台输出（stdout + stderr）整段写入指定文件。
    /// 供提权自举的父进程回显子进程输出用（Verb="runas" 必须且只能 UseShellExecute=true，
    /// .NET Framework 下该模式禁止重定向子进程的流 ⇒ 只能让子进程自己落盘、父进程读文件）。
    /// 不出现在 --help 中。
    /// </summary>
    public string? ConsoleLogPath { get; set; }

    /// <summary>
    /// 内部开关：提权子进程尝试接管指定 PID（= 父进程）的控制台（方案 B，见 <see cref="Util.ConsoleAttach"/>）。
    /// 接管成功时输出/进度条直达父窗口，<see cref="ConsoleLogPath"/> 只承载一行握手标记；
    /// 失败时退回「输出整段落盘 + 父进程中继」。不出现在 --help 中。
    /// </summary>
    public string? AttachParentPid { get; set; }

    /// <summary>跟随目录符号链接 / junction（默认不跟随）。</summary>
    public bool FollowReparse { get; set; }

    /// <summary>本次命令行是否出现过已取消的 --db（出现即忽略其值，仅用于打印一行提示）。</summary>
    public bool DbArgSeen { get; set; }

    /// <summary>HTML 报告输出路径；为空则自动生成到 reports 目录。</summary>
    public string? OutputPath { get; set; }

    /// <summary>不生成 HTML 报告，仅扫描入库。</summary>
    public bool NoReport { get; set; }

    /// <summary>仅列出本机磁盘后退出。</summary>
    public bool ListDrives { get; set; }

    /// <summary>仅列出已记录的跳过清单后退出。</summary>
    public bool ListSkips { get; set; }

    /// <summary>仅列出目录用途知识库条目后退出。</summary>
    public bool ListKnowledge { get; set; }

    /// <summary>
    /// 仅探测并打印运行环境（OS / CPU / 内存 / 介质 / 文件系统）与推导出的扫描策略后退出。
    /// 不扫描、不写库。用于排查「这台机器为什么选了这个并行度 / 引擎」。
    /// </summary>
    public bool ShowEnv { get; set; }

    /// <summary>
    /// 开发者模式：额外打印面向开发/排障的诊断行（环境明细、增量决策、MFT 估算、
    /// 枚举引擎探测、知识库与跳过清单的记账、用途标注计数等）。
    ///
    /// 默认关闭。常规使用时屏幕只保留「五步进度 + 扫描结果 + 报告路径」，
    /// 这些诊断细节既不参与结果解读、又会把关键数字淹没在几十行里。
    /// 关闭只是不打印，所有逻辑照常执行（统计值仍写入报告与数据库）。
    /// </summary>
    public bool DevMode { get; set; }

    /// <summary>强制全量扫描（禁用 USN 增量复用）。</summary>
    public bool FullScan { get; set; }

    /// <summary>并行扫描线程数；0 = 自动（默认 = 扫描根数，上限 CPU 核数）。</summary>
    public int Threads { get; set; }

    /// <summary>
    /// 目录枚举引擎档位（阶段 B）。默认 Auto：按卷实测 60 → 37 → Win32 并缓存。
    /// win32 即改动前的行为，用于运行时一键回退。
    /// </summary>
    public EnumEngineKind EnumEngine { get; set; } = EnumEngineKind.Auto;

    /// <summary>
    /// <c>--lang</c> 的原始取值（<c>auto</c> / <c>zh</c> / <c>en</c>），默认 auto。
    ///
    /// 说明：语言**不在这里裁决**。真正的探查与裁决在 <c>Program.Main</c> 最先完成
    /// （要早于「参数错误」这类提示本身的输出），本属性只负责语法校验 ——
    /// 取值非法时给出可读的参数错误。裁决结果见 <see cref="Util.Lang.Info"/>。
    /// </summary>
    public string Language { get; set; } = "auto";

    /// <summary>报告生成后自动用默认浏览器打开。</summary>
    public bool OpenAfterReport { get; set; }

    /// <summary>帮助。</summary>
    public bool ShowHelp { get; set; }

    /// <summary>解析失败信息。</summary>
    public string? Error { get; private set; }

    /// <summary>最小增长字节数。</summary>
    public long MinBytes => (long)(MinMb * 1024 * 1024);

    /// <summary>详细明细表的最小变化量（字节）。</summary>
    public long DetailMinBytes => (long)(DetailMinMb * 1024 * 1024);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];

            // 兼容 --opt=value 写法：只在「开关本身」上拆等号，参数值里的等号不受影响
            // （例如 -r "D:\a=b" 仍按原样解释）。
            string? inlineValue = null;
            int eq = a.IndexOf('=');
            if (eq > 0)
            {
                inlineValue = a.Substring(eq + 1);
                a = a.Substring(0, eq);
            }

            string? Next()
            {
                if (inlineValue != null) return inlineValue;
                if (i + 1 >= args.Length) return null;
                return args[++i];
            }

            switch (a.Trim().ToLowerInvariant())
            {
                case "-d":
                case "--drive":
                case "--drives":
                    {
                        var v = Next();
                        if (string.IsNullOrWhiteSpace(v)) { o.Error = Lang.T("缺少 --drive 的参数值。", "Missing value for --drive."); return o; }
                        foreach (var part in v.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                            o.DriveSpecs.Add(part.Trim());
                        break;
                    }
                case "-r":
                case "--root":
                    {
                        var v = Next();
                        if (string.IsNullOrWhiteSpace(v)) { o.Error = Lang.T("缺少 --root 的参数值。", "Missing value for --root."); return o; }
                        o.RootDirs.Add(v.Trim().Trim('"'));
                        break;
                    }
                case "-t":
                case "--top":
                    if (!TryInt(Next(), 1, 100000, out int top)) { o.Error = Lang.T("--top 需要 1..100000 的整数。", "--top needs an integer in 1..100000."); return o; }
                    o.Top = top;
                    break;
                case "--detail-top":
                    if (!TryInt(Next(), 1, 5000000, out int dt)) { o.Error = Lang.T("--detail-top 需要 1..5000000 的整数。", "--detail-top needs an integer in 1..5000000."); return o; }
                    o.DetailTop = dt;
                    break;
                case "--detail-min-mb":
                    if (!TryDouble(Next(), 0, out double dmm)) { o.Error = Lang.T("--detail-min-mb 需要非负数值。", "--detail-min-mb needs a non-negative number."); return o; }
                    o.DetailMinMb = dmm;
                    break;
                case "-m":
                case "--min-mb":
                    if (!TryDouble(Next(), 0, out double mm)) { o.Error = Lang.T("--min-mb 需要非负数值。", "--min-mb needs a non-negative number."); return o; }
                    o.MinMb = mm;
                    break;
                case "-p":
                case "--min-percent":
                    if (!TryDouble(Next(), 0, out double mp)) { o.Error = Lang.T("--min-percent 需要非负数值。", "--min-percent needs a non-negative number."); return o; }
                    o.MinPercent = mp;
                    break;
                case "-x":
                case "--exclude":
                    {
                        var v = Next();
                        if (string.IsNullOrWhiteSpace(v)) { o.Error = Lang.T("缺少 --exclude 的参数值。", "Missing value for --exclude."); return o; }
                        o.Excludes.Add(v.Trim().Trim('"'));
                        break;
                    }
                case "--no-default-exclude":
                    o.NoDefaultExclude = true;
                    break;
                case "--reset-skips":
                    o.ResetSkips = true;
                    break;
                case "--no-knowledge-rescan":
                    o.NoKnowledgeRescan = true;
                    break;
                case "--reset-db":
                    o.ResetDb = true;
                    break;
                case "--no-dedup":
                    o.NoDedup = true;
                    break;
                case "--no-elevate":
                    o.NoElevate = true;
                    break;
                case "--elevated":
                    // 内部使用：自举产生的子进程带上此标记，防止提权未生效时无限重启
                    o.ElevatedChild = true;
                    break;
                case "--console-log":
                    // 内部使用：控制台输出整段落盘，供提权自举的父进程回显（见属性注释）
                    {
                        var v = Next();
                        if (string.IsNullOrWhiteSpace(v)) { o.Error = Lang.T("缺少 --console-log 的参数值。", "Missing value for --console-log."); return o; }
                        o.ConsoleLogPath = v.Trim().Trim('"');
                        break;
                    }
                case "--attach-parent":
                    // 内部使用：提权子进程尝试接管父进程控制台（方案 B，见 Util.ConsoleAttach）
                    {
                        var v = Next();
                        if (string.IsNullOrWhiteSpace(v)) { o.Error = Lang.T("缺少 --attach-parent 的参数值。", "Missing value for --attach-parent."); return o; }
                        o.AttachParentPid = v.Trim();
                        break;
                    }
                case "--follow-reparse":
                    o.FollowReparse = true;
                    break;
                case "--db":
                    // 该开关已取消：数据库固定为 <exe 目录>\disk_growth.db。
                    // 为兼容旧命令与脚本，这里仍识别但丢弃其值，由 Program 打印一行提示。
                    o.DbArgSeen = true;
                    if (i + 1 < args.Length) i++;
                    break;
                case "-o":
                case "--out":
                    {
                        var v = Next();
                        if (string.IsNullOrWhiteSpace(v)) { o.Error = Lang.T("缺少 --out 的参数值。", "Missing value for --out."); return o; }
                        o.OutputPath = v.Trim().Trim('"');
                        break;
                    }
                case "--no-report":
                    o.NoReport = true;
                    break;
                case "--list-drives":
                    o.ListDrives = true;
                    break;
                case "--list-skips":
                    o.ListSkips = true;
                    break;
                case "--list-knowledge":
                    o.ListKnowledge = true;
                    break;
                case "--show-env":
                    o.ShowEnv = true;
                    break;
                case "--dev":
                    // 开发者模式：打开面向开发/排障的诊断输出（默认关闭）
                    o.DevMode = true;
                    break;
                case "--open":
                    o.OpenAfterReport = true;
                    break;
                case "--full":
                    o.FullScan = true;
                    break;
                case "--threads":
                    if (!TryInt(Next(), 0, 256, out int th)) { o.Error = Lang.T("--threads 需要 0..256 的整数（0 = 自动）。", "--threads needs an integer in 0..256 (0 = auto)."); return o; }
                    o.Threads = th;
                    break;
                case "--enum-engine":
                    if (!EnumEngineSelector.TryParse(Next(), out var engineKind))
                    {
                        o.Error = Lang.T("--enum-engine 需要 auto | extd | both | win32 之一。",
                                         "--enum-engine needs one of auto | extd | both | win32.");
                        return o;
                    }
                    o.EnumEngine = engineKind;
                    break;
                case "--lang":
                    {
                        // 只做语法校验：真正的语言探查与裁决在 Program.Main 最先完成
                        // （必须早于包括本条错误在内的任何提示输出），见 Options.Language 的注释
                        var v = Next();
                        if (!LanguageProbe.TryParseName(v, out var forcedLang))
                        {
                            o.Error = Lang.T("--lang 需要 auto | zh | en 之一。",
                                             "--lang needs one of auto | zh | en.");
                            return o;
                        }
                        o.Language = !forcedLang.HasValue
                            ? "auto"
                            : (forcedLang.Value == OutputLanguage.Zh ? "zh" : "en");
                        break;
                    }
                case "-h":
                case "-?":
                case "--help":
                    o.ShowHelp = true;
                    break;
                default:
                    o.Error = Lang.F("无法识别的参数：{0}", "Unrecognized argument: {0}", a);
                    return o;
            }
        }
        return o;
    }

    private static bool TryInt(string? s, int min, int max, out int value)
    {
        value = 0;
        return s != null
               && int.TryParse(s.Trim(), out value)
               && value >= min && value <= max;
    }

    private static bool TryDouble(string? s, double min, out double value)
    {
        value = 0;
        return s != null
               && double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out value)
               && value >= min;
    }

    /// <summary>用法说明。按当前输出语言二选一（判定见 <see cref="Util.Lang"/>）。</summary>
    public static string Help() => Lang.IsEnglish ? HelpEnglish() : HelpChinese();

    /// <summary>中文帮助。</summary>
    private static string HelpChinese()
    {
        var sb = new StringBuilder();
        sb.AppendLine("DiskGrowthMonitor - 磁盘空间占用扫描与增长对比工具");
        sb.AppendLine();
        sb.AppendLine("用法：");
        sb.AppendLine("  DiskGrowthMonitor.exe [选项]");
        sb.AppendLine();
        sb.AppendLine("扫描范围（必须指定其一，二者可同时使用）：");
        sb.AppendLine("  -d, --drive <ALL|C|D,...>   扫描全部本地固定磁盘，或指定盘符（可逗号分隔多个）");
        sb.AppendLine("  -r, --root  <目录>          只扫描指定目录（可多次指定，便于针对性地监控某几棵树）");
        sb.AppendLine();
        sb.AppendLine("报告内容控制：");
        sb.AppendLine("  -t, --top <N>               增长榜/缩减榜条数，默认 30");
        sb.AppendLine("      --detail-top <N>        详细明细表最大行数，默认 500");
        sb.AppendLine("      --detail-min-mb <N>     详细明细表最小变化量，默认 1 MB");
        sb.AppendLine("  -m, --min-mb <N>            只报告增长量超过 N MB 的目录，默认 100");
        sb.AppendLine("  -p, --min-percent <N>       只报告增长百分比超过 N% 的目录，默认 0");
        sb.AppendLine();
        sb.AppendLine("排除与跳过：");
        sb.AppendLine("  -x, --exclude <路径前缀>    额外排除目录（可多次指定）");
        sb.AppendLine("      --no-default-exclude    不排除默认目录（Windows / $Recycle.Bin / System Volume Information）");
        sb.AppendLine("      --reset-skips           清空历史「权限不足 / IO 错误」记录并重新尝试扫描");
        sb.AppendLine("      --no-knowledge-rescan   关闭知识库豁免：命中目录用途知识库的目录不再突破默认排除");
        sb.AppendLine("                              与跳过清单（默认开启豁免 —— 知识库里的目录每轮都重新尝试扫描）");
        sb.AppendLine("      --follow-reparse        跟随目录符号链接与 junction（默认不跟随）");
        sb.AppendLine("      --no-dedup              关闭硬链接去重（更快，但可能重复计数）");
        sb.AppendLine();
        sb.AppendLine("扫描性能：");
        sb.AppendLine("      --full                  强制全量扫描（禁用 USN 增量复用）");
        sb.AppendLine("      --threads <N>           并行扫描线程数，0 = 自动（默认按扫描根数并行，上限物理核数；");
        sb.AppendLine("                              空转基线实测并行收益在物理核处饱和，超出只耗资源不提速）");
        sb.AppendLine("      --enum-engine <档位>    目录枚举引擎，取 auto | extd | both | win32，默认 auto");
        sb.AppendLine("                              auto：按卷实测后自动降级（extd 60 → both 37 → win32）");
        sb.AppendLine("                              extd / both：目录项直出文件标识，去重无需逐文件开句柄（快）");
        sb.AppendLine("                              win32：改动前的行为（逐文件开句柄），运行时回退用");
        sb.AppendLine("                              三档的结果口径完全一致，只有速度不同：");
        sb.AppendLine("                              用 --enum-engine=win32 可一键退回旧行为做对照");
        sb.AppendLine("      --no-elevate            不尝试以管理员身份重启自己");
        sb.AppendLine("                              （默认会尝试：读 USN 变更日志与 MFT 目录数估算都需要管理员权限，");
        sb.AppendLine("                                非管理员时只能全量扫描且进度百分比为近似值）");
        sb.AppendLine();
        sb.AppendLine("数据库维护：");
        sb.AppendLine("      --reset-db              清空数据库全部记录（批次/快照/对比历史/跳过清单/USN 状态），");
        sb.AppendLine("                              使程序回到「从未扫描」状态；需在交互式终端确认 y/N，执行后退出");
        sb.AppendLine();
        sb.AppendLine("输出与存储：");
        sb.AppendLine("      （数据库固定为 <exe 目录>\\disk_growth.db，无开关；旧命令中的 --db 会被忽略并提示）");
        sb.AppendLine("  -o, --out <文件路径>        HTML 报告输出路径，默认 .\\reports\\disk_growth_report_<时间>.html");
        sb.AppendLine("      --no-report             只扫描入库，不生成报告");
        sb.AppendLine("      --open                  报告生成后用默认浏览器打开");
        sb.AppendLine("      --list-drives           列出本机磁盘后退出");
        sb.AppendLine("      --list-skips            列出已记录的跳过清单后退出");
        sb.AppendLine("      --list-knowledge        列出目录用途知识库条目后退出");
        sb.AppendLine("      --show-env              探测运行环境（OS/CPU/内存/介质/文件系统/语言）并打印推导出的");
        sb.AppendLine("                              扫描策略后退出（介质探测需管理员权限，否则显示「未知」）");
        sb.AppendLine("      --dev                   开发者模式：额外打印诊断信息（环境明细、增量决策、MFT 估算、");
        sb.AppendLine("                              枚举引擎探测、知识库/跳过清单记账等），默认关闭");
        sb.AppendLine("      --lang <档位>           程序自己的提示文本用哪种语言，取 auto | zh | en，默认 auto");
        sb.AppendLine("                              auto：界面语言与控制台代码页都是中文时用中文提示，否则用英文");
        sb.AppendLine("                              （探测结果见 --show-env 的「语言」小节；数据内容不随此开关变化）");
        sb.AppendLine("  -h, --help                  显示本帮助");
        sb.AppendLine();
        sb.AppendLine("示例：");
        sb.AppendLine("  DiskGrowthMonitor.exe -d C                      扫描 C 盘并对比上一轮快照");
        sb.AppendLine("  DiskGrowthMonitor.exe -d ALL -m 500 -t 50       扫描全部磁盘，只看增长 500MB 以上，显示前 50");
        sb.AppendLine("  DiskGrowthMonitor.exe -r D:\\Build -r D:\\Data   只监控两个目录");
        sb.AppendLine("  DiskGrowthMonitor.exe -d D -o D:\\report.html    指定报告输出路径");
        sb.AppendLine("  DiskGrowthMonitor.exe --reset-db                清空数据库，回到「从未扫描」状态（需确认 y/N）");
        return sb.ToString();
    }

    /// <summary>
    /// 英文帮助。与 <see cref="HelpChinese"/> 逐条对应 —— 两边必须同步修改，
    /// 新增开关时建议先写中文再照抄结构写英文，避免漏项。
    /// </summary>
    private static string HelpEnglish()
    {
        var sb = new StringBuilder();
        sb.AppendLine("DiskGrowthMonitor - disk space usage scanner and growth comparison tool");
        sb.AppendLine();
        sb.AppendLine("Usage:");
        sb.AppendLine("  DiskGrowthMonitor.exe [options]");
        sb.AppendLine();
        sb.AppendLine("Scan scope (at least one is required; both may be used together):");
        sb.AppendLine("  -d, --drive <ALL|C|D,...>   Scan all local fixed disks, or the listed drives (comma-separated)");
        sb.AppendLine("  -r, --root  <directory>     Scan only the given directory (repeatable, for watching a few trees)");
        sb.AppendLine();
        sb.AppendLine("Report content:");
        sb.AppendLine("  -t, --top <N>               Rows in the growth/shrink rankings, default 30");
        sb.AppendLine("      --detail-top <N>        Max rows in the detail table, default 500");
        sb.AppendLine("      --detail-min-mb <N>     Minimum change to appear in the detail table, default 1 MB");
        sb.AppendLine("  -m, --min-mb <N>            Only report directories growing by more than N MB, default 100");
        sb.AppendLine("  -p, --min-percent <N>       Only report directories growing by more than N%, default 0");
        sb.AppendLine();
        sb.AppendLine("Exclusions and skips:");
        sb.AppendLine("  -x, --exclude <prefix>      Extra directory prefix to exclude (repeatable)");
        sb.AppendLine("      --no-default-exclude    Do not exclude the default directories (Windows / $Recycle.Bin /");
        sb.AppendLine("                              System Volume Information)");
        sb.AppendLine("      --reset-skips           Clear historical \"access denied / IO error\" records and retry them");
        sb.AppendLine("      --no-knowledge-rescan   Disable the knowledge-base exemption: directories matching the");
        sb.AppendLine("                              directory-purpose knowledge base no longer override the default");
        sb.AppendLine("                              exclusions and the skip list (exemption is on by default)");
        sb.AppendLine("      --follow-reparse        Follow directory symbolic links and junctions (off by default)");
        sb.AppendLine("      --no-dedup              Disable hardlink dedup (faster, but hardlinked data may be counted twice)");
        sb.AppendLine();
        sb.AppendLine("Scan performance:");
        sb.AppendLine("      --full                  Force a full scan (disable USN incremental reuse)");
        sb.AppendLine("      --threads <N>           Parallel scan threads; 0 = auto (default: one per scan root, capped at");
        sb.AppendLine("                              the physical core count - the measured baseline saturates there)");
        sb.AppendLine("      --enum-engine <mode>    Directory enumeration engine: auto | extd | both | win32, default auto");
        sb.AppendLine("                              auto: probe per volume, then degrade (extd 60 -> both 37 -> win32)");
        sb.AppendLine("                              extd / both: file IDs straight from directory entries, so dedup needs");
        sb.AppendLine("                              no per-file handle (fast)");
        sb.AppendLine("                              win32: the pre-change behaviour (per-file handles), for runtime rollback");
        sb.AppendLine("                              All three modes produce identical results; only speed differs.");
        sb.AppendLine("      --no-elevate            Do not try to restart itself as administrator");
        sb.AppendLine("                              (by default it does: reading the USN change journal and estimating the");
        sb.AppendLine("                              MFT directory count both need administrator rights; without them every");
        sb.AppendLine("                              run is a full scan and the progress percentage is only approximate)");
        sb.AppendLine();
        sb.AppendLine("Database maintenance:");
        sb.AppendLine("      --reset-db              Clear all database records (runs / snapshots / growth history / skip");
        sb.AppendLine("                              list / USN state), returning the tool to a \"never scanned\" state;");
        sb.AppendLine("                              asks for y/N confirmation in an interactive terminal, then exits");
        sb.AppendLine();
        sb.AppendLine("Output and storage:");
        sb.AppendLine("      (the database is fixed at <exe folder>\\disk_growth.db; there is no switch for it,");
        sb.AppendLine("       a legacy --db is ignored with a hint)");
        sb.AppendLine("  -o, --out <file path>       HTML report path, default .\\reports\\disk_growth_report_<time>.html");
        sb.AppendLine("      --no-report             Scan and store only, do not generate a report");
        sb.AppendLine("      --open                  Open the report in the default browser after generating it");
        sb.AppendLine("      --list-drives           List local drives and exit");
        sb.AppendLine("      --list-skips            List the recorded skip entries and exit");
        sb.AppendLine("      --list-knowledge        List directory-purpose knowledge base entries and exit");
        sb.AppendLine("      --show-env              Probe the runtime environment (OS / CPU / memory / media / file");
        sb.AppendLine("                              system / language) and print the derived scan strategy, then exit");
        sb.AppendLine("                              (media probing needs administrator rights, otherwise it shows \"unknown\")");
        sb.AppendLine("      --dev                   Developer mode: print extra diagnostics (environment detail, incremental");
        sb.AppendLine("                              decisions, MFT estimates, enum-engine probing, knowledge/skip");
        sb.AppendLine("                              accounting), off by default");
        sb.AppendLine("      --lang <mode>           Language of the program's own messages: auto | zh | en, default auto");
        sb.AppendLine("                              auto: Chinese messages only when BOTH the UI language and the console");
        sb.AppendLine("                              code page are Chinese; otherwise English");
        sb.AppendLine("                              (see the \"Language\" section of --show-env; data is unaffected)");
        sb.AppendLine("  -h, --help                  Show this help");
        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine("  DiskGrowthMonitor.exe -d C                      Scan drive C and compare with the previous snapshot");
        sb.AppendLine("  DiskGrowthMonitor.exe -d ALL -m 500 -t 50       Scan all disks, only growth over 500MB, show top 50");
        sb.AppendLine("  DiskGrowthMonitor.exe -r D:\\Build -r D:\\Data    Watch two directories only");
        sb.AppendLine("  DiskGrowthMonitor.exe -d D -o D:\\report.html    Write the report to a specific path");
        sb.AppendLine("  DiskGrowthMonitor.exe --reset-db                Clear the database, back to \"never scanned\" (asks y/N)");
        return sb.ToString();
    }
}
