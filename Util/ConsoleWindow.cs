using System.Runtime.InteropServices;

namespace DiskGrowthMonitor.Util;

/// <summary>
/// 控制台窗口的 best-effort 隐藏（提权自举的日志中继模式专用）。
///
/// 背景：提权子进程必然获得自己的新控制台窗口（UAC 由 AppInfo 服务用新令牌创建进程，
/// 无法继承父进程的控制台）。日志中继模式下子进程的全部输出都写进临时日志、由父进程
/// 回显到原窗口，这个自带窗口就只剩「空壳」——跑完即关倒是无妨，但扫描期间它一直
/// 停在桌面上毫无内容，观感像程序卡死。因此把它隐藏掉（SW_HIDE 只藏不关，
/// 进程结束时窗口随之销毁，无残留）。
///
/// 失败一律静默：隐藏失败只是「多看到一个空窗口」，不值得为它冒任何异常风险。
/// </summary>
internal static class ConsoleWindow
{
    private const int SW_HIDE = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>隐藏当前进程的控制台窗口。无控制台 / 调用失败时不做任何事。</summary>
    public static void TryHide()
    {
        try
        {
            IntPtr hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);
        }
        catch
        {
            // best-effort：任何失败都按「没藏成」处理，不影响主流程
        }
    }
}
