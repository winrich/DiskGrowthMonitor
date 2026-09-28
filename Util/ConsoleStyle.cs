using System.Runtime.InteropServices;

namespace DiskGrowthMonitor.Util;

/// <summary>
/// 控制台 ANSI 颜色样式（best-effort）。
///
/// 只有同时满足下面三点才会真正输出 ANSI 转义序列，否则一律原样返回纯文本：
///   ① <see cref="Enable"/> 的 <paramref name="interactive"/> 为 true —— 调用方（Program.Run）
///      用它区分「输出直达控制台」与「日志中继 / 重定向」；后两种场景下 ANSI 码会污染日志，
///      或混进机器可读的 REPORT_PATH 抓取流，必须禁用；
///   ② 标准输出句柄是真正的控制台（GetConsoleMode 成功）—— 被 shell 重定向到文件时，
///      STD_OUTPUT_HANDLE 指向文件而非控制台，GetConsoleMode 直接失败 ⇒ 自然禁用；
///   ③ 终端支持 VT（SetConsoleMode 加上 ENABLE_VIRTUAL_TERMINAL_PROCESSING 成功）——
///      Windows 10 1607 起支持，WS2012 等旧控制台会拒绝 ⇒ 自然降级为纯文本。
///
/// 因此本类**绝不抛出异常**、**绝不改变既有文本宽度**（未启用时等价于恒等函数），
/// 调用方可以放心在任何输出点套用，无需关心终端能力。
/// </summary>
internal static class ConsoleStyle
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private static bool _enabled;

    /// <summary>
    /// 尝试启用颜色。幂等，失败或非交互场景静默降级。
    /// </summary>
    /// <param name="interactive">输出是否直达控制台（false = 日志中继/重定向，禁用颜色）。</param>
    public static void Enable(bool interactive)
    {
        if (!interactive) return;
        try
        {
            IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
            if (h == IntPtr.Zero || h == InvalidHandleValue) return;
            if (!GetConsoleMode(h, out uint mode)) return;      // 非控制台（重定向到文件）
            _enabled = SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
        catch
        {
            _enabled = false;
        }
    }

    /// <summary>章节标题（青色）。</summary>
    public static string Section(string s) => Wrap(s, "36");

    /// <summary>关键数字/摘要主行（亮白加粗）。</summary>
    public static string Key(string s) => Wrap(s, "1;97");

    /// <summary>成功（绿色）。</summary>
    public static string Success(string s) => Wrap(s, "32");

    /// <summary>警告（黄色）。</summary>
    public static string Warning(string s) => Wrap(s, "33");

    /// <summary>错误（红色）。</summary>
    public static string Error(string s) => Wrap(s, "31");

    /// <summary>次要说明（暗灰）。</summary>
    public static string Muted(string s) => Wrap(s, "90");

    private static string Wrap(string s, string code)
        => _enabled ? "\x1b[" + code + "m" + s + "\x1b[0m" : s;
}
