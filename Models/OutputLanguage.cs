namespace DiskGrowthMonitor.Models;

/// <summary>
/// 程序提示文本的输出语言档位。
///
/// 注意：这里只描述「程序自己的话」（标题、标签、提示、告警、报告文案）用哪种语言。
/// 知识库里的目录用途描述、真实目录名这类**数据**不随它变化 —— 数据自带语言来源
/// （知识库有独立的英文列，见 <c>dir_knowledge</c> 的 <c>title_en</c> 一族）。
/// </summary>
public enum OutputLanguage
{
    /// <summary>简体中文。</summary>
    Zh = 0,

    /// <summary>英语（非中文操作系统的默认档）。</summary>
    En = 1
}

/// <summary>
/// 「为什么判定成这个语言」——判定依据的<b>机器可读</b>记录。
///
/// 为什么不直接存一句现成的说明文本：语言判定必须发生在**任何输出之前**，
/// 而那一刻恰恰还不能生成提示文本 —— 否则「用中文还是英文写这句话」本身就死循环了。
/// 所以只记录事实（枚举），等到真正要打印时（环境行 / <c>--show-env</c>）再按已定档位翻译。
/// </summary>
public enum LanguageDecision
{
    /// <summary>由 <c>--lang</c> 显式指定，未做自动探查。</summary>
    Forced,

    /// <summary>界面语言与控制台代码页**都是**中文（代码页按输出/输入取或，见 LanguageInfo.ConsoleIsChinese）。</summary>
    UiAndConsoleChinese,

    /// <summary>界面语言不是中文（此时不看控制台代码页）。</summary>
    UiNotChinese,

    /// <summary>界面语言是中文，但控制台代码页（输出与输入）都不是中文代码页。</summary>
    ConsoleNotChinese
}
