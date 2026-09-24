namespace DiskGrowthMonitor.Models;

/// <summary>
/// 操作系统语言探查结果（阶段 P 的一部分）。
///
/// 事实来源互相独立，都要留证据：
///   · <c>GetUserDefaultUILanguage</c> —— <b>当前用户</b>界面语言（多用户机上可能不同于系统语言）；
///   · <c>GetSystemDefaultUILanguage</c> —— 系统安装时的界面语言（仅诊断展示，不参与判定）；
///   · <c>GetConsoleOutputCP</c> —— 控制台**本来的**输出代码页；
///   · <c>GetConsoleCP</c> —— 控制台**输入**代码页（本程序从不修改它，是最干净的一份证据）。
///
/// 🔴 关键约束：两个代码页都必须在程序把控制台切到 UTF-8 <b>之前</b>读。
/// 见 <c>Program.Run</c> 开头的 <c>Console.OutputEncoding = Encoding.UTF8</c> ——
/// 那一行会把控制台输出代码页改成 65001，之后再去读就恒为 65001，
/// 「中文代码页」那一路判定永远不成立（本项目 2026-09-24 第 57 轮实测踩到）。
/// </summary>
public sealed class LanguageInfo
{
    /// <summary>GetUserDefaultUILanguage 的 LANGID（含子语言）；0 = 探测失败。</summary>
    public uint UserLanguageId { get; set; }

    /// <summary>GetSystemDefaultUILanguage 的 LANGID；0 = 探测失败。</summary>
    public uint SystemLanguageId { get; set; }

    /// <summary>控制台输出代码页（GetConsoleOutputCP）；0 = 无控制台。</summary>
    public uint ConsoleOutputCodePage { get; set; }

    /// <summary>
    /// 控制台输入代码页（GetConsoleCP）；0 = 无控制台。
    /// 它与 <see cref="ConsoleOutputCodePage"/> <b>取或</b> 参与「控制台是否中文代码页」的判定 ——
    /// 因为本程序只改输出代码页，输入代码页是一份没被自己污染过的证据（第 57 轮 R3 修法）。
    /// </summary>
    public uint ConsoleInputCodePage { get; set; }

    /// <summary>控制台是否处于中文代码页（输出与输入任一命中即算，见两处说明）。</summary>
    public bool ConsoleIsChinese => IsChineseCodePage(ConsoleOutputCodePage)
                                    || IsChineseCodePage(ConsoleInputCodePage);

    /// <summary>控制台输出代码页数值文本（供提示行与报告用，避免各处重复写）。</summary>
    public string ConsoleCodePageText => ConsoleInputCodePage > 0 && ConsoleInputCodePage != ConsoleOutputCodePage
        ? ConsoleOutputCodePage + "/" + ConsoleInputCodePage
        : ConsoleOutputCodePage.ToString();

    private static bool IsChineseCodePage(uint codePage)
        => codePage == 936 || codePage == 950 || codePage == 54936;

    /// <summary>界面语言对应的区域性名（如 "zh-CN"）；取不到时为空串。</summary>
    public string UiCultureName { get; set; } = string.Empty;

    /// <summary>最终采用的输出语言。</summary>
    public OutputLanguage Language { get; set; } = OutputLanguage.En;

    /// <summary>判定依据（机器可读，供输出时翻译成提示）。</summary>
    public LanguageDecision Decision { get; set; } = LanguageDecision.UiNotChinese;

    /// <summary>是否为 <c>--lang</c> 显式指定（此时未做自动探查，两个 API 的值仅供展示）。</summary>
    public bool IsForced => Decision == LanguageDecision.Forced;

    /// <summary>
    /// 探查本身是否成功。false 表示三个 API 全部拿不到值（正常 Windows 上不会发生），
    /// 此时按「非中文」保守处理 —— 宁可给出英文提示，也不要出现乱码或半中半英。
    /// </summary>
    public bool ProbeSucceeded { get; set; }

    /// <summary>界面语言的主语言 ID 是否为中国语系（LANG_CHINESE = 0x04，覆盖简体/繁体/港澳）。</summary>
    public bool UiIsChinese => (UserLanguageId & PrimaryLangMask) == LangChinese;

    /// <summary>LANGID 低 10 位 = 主语言 ID。</summary>
    public const uint PrimaryLangMask = 0x3FF;

    /// <summary>LANG_CHINESE。</summary>
    public const uint LangChinese = 0x04;
}
