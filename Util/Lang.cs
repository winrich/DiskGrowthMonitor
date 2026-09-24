using DiskGrowthMonitor.Models;

namespace DiskGrowthMonitor.Util;

/// <summary>
/// 输出语言上下文 + 双语取词。
///
/// <para><b>为什么要有这个类</b>：程序要能按操作系统语言给出中文或英文提示。
/// 判定发生在 <c>Program.Main</c> 的第一行（必须早于任何输出，也早于
/// <c>Console.OutputEncoding</c> 改写控制台代码页），之后所有提示文本都从这里取。</para>
///
/// <para><b>取词方式刻意选择「内联双语」而非「键值表」</b>：
/// 调用点写成 <c>Lang.T("中文原句", "English sentence")</c>，中文与英文并排在同一行。
/// 好处是——① 不引入一套键名，也就不存在「键写错 → 静默返回空串」这类故障；
/// ② 不会出现「改了中文忘了改英文」时两边隔了几个屏找不到配对项；
/// ③ 代码审阅时一眼能看出译文与原文是否对应。
/// 代价是本工程的字符串无法交给外部翻译工具批量处理 —— 本项目为单机工具，不适用该场景。</para>
///
/// <para><b>不要在这里放「数据」</b>：目录路径、卷标、知识库描述都不属于提示文本，
/// 不随语言切换（知识库有自己的英文列）。</para>
/// </summary>
public static class Lang
{
    /// <summary>未初始化时的兜底档位：英文（宁可给英文，也不要乱码或半中半英）。</summary>
    private static LanguageInfo _info = new();

    /// <summary>本次运行的语言探查结果（含判定依据，供 <c>--show-env</c> 展示）。</summary>
    public static LanguageInfo Info => _info;

    /// <summary>当前输出语言。</summary>
    public static OutputLanguage Current => _info.Language;

    /// <summary>当前是否输出英文。</summary>
    public static bool IsEnglish => _info.Language == OutputLanguage.En;

    /// <summary>由 <c>Program.Main</c> 在一切输出之前调用一次。</summary>
    public static void Initialize(LanguageInfo info) => _info = info ?? new LanguageInfo();

    /// <summary>按当前语言二选一。</summary>
    public static string T(string zh, string en) => IsEnglish ? en : zh;

    /// <summary>
    /// 按当前语言二选一并做占位符填充。
    /// 用当前区域性格式化（与 <c>$"…"</c> 插值行为一致），避免数字/日期格式在两种语言下不一致。
    /// </summary>
    public static string F(string zh, string en, params object[] args)
        => string.Format(IsEnglish ? en : zh, args);

    /// <summary>把 <see cref="OutputLanguage"/> 翻译成可读名称（用于环境输出）。</summary>
    public static string DescribeLanguage(OutputLanguage lang)
        => lang == OutputLanguage.Zh ? T("中文", "Chinese") : T("英文", "English");

    /// <summary>把判定依据翻译成一句可读说明（用于环境输出）。</summary>
    public static string DescribeDecision(LanguageInfo info)
    {
        switch (info.Decision)
        {
            case LanguageDecision.Forced:
                return F("由 --lang={0} 显式指定，未做自动探查", "forced by --lang={0}; no auto-detection performed",
                    info.Language == OutputLanguage.Zh ? "zh" : "en");

            case LanguageDecision.UiAndConsoleChinese:
                return T("界面语言为中文，且控制台代码页为中文代码页 → 中文提示",
                         "UI language is Chinese and the console code page is a Chinese one -> Chinese messages");

            case LanguageDecision.ConsoleNotChinese:
                return F("界面语言为中文（{0}），但控制台代码页 {1}（输出/输入）都不是中文代码页 → 英文提示",
                         "UI language is Chinese ({0}) but console code pages {1} (output/input) are not Chinese ones -> English messages",
                    Show(info.UiCultureName, "未知", "unknown"), info.ConsoleCodePageText);

            default:
                return F("界面语言不是中文（{0}）→ 英文提示",
                         "UI language is not Chinese ({0}) -> English messages",
                    Show(info.UiCultureName, "未知", "unknown"));
        }
    }

    /// <summary>空串/占位符统一处理（两个语言各自给默认文本，避免出现空白括号）。</summary>
    private static string Show(string? value, string zhFallback, string enFallback)
        // net45 的 BCL 没有 [NotNullWhen] 标注，编译器无法由 IsNullOrEmpty 推出非空 ⇒ 需要 !
        => string.IsNullOrEmpty(value) ? T(zhFallback, enFallback) : value!;
}
