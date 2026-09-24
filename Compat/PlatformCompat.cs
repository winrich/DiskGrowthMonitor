using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskGrowthMonitor.Compat;

/// <summary>
/// 跨目标框架（net45 / net9.0）的零散兼容实现。
/// 只用于「两边行为必须一致、但差异小到不值得加 #if」的场景；
/// 性能敏感且差异明显的地方（如目录枚举）仍用 #if 各自保留最优实现。
/// </summary>
internal static class PlatformCompat
{
    private static readonly double MsPerTimestamp = 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// 单调递增的毫秒计数，语义等价 .NET Core 的 Environment.TickCount64。
    /// net45 没有 TickCount64，而 Environment.TickCount 是 int（约 24.9 天回绕），
    /// 用 Stopwatch 换算可同时保证单调性与不回绕。
    /// </summary>
    public static long TickCount64() => (long)(Stopwatch.GetTimestamp() * MsPerTimestamp);

    /// <summary>等价 Math.Clamp(int,int,int)（.NET Core 2.0+ 才有）。</summary>
    public static int Clamp(int value, int min, int max)
        => value < min ? min : (value > max ? max : value);

    /// <summary>
    /// 等价 Marshal.PtrToStructure&lt;T&gt;(IntPtr)。该泛型重载是 .NET Framework 4.5.1 才加入的，
    /// net45 上不存在，因此统一走非泛型重载（1.1 起就有），两个目标框架行为完全一致。
    /// 只在扫描启动时读一次 USN 日志信息，装箱开销可忽略。
    /// 末尾的 ! 用于压制 net9 侧「拆箱可能为 null」的 CS8605（net45 参考程序集无可空注解，同写法无警告）。
    /// </summary>
    public static T PtrToStructure<T>(IntPtr ptr) where T : struct
        => (T)Marshal.PtrToStructure(ptr, typeof(T))!;
}
