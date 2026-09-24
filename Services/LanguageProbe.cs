using System.Globalization;
using DiskGrowthMonitor.Models;
using DiskGrowthMonitor.Native;
using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// 操作系统语言探查与输出语言裁决（阶段 P 新增项）。
///
/// <para><b>裁决规则（用户 2026-09-24 裁决，双条件）</b>：界面语言为中国语系
/// <b>且</b> 控制台输出代码页为中文代码页时，才用中文提示；否则一律英文。
/// 这样「英文 Windows + 中文 cmd」不会冒出中文，而「中文 Windows 但控制台是非中文代码页」
/// 也不会出现中文提示被终端渲成问号的情形。</para>
///
/// <para><b>中国语系的判定范围</b>：LANGID 低 10 位为主语言 ID，<c>LANG_CHINESE = 0x04</c>。
/// 它同时覆盖简体（0x0804）、繁体（0x0404）、港澳（0x0C04）、新加坡（0x1004）——
/// 这些都在「中文」之下，不再细分。</para>
///
    /// <para><b>中文代码页的判定范围</b>：936（GBK/GB2312，简体）、950（Big5，繁体）、
    /// 54936（GB18030）。代码页必须在控制台被切成 UTF-8 之前读取，见
    /// <see cref="Native.SystemNative.TryGetLanguageInfo"/> 的说明。</para>
    ///
    /// <para><b>第 57 轮 R3 修法</b>：控制台代码页这一路判定改为「<b>输出代码页或输入代码页</b>
    /// 任一命中中文代码页即可」。原因是 <c>Console.OutputEncoding = Encoding.UTF8</c> 会把控制台
    /// 输出代码页永久改成 65001，导致同一个窗口里第二次运行必然判英文；而输入代码页本程序从不修改，
    /// 是没被自己污染过的证据。配套的 <see cref="RestoreConsoleCodePage"/> 在退出前把输出代码页写回原值，
    /// 既恢复判定依据，也停止污染用户终端。</para>
    ///
    /// <para>语言探查<b>不参与</b>任何扫描/统计/落库逻辑，只影响提示文本的措辞 ——
    /// 失败或判错都不会影响数据正确性，可以用 <c>--lang</c> 强制覆盖。</para>
    /// </summary>
    public static class LanguageProbe
    {
        /// <summary>中文代码页集合：936 GBK/GB2312（简体）、950 Big5（繁体）、54936 GB18030。</summary>
        private static readonly uint[] ChineseCodePages = { 936, 950, 54936 };

    /// <summary><c>--lang</c> 的合法取值。<c>auto</c> 产出 <c>null</c>（表示走自动探查）。</summary>
    /// <returns>false = 取值无法识别（由参数解析报错，语言按 auto 处理）。</returns>
    public static bool TryParseName(string? text, out OutputLanguage? forced)
    {
        forced = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "auto": forced = null; return true;
            case "zh": forced = OutputLanguage.Zh; return true;
            case "en": forced = OutputLanguage.En; return true;
            default: return false;
        }
    }

    /// <summary>
    /// 从命令行参数里取出 <c>--lang</c> 的取值（支持 <c>--lang=zh</c> 与 <c>--lang zh</c> 两种写法）。
    ///
    /// 为什么不复用 <see cref="DiskGrowthMonitor.Cli.Options.Parse"/>：语言必须在**解析参数之前**定下来
    /// （「参数错误」这行提示本身就要用正确的语言打印），所以这里自己做一次最小扫描。
    /// 取值非法时返回 null（等价于 auto），错误提示留给正式解析阶段报告。
    /// </summary>
    public static string? FindLangArg(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--lang", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length) return args[i + 1];
                return null;
            }
            if (a.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
                return a.Substring("--lang=".Length);
        }
        return null;
    }

    /// <summary>
    /// 探查并裁决。<paramref name="args"/> 为完整命令行（用于识别 <c>--lang</c>）。
    /// 返回值同时给出「结论」与「依据」，供 <c>--show-env</c> 逐项核对。
    /// </summary>
    public static LanguageInfo Resolve(string[] args) => Decide(FindLangArg(args));

    /// <summary>
    /// 探查并裁决。三个探测值一律留在结果里（即使被 <c>--lang</c> 强制），
    /// 这样「强制值」与「本机真实情况」能在环境输出里同时看到，排错时不必重跑。
    /// </summary>
    public static LanguageInfo Decide(string? langArg)
    {
        var info = new LanguageInfo();

        info.ProbeSucceeded = SystemNative.TryGetLanguageInfo(
            out uint userLangId, out uint systemLangId,
            out uint consoleOutputCodePage, out uint consoleInputCodePage);
        info.UserLanguageId = userLangId;
        info.SystemLanguageId = systemLangId;
        info.ConsoleOutputCodePage = consoleOutputCodePage;
        info.ConsoleInputCodePage = consoleInputCodePage;
        info.UiCultureName = CultureNameOf(userLangId);

        // ① 显式指定优先，且不再自动裁决
        if (TryParseName(langArg, out var forced) && forced.HasValue)
        {
            info.Language = forced.Value;
            info.Decision = LanguageDecision.Forced;
            return info;
        }

        // ② 双条件：界面语言为中国语系 且 控制台代码页为中文代码页
        bool uiChinese = info.UiIsChinese;
        bool consoleChinese = info.ConsoleIsChinese;

        if (uiChinese && consoleChinese)
        {
            info.Language = OutputLanguage.Zh;
            info.Decision = LanguageDecision.UiAndConsoleChinese;
        }
        else if (!uiChinese)
        {
            info.Language = OutputLanguage.En;
            info.Decision = LanguageDecision.UiNotChinese;
        }
        else
        {
            info.Language = OutputLanguage.En;
            info.Decision = LanguageDecision.ConsoleNotChinese;
        }
        return info;
    }

    /// <summary>
    /// 退出前把控制台输出代码页恢复成探查到的原值（第 57 轮 R3 修法之二）。
    ///
    /// <para>由 <c>Program.Main</c> 的 <c>finally</c> 调用，且必须是<b>最后一步</b> ——
    /// 恢复之后不得再有任何输出，否则 UTF-8 字节会被按恢复后的代码页解码。</para>
    ///
    /// <para>恢复源取 <see cref="LanguageInfo.ConsoleOutputCodePage"/>（进入时的真实值）：
    /// 它可能已经是 65001（上一次运行遗留、或被强杀没来得及恢复），此时写回 65001 等于空操作，
    /// 不会把用户终端改坏；判定那头则靠输入代码页兜住，见 <see cref="LanguageInfo.ConsoleIsChinese"/>。</para>
    /// </summary>
    public static void RestoreConsoleCodePage()
    {
        SystemNative.RestoreConsoleOutputCodePage(Lang.Info.ConsoleOutputCodePage);
    }

    /// <summary>控制台输出代码页是否为中文代码页（见 <see cref="ChineseCodePages"/>）。</summary>
    public static bool IsChineseCodePage(uint codePage)
    {
        for (int i = 0; i < ChineseCodePages.Length; i++)
            if (ChineseCodePages[i] == codePage) return true;
        return false;
    }

    /// <summary>
    /// LANGID → 区域性名（如 0x0804 → "zh-CN"）。取不到时退回 <c>0xXXXX</c> 形式，
    /// 保证环境输出里永远有可核对的东西，而不是空白。
    /// </summary>
    private static string CultureNameOf(uint langId)
    {
        if (langId == 0) return string.Empty;
        try
        {
            // LANGID 与 LCID（低 16 位）在绝大多数语言上一致，用 CultureInfo(int) 直接换名
            return new CultureInfo((int)(langId & 0xFFFF)).Name;
        }
        catch
        {
            return "0x" + (langId & 0xFFFF).ToString("X4");
        }
    }
}
