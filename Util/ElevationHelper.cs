using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace DiskGrowthMonitor.Util;

/// <summary>
/// 「以管理员身份自举」：本次要实际扫描、而当前进程又不是管理员时，
/// 用 UAC 重新以管理员身份启动自己。
///
/// 为什么需要：读 USN 变更日志（要打开 <c>\\.\X:</c> 卷设备）与 MFT 目录数估算都要求管理员权限。
/// 非管理员下不仅增量永远不可用（恒全量扫描），连进度百分比都只能退化为
/// 「已扫字节 / 卷已用空间」的近似值（带 ~ 前缀）。而这些都必须由进程自己具备权限，
/// 没有「中途提权」这种操作 —— Windows 的权限绑在进程令牌上。
///
/// 实现约束（.NET Framework 下没有第二条路）：
///   1. <c>Verb="runas"</c> 必须先置 <c>UseShellExecute=true</c>；
///   2. 用户取消 UAC 会抛 <see cref="Win32Exception"/>，其 <c>NativeErrorCode</c> 为 1223
///      （ERROR_CANCELLED），必须捕获并**降级继续**，绝不能因此终止进程；
///   3. 子进程必然已提权 ⇒ <see cref="IsElevated"/> 为真 ⇒ 天然不会二次自举；
///      仍额外透传 <c>--elevated</c> 标记作为硬保险（防御提权未生效的极端环境）；
///   4. 提权启动的是**新控制台窗口**，原终端的重定向流无法共享 ⇒ 输出被重定向时
///      由调用方跳过自举，否则脚本会既拿不到日志、又误以为程序静默失败。
/// </summary>
internal static class ElevationHelper
{
    /// <summary>ERROR_CANCELLED：用户在 UAC 对话框中选了「否」。</summary>
    private const int ERROR_CANCELLED = 1223;

    /// <summary>
    /// 当前进程是否以管理员身份运行。
    /// 用 <see cref="WindowsPrincipal.IsInRole(WindowsBuiltInRole)"/> 而非组查询：
    /// 在启用 UAC 的普通用户上下文中，进程持有的是**过滤令牌**，该调用会正确返回 false；
    /// 只有真正提权后（完整令牌）才返回 true，正是自举判定所需语义。
    /// </summary>
    public static bool IsElevated()
    {
        try
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // 极端受限环境下取不到身份信息：按「非管理员」处理，后续只影响是否尝试提权
            return false;
        }
    }

    /// <summary>定位自身可执行文件路径。MainModule 拿不到时退回入口程序集位置。</summary>
    private static string? ExecutablePath()
    {
        try
        {
            using (var p = Process.GetCurrentProcess())
            {
                string? file = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(file)) return file;
            }
        }
        catch
        {
            // 某些宿主下访问 MainModule 会抛异常，走下面的兜底
        }

        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            if (asm != null && !string.IsNullOrEmpty(asm.Location)) return asm.Location;
        }
        catch
        {
            // 忽略：两条路都失败时返回 null，由调用方降级
        }
        return null;
    }

    /// <summary>参数含空格时加双引号，避免重启后参数被拆散。</summary>
    private static string QuoteIfNeeded(string s)
        => s.IndexOf(' ') >= 0 && !(s.StartsWith("\"", StringComparison.Ordinal) && s.EndsWith("\"", StringComparison.Ordinal))
            ? "\"" + s + "\""
            : s;

    /// <summary>
    /// 尝试以管理员身份重新启动自己，并等待其结束。
    /// </summary>
    /// <param name="args">原始命令行参数（不含程序名），会原样透传。</param>
    /// <param name="exitCode">子进程退出码（仅在返回 true 时有效）。</param>
    /// <returns>
    /// true  = 子进程已启动并已结束，调用方应立刻返回 <paramref name="exitCode"/>（本进程不再做任何事）；
    /// false = 未启动（用户取消 UAC / 定位不到自身路径 / 启动异常），调用方应以当前权限继续执行。
    /// </returns>
    public static bool TryRelaunchElevated(string[] args, out int exitCode)
    {
        exitCode = 0;

        string? exe = ExecutablePath();
        if (string.IsNullOrEmpty(exe))
        {
            Console.WriteLine(Lang.T("[提权] 无法定位自身可执行文件路径，跳过自举（将以当前权限继续）。",
                                     "[Elevate] Cannot locate this executable; skipping self-elevation (continuing with current rights)."));
            return false;
        }

        var sb = new StringBuilder();
        foreach (var a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(QuoteIfNeeded(a));
        }
        if (sb.Length > 0) sb.Append(' ');
        sb.Append("--elevated");

        // 输出中继：子进程在新控制台运行，其输出无法重定向（UseShellExecute=true 的硬限制）。
        // 让子进程把控制台输出写进临时日志，本进程轮询该文件并逐行回显到当前窗口 ——
        // 这样用户在原窗口就能看到完整结果，而不是盯着一个一闪而过的新窗口。
        string? consoleLog = null;
        try
        {
            consoleLog = Path.Combine(Path.GetTempPath(),
                "DiskGrowthMonitor_elevated_" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".log");
            sb.Append(" --console-log \"").Append(consoleLog).Append('"');
        }
        catch
        {
            // 取不到临时目录（极罕见）：放弃中继，退化为「新窗口跑完即关」的旧行为
            consoleLog = null;
        }

        // 方案 B：让子进程尝试接管本窗口（attach 成功则完整进度条原样可见、输出直达）。
        // 与 --console-log 并存：attach 成功时日志只留一行握手标记，失败时它承载完整输出。
        sb.Append(" --attach-parent ").Append(Process.GetCurrentProcess().Id);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,   // Verb="runas" 的前提，缺一不可
            Verb = "runas",
            Arguments = sb.ToString(),
            WorkingDirectory = Environment.CurrentDirectory
        };

        Console.WriteLine(Lang.T(
            "[提权] 本次需要管理员权限：读 USN 变更日志与 MFT 目录数估算都要求管理员，",
            "[Elevate] This run needs administrator rights: reading the USN change journal and estimating the MFT"));
        Console.WriteLine(Lang.T(
            "       否则每轮都是全量扫描、进度百分比也只能是近似值。",
            "          directory count both require them; otherwise every run is a full scan with only approximate progress."));
        Console.WriteLine(consoleLog != null
            ? Lang.T("       正在请求以管理员身份重新启动…（输出与进度条将直接显示在本窗口；不想提权请加 --no-elevate）",
                     "          Requesting a restart as administrator... (output and the progress block will appear in this window; add --no-elevate to skip)")
            : Lang.T("       正在请求以管理员身份重新启动…（将打开新窗口；不想提权请加 --no-elevate）",
                     "          Requesting a restart as administrator... (a new window will open; add --no-elevate to skip)"));

        try
        {
            using (var child = Process.Start(psi))
            {
                if (child == null)
                {
                    Console.WriteLine(Lang.T("[提权] 启动失败，将以当前权限继续。",
                                             "[Elevate] Failed to start the elevated process; continuing with current rights."));
                    return false;
                }
                bool attachMode = false;
                if (consoleLog != null)
                    attachMode = RelayConsoleLog(consoleLog, child);
                else
                    child.WaitForExit();
                try { exitCode = child.ExitCode; }
                catch { exitCode = 0; }
                // 输出已全部回显，日志完成使命；退出码非 0 时保留以便排查
                if (consoleLog != null)
                {
                    if (exitCode == 0) { try { File.Delete(consoleLog); } catch { /* 删不掉就留着，无碍 */ } }
                    else
                    {
                        Console.WriteLine(Lang.F("[提权] 子进程退出码 {0}（非 0），输出日志已保留：{1}",
                                                 "[Elevate] Child process exited with code {0} (non-zero); its output log is kept at {1}",
                                                 exitCode, consoleLog));
                        // attach 模式下输出直达本窗口、日志本应只有握手行 ⇒ 日志里多出来的内容
                        // 必然是崩溃兜底（Program.ReportFatal）写的异常详情，打出来
                        if (attachMode) DumpAttachLog(consoleLog);
                    }
                }
                return true;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            Console.WriteLine(Lang.T("[提权] 已取消 UAC 授权，将以当前权限继续（本次为全量扫描，速度较慢）。",
                                     "[Elevate] UAC prompt was declined; continuing with current rights (this run is a full scan and will be slower)."));
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine(Lang.F("[提权] 无法以管理员身份重启：{0}",
                                     "[Elevate] Could not restart as administrator: {0}", ex.Message));
            Console.WriteLine(Lang.T("       将以当前权限继续。",
                                     "          Continuing with the current rights."));
            return false;
        }
    }

    // ---------------------------------------------------------- 输出中继

    /// <summary>轮询间隔（毫秒）。200ms 下回显延迟不致察觉，CPU 占用可忽略。</summary>
    private const int RelayPollMs = 200;

    /// <summary>无输出多久后打一行「仍在运行」（毫秒）。提权档进度条不可见，用它兜底体感。</summary>
    private const int RelayHeartbeatMs = 3000;

    /// <summary>
    /// 轮询子进程的日志并处理，直至子进程退出。首行握手标记决定本进程的角色：
    ///   · <c>[attach] ok</c>  —— 子进程已接管本控制台（方案 B），输出与进度条自己画。
    ///     本进程转入**完全静默等待**：不回显、不打心跳（打印会插进子进程的重绘块），退出后补一个换行。
    ///   · <c>[attach] fail</c> 或未出现 —— 子进程把输出写进了日志（方案 A），逐行回显 + 无输出时打心跳。
    ///
    /// 解码纪律：只解码「到最后一个换行为止」的字节、剩余字节留给下一轮 ——
    /// UTF-8 的续字节不可能出现 0x0A，因此按行边界切分永远不会把一个汉字劈成两半
    /// （若按「读多少解多少」来，跨读取块的汉字会被解成 U+FFFD 乱码）。
    /// 中继自身出错时静默退出：子进程照常运行，只是本窗口看不到后续输出，不值得冒险重试。
    /// </summary>
    /// <returns>true = 走的是 attach 模式（子进程自己画输出），调用方据此决定崩溃时是否 dump 日志。</returns>
    private static bool RelayConsoleLog(string logPath, Process child)
    {
        FileStream? fs = null;
        try
        {
            // 子进程创建日志有先后：轮询等待文件出现（上限 5 秒）。
            // 等不到 = 子进程打不开日志、已退回「写自己窗口」的旧行为 ⇒ 退化为只等待。
            for (int waited = 0; waited < 5000 && !child.HasExited; waited += RelayPollMs)
            {
                try
                {
                    // FileShare.ReadWrite：子进程以写句柄持有该文件，读它必须容忍共享写
                    fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    break;
                }
                catch { Thread.Sleep(RelayPollMs); }
            }

            if (fs == null)
            {
                // 等不到日志 = 子进程没在写日志（退回自带窗口）：无从判断握手，按中继模式收场
                child.WaitForExit();
                return false;
            }

            var pending = new List<byte>(4096);   // 尚未凑齐整行的字节
            var output = new StringBuilder(1024);
            int lastOutputTick = Environment.TickCount;
            bool? attachOk = null;                // null = 尚未读到握手标记

            while (!child.HasExited)
            {
                ReadCompleteLines(fs, pending, output);
                if (output.Length > 0)
                {
                    string text = output.ToString();
                    output.Clear();

                    if (attachOk == null)
                    {
                        if (text.StartsWith("[attach] ok", StringComparison.Ordinal))
                        {
                            // 子进程已接管本窗口并直接绘制输出/进度条：本进程必须彻底安静 ——
                            // 连心跳都不能打，任何打印都会插进子进程的原地重绘块里造成乱屏
                            attachOk = true;
                            continue;
                        }
                        if (text.StartsWith("[attach] fail", StringComparison.Ordinal))
                        {
                            attachOk = false;                 // 退回逐行中继；握手行本身不回显
                            int nl = text.IndexOf('\n');
                            text = nl >= 0 ? text.Substring(nl + 1) : string.Empty;
                        }
                    }

                    if (attachOk == true || text.Length == 0) continue;

                    Console.Write(text);
                    lastOutputTick = Environment.TickCount;
                }
                else if (attachOk != true && Environment.TickCount - lastOutputTick >= RelayHeartbeatMs)
                {
                    Console.WriteLine(Lang.T("      [提权] 子进程仍在运行（无输出超 3 秒，长任务属正常）…",
                                             "      [Elevate] Child process still running (no output for over 3 seconds; normal for long tasks)..."));
                    lastOutputTick = Environment.TickCount;   // 提示本身也算一次输出，避免刷屏
                }
                Thread.Sleep(RelayPollMs);
            }

            // 退出后再读一次尾巴：最后一批写入可能晚于 HasExited 的判定
            ReadCompleteLines(fs, pending, output);
            if (attachOk != true && output.Length > 0) Console.Write(output.ToString());
            // attach 模式下子进程的进度块画在共享控制台上，退出后补一个换行，
            // 让调用方后续的输出（如有）从新行开始，不至于叠在进度块尾行上
            if (attachOk == true) Console.WriteLine();
            return attachOk == true;
        }
        catch
        {
            // 中继失败：不影响子进程与退出码透传
            return false;
        }
        finally
        {
            try { fs?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// attach 模式（<c>[attach] ok</c>）下子进程非 0 退出时的诊断输出。
    /// 该模式的约定是「输出直达父窗口、日志只留一行握手标记」，因此日志里握手标记**之外**的
    /// 内容只可能来自子进程的崩溃兜底（Program.ReportFatal 追加写入），原样打出来即可。
    /// 若确实为空，说明子进程在产生任何输出之前就死了 —— 直接给结论，而不是让用户对着退出码猜。
    /// </summary>
    private static void DumpAttachLog(string logPath)
    {
        string body;
        try
        {
            body = File.ReadAllText(logPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine(Lang.F("[提权] （崩溃日志读取失败：{0}）",
                                     "[Elevate] (Could not read the crash log: {0})", ex.Message));
            return;
        }

        if (body.StartsWith("[attach]", StringComparison.Ordinal))
        {
            int nl = body.IndexOf('\n');
            body = nl >= 0 ? body.Substring(nl + 1) : string.Empty;
        }

        if (body.Trim().Length == 0)
        {
            Console.WriteLine(Lang.T("[提权] 子进程在接管本窗口后异常退出，且未写出任何内容。",
                                     "[Elevate] The child process died after taking over this window without writing anything."));
            Console.WriteLine(Lang.T("       说明崩溃发生在第一次输出之前（或控制台接管本身有问题）。",
                                     "          That means it crashed before its first output (or console takeover itself is broken)."));
            return;
        }

        Console.WriteLine(Lang.T("[提权] 子进程崩溃详情：", "[Elevate] Child process crash details:"));
        Console.Write(body);
        if (!body.EndsWith("\n", StringComparison.Ordinal)) Console.WriteLine();
    }

    /// <summary>把日志里新增的、以换行结尾的内容解码并追加到 <paramref name="output"/>。</summary>
    private static void ReadCompleteLines(FileStream fs, List<byte> pending, StringBuilder output)
    {
        // 1) 读新增字节（文件按追加写，当前位置即上次读到的位置）
        var buf = new byte[8192];
        int read;
        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i++) pending.Add(buf[i]);

        // 2) 只解码到最后一个换行为止（含），剩余字节是半行/半个汉字，留给下一轮
        int lastNl = -1;
        for (int i = pending.Count - 1; i >= 0; i--)
            if (pending[i] == 0x0A) { lastNl = i; break; }
        if (lastNl < 0) return;

        var complete = new byte[lastNl + 1];
        pending.CopyTo(0, complete, 0, lastNl + 1);
        pending.RemoveRange(0, lastNl + 1);
        output.Append(Encoding.UTF8.GetString(complete));
    }
}
