using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskGrowthMonitor.Util;

/// <summary>
/// 提权子进程「接管父进程控制台」（提权输出方案 B，已由 <c>_optdb/attachpoc/</c> 验证）。
///
/// 背景：Verb="runas" 只能 UseShellExecute=true，.NET Framework 禁止该模式重定向子进程的流，
/// 且提权进程必然获得自己的新控制台 ⇒ 原窗口既看不到输出、新窗口又跑完即关。
/// 方案 A（日志中继）已解决「看得到结果」；方案 B 在此之上把**交互性**也拿回来：
/// 子进程 <see cref="FreeConsole"/> 脱离自己的新控制台（窗口随之销毁），
/// <see cref="AttachConsole"/> 接管父进程的控制台，再把 .NET 的 Console.Out/Error
/// 重挂到新句柄上 —— 之后 <see cref="Console.SetCursorPosition"/> 等光标 API 全部跟随
/// 新句柄工作（PoC 4/4 已验证），完整的多行进度条得以在原窗口原样呈现。
///
/// 已验证的关键事实（PoC 结论，2026-09-24）：
///   · attach 可跨提权令牌边界（父非管理员过滤令牌 → 子完整令牌）；
///   · SetStdHandle 之后 <see cref="Console.OpenStandardOutput"/> 取到的是新 CONOUT$；
///   · 流编码用控制台当前输出代码页（中文 GBK 逐字正确；PoC 教训：
///     WriteConsoleW 的 P/Invoke 漏 CharSet.Unicode 会把字符串按 ANSI 编组出乱码）。
///
/// 🔴 PoC 的**覆盖盲区**（2026-09-24 生产事故，退出码 0xE0434352 = -532462766）：
///   PoC 把 <c>CloseHandle(conout)</c> 放在**所有输出之后**，从未测过「关掉句柄再写」，
///   于是它看起来像一句无害的收尾清理。生产代码照搬时把它挪进了 try/finally，
///   返回 true 后程序继续用 Console 输出 ⇒ 句柄已悬空、首次 WriteFile 即抛 IOException，
///   而 CLR 打印该未处理异常走的又是同一个 Console.Error ⇒ 窗口全黑、日志只剩握手行、
///   连堆栈都拿不到。**结论：SetStdHandle 用过的句柄必须活到进程结束，绝不能 CloseHandle。**
///
/// 失败语义：任何一步失败都返回 false —— 调用方（Program.Main）随即退回
/// 「输出写日志文件 + 父进程中继回显」的方案 A 行为，两层兜底。
/// 注意：FreeConsole 成功而后续失败时本进程已无控制台，此后任何控制台 I/O 都会抛异常，
/// 必须保证调用方在 false 返回后立刻改走日志文件路径（现状正是如此）。
/// </summary>
internal static class ConsoleAttach
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int which, IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const int STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

    /// <summary>
    /// 尝试接管 <paramref name="parentPid"/> 的控制台并把 Console.Out/Error 重挂上去。
    /// 成功返回 true（此后 Console.* 直达父窗口，进度条保持完整交互）；失败返回 false。
    /// </summary>
    public static bool TryAttach(int parentPid)
    {
        if (parentPid <= 0) return false;
        try
        {
            if (!FreeConsole()) return false;
            if (!AttachConsole((uint)parentPid)) return false;

            IntPtr conout = CreateFileW("CONOUT$",
                GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (conout == IntPtr.Zero || conout == InvalidHandleValue) return false;

            if (!SetStdHandle(STD_OUTPUT_HANDLE, conout)) return false;
            if (!SetStdHandle(STD_ERROR_HANDLE, conout)) return false;

            // 编码取新控制台当前的输出代码页：与父窗口一致，中文逐字正确
            var enc = Console.OutputEncoding;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), enc) { AutoFlush = true });

            // 🔴 到此为止，绝不能 CloseHandle(conout)（2026-09-24 生产事故 0xE0434352）：
            //    SetStdHandle 只是把**句柄值**写进进程的标准句柄槽，并不增加引用计数；
            //    一旦关闭，STD_OUTPUT / STD_ERROR 立即变成悬空句柄，此后任何 Console 输出
            //    都会让 WriteFile 失败并抛 IOException —— 而 CLR 打印该未处理异常走的又是
            //    同一个 Console.Error，于是表现为「窗口全黑 + 日志只剩握手行 + 退出码 -532462766」。
            //    句柄随进程退出由系统回收，此处唯一代价是进程存活期间多占一个 CONOUT$ 句柄。
            return true;
        }
        catch
        {
            // 任何异常都按「接管失败」处理，调用方退回日志中继
            return false;
        }
    }

    /// <summary>
    /// 兜底：<see cref="TryAttach"/> 失败时本进程已无控制台（FreeConsole 已生效），
    /// 若调用方的日志文件也打不开，后续任何控制台 I/O 都会失败。此时补分配一个新控制台
    /// （Windows 会随之把标准句柄指向它），使「退回自带窗口」这条最坏路径仍有可用输出目标 ——
    /// 等价于修复前「新窗口」的行为。仅供失败路径调用，正常路径不产生任何窗口。
    /// </summary>
    public static void EnsureConsole()
    {
        try { AllocConsole(); } catch { /* 分配不到就作罢：调用方会自行忽略输出 */ }
    }
}
