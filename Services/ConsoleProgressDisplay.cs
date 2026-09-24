using DiskGrowthMonitor.Compat;
using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// 多根并行扫描的控制台进度显示（原地重绘、不滚动刷屏）。
///
/// 布局：每个扫描根固定 2 行，末尾 1 行总计。分母有三种口径，按优先级自动选择：
///   1) 目录口径（有历史基准，或管理员下 MFT 精确估算）：
///        C: 盘      [████████████░░░░░░░░] 61%（已扫 123,456 / 约 200,000 个目录）
///   2) 容量口径（首次扫描且无管理员权限，仅适用于整卷根）：
///        C: 盘      [██████░░░░░░░░░░░░░░] ~48%（已扫 46.2 GB / 卷已用 97.4 GB）
///   3) 无分母（子目录根且无基准）：
///        C: 盘      [░░░░░░░░░░░░░░░░░░░░] --%（已扫 1,234 个目录 / 3.2 GB）
///   第二行恒为当前扫描路径（超出宽度时保留尾部），最后一行是总计。
///
/// 渲染要点（这两条是「不滚动刷屏」的关键，前一版在此踩坑）：
///   1) 每行按「显示列宽」截断（中文/全角按 2 列计），写完当前行再按控制台回报的
///      CursorLeft 精确清行尾——避免超宽自动换行把 3 个逻辑行变成 6 个物理行；
///   2) 全部行一律用 SetCursorPosition 绝对定位重绘，块内绝不使用 WriteLine——
///      在缓冲区末行 WriteLine 会触发滚动，使 CursorTop 相对定位逐帧下移（刷屏）。
///
/// 输出被重定向（如 >> 日志文件）时自动退化为静默模式，不使用任何光标控制。
/// </summary>
public sealed class ConsoleProgressDisplay
{
    /// <summary>单个扫描根的进度状态（供布局函数使用，无任何控制台 I/O）。</summary>
    public readonly record struct RootProgress(
        string Label,
        long Dirs,
        long Bytes,
        long EstimatedDirs,
        long VolumeUsedBytes,
        string Path);

    private sealed class Slot
    {
        public readonly string Label;
        /// <summary>目录口径分母；0 = 无（回退到容量口径）。</summary>
        public readonly long EstimatedDirs;
        /// <summary>容量口径分母（卷已用字节）；0 = 无。</summary>
        public readonly long VolumeUsedBytes;

        public long Dirs;
        public long Bytes;
        public string Path = Lang.T("等待中...", "waiting...");

        public Slot(string label, long estimatedDirs, long volumeUsedBytes)
        {
            Label = label;
            EstimatedDirs = Math.Max(0, estimatedDirs);
            VolumeUsedBytes = Math.Max(0, volumeUsedBytes);
        }
    }

    private const int BarWidth = 22;
    private const int LabelCols = 10;   // 根名统一占 10 显示列，让各盘进度条左端对齐
    private const int MinWidth = 24;

    private readonly List<Slot> _slots;
    private readonly Dictionary<string, Slot> _byKey;
    private readonly object _lock = new();

    private bool _interactive;
    private int _topRow = -1;           // 进度块首行的绝对行号（-1 = 尚未绘制）

    /// <param name="interactive">
    /// 是否允许绘制进度块。默认自动判定（输出未被重定向且至少一个根）。
    /// 提权日志中继模式下必须显式传 false：此时 Console.Out 已指向文件，
    /// 但 <see cref="Console.IsOutputRedirected"/> 仍按进程原始句柄报告「未重定向」，
    /// 若不关掉，光标控制字符会混进日志被父进程回显成乱码。
    /// </param>
    public ConsoleProgressDisplay(
        IReadOnlyList<(string Key, string Label, long EstimatedDirs, long VolumeUsedBytes)> roots,
        bool interactive = true)
    {
        _slots = roots.Select(r => new Slot(r.Label, r.EstimatedDirs, r.VolumeUsedBytes)).ToList();
        _byKey = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < roots.Count; i++) _byKey[roots[i].Key] = _slots[i];
        _interactive = interactive && !Console.IsOutputRedirected && _slots.Count > 0;
    }

    /// <summary>进度块占用的行数：每个根 2 行 + 总计 1 行。</summary>
    private int BlockHeight => _slots.Count * 2 + 1;

    /// <summary>写出初始进度块（此后每次更新都在原位重绘）。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (!_interactive) return;
            try
            {
                // 先把进度块要占的行腾空，再取块首行号。
                // Draw() 会把块首行 clamp 到 BufferHeight - BlockHeight；窗口不够高时（ConPTY 下
                // BufferHeight == WindowHeight，输出写满后每次 WriteLine 都会滚动、光标被钉在末行），
                // 这个 clamp 会把块整体上移，**覆盖掉紧邻块上方已经打印的行** —— 实测会吃掉
                // "[2/5] 开始扫描…" 这类标记行或刚打印的统计行/警告，且无声、换次运行就不复现。
                // 这里宁可先滚动一次屏（滚掉最上面那行，属终端正常行为），也不吃掉刚输出的内容。
                // 计数只在循环初值里算一次：Console.CursorTop 会随写入变化，放进条件里会重算。
                for (int i = Console.BufferHeight - Console.CursorTop; i < BlockHeight; i++)
                    Console.WriteLine();

                _topRow = Console.CursorTop;
                Draw(final: false);
            }
            catch
            {
                _interactive = false;   // 控制台 API 不可用：退化为静默，不影响扫描
                _topRow = -1;
            }
        }
    }

    /// <summary>扫描线程节流回调：汇报某根的绝对目录数、已扫字节与当前扫描路径。</summary>
    public void Update(string rootKey, long rootDirs, long rootBytes, string currentPath)
    {
        lock (_lock)
        {
            if (!_byKey.TryGetValue(rootKey, out var slot)) return;
            slot.Dirs = rootDirs;
            slot.Bytes = rootBytes;
            if (!string.IsNullOrEmpty(currentPath)) slot.Path = currentPath;
            if (!_interactive) return;
            try { Draw(final: false); }
            catch { _interactive = false; }
        }
    }

    /// <summary>扫描结束：渲染最终状态（百分比由 <see cref="Percent"/> 按 final 固定为 100%）。</summary>
    public void Finish()
    {
        lock (_lock)
        {
            if (!_interactive) return;
            try
            {
                // 这里**不要**把已扫数与分母取 Max 来「凑满」：百分比本来就由 Percent(final: true)
                // 固定为 100%，取 Max 只会让括号里的数字变成假的 —— 例如刚完成一轮整根复用时
                // 真实只枚举了 1 个目录，却显示「已扫 192,037 / 约 192,037」；字节口径则把
                // 286.69 GB 写成「已扫 293.68 GB / 卷已用 293.68 GB」，正好抹掉要解释的那部分差额。
                // 数字必须诚实：复用情况与「扫描数 vs 卷已用」的差额都要靠它暴露。
                Draw(final: true);
                MoveBelowBlock();
            }
            catch { _interactive = false; }
        }
    }

    // ------------------------------------------------------------------ 渲染

    /// <summary>
    /// 整块定位重绘。块首行固定为 _topRow（绝对行号），因此绝不会产生滚动：
    /// 缓冲末尾容不下整块时改为「贴着末行上方」绘制（行号下移 = 块整体上移）。
    /// </summary>
    private void Draw(bool final)
    {
        int windowWidth = Math.Max(MinWidth, Console.WindowWidth);
        int width = windowWidth - 1;                       // 留 1 列，避免触发末列自动换行

        int top = _topRow >= 0 ? _topRow : Console.CursorTop;
        int maxTop = Math.Max(0, Console.BufferHeight - BlockHeight);
        if (top > maxTop) top = maxTop;
        if (top < 0) top = 0;

        var lines = BuildLines(final, width);
        for (int i = 0; i < lines.Length; i++)
        {
            Console.SetCursorPosition(0, top + i);         // 绝对定位：不依赖上一帧的行数推算
            Console.Write(lines[i]);
            int clear = width - Console.CursorLeft;        // 按控制台实际列位清行尾，杜绝残留
            if (clear > 0) Console.Write(new string(' ', clear));
        }
        _topRow = top;
    }

    /// <summary>把光标移到进度块下方一行，供后续普通输出使用（块本身保持不动）。</summary>
    private void MoveBelowBlock()
    {
        int below = _topRow + BlockHeight;
        int last = Math.Max(0, Console.BufferHeight - 1);
        if (below <= last) { Console.SetCursorPosition(0, below); return; }
        Console.SetCursorPosition(0, last);
        Console.WriteLine();                               // 已在末行：滚动一行腾出新行
    }

    /// <summary>取当前各根状态，构造进度块文本（便于自测时也可直接调用）。</summary>
    private string[] BuildLines(bool final, int width)
        => BuildBlock(_slots
            .Select(s => new RootProgress(s.Label, s.Dirs, s.Bytes, s.EstimatedDirs, s.VolumeUsedBytes, s.Path))
            .ToList(), final, width);

    /// <summary>纯布局（无任何控制台 I/O）：每根 2 行 + 总计 1 行。</summary>
    public static string[] BuildBlock(IReadOnlyList<RootProgress> roots, bool final, int width)
    {
        var lines = new List<string>(roots.Count * 2 + 1);
        long sumDirs = 0, sumBytes = 0;
        bool allDirBased = roots.Count > 0;
        bool allVolumeBased = roots.Count > 0;

        foreach (var r in roots)
        {
            int pct = Percent(r.Dirs, r.Bytes, r.EstimatedDirs, r.VolumeUsedBytes, final);
            lines.Add(MakeHead(r.Label, r.Dirs, r.Bytes, r.EstimatedDirs, r.VolumeUsedBytes, pct, width));

            string pathText = "  " + Lang.T("└ ", "- ")
                              + (final ? Lang.T("已完成：", "done: ") : string.Empty) + r.Path;
            lines.Add(CutKeepTail(pathText, width));

            sumDirs += r.Dirs;
            sumBytes += r.Bytes;
            if (r.EstimatedDirs <= 0) allDirBased = false;
            if (r.EstimatedDirs > 0 || r.VolumeUsedBytes <= 0) allVolumeBased = false;
        }

        // 总计只在「所有根同口径」时给百分比：混合口径（部分有基准、部分没有）
        // 汇总出来的分母没有意义，宁可显示 --%，也不给出一个误导性的数字
        long totalEstDirs = 0, totalVolUsed = 0;
        if (allDirBased) totalEstDirs = roots.Sum(r => r.EstimatedDirs);
        else if (allVolumeBased) totalVolUsed = roots.Sum(r => r.VolumeUsedBytes);

        int totalPct = Percent(sumDirs, sumBytes, totalEstDirs, totalVolUsed, final);
        lines.Add(MakeHead(Lang.T("总计", "Total"), sumDirs, sumBytes, totalEstDirs, totalVolUsed, totalPct, width));

        return lines.ToArray();
    }

    /// <summary>进度条首行：宽度不足时逐级降级（完整 → 去掉分母 → 只留已扫数 → 仅进度条 → 硬截断）。</summary>
    private static string MakeHead(
        string label, long dirs, long bytes, long estDirs, long volUsed, int pct, int width)
    {
        string pctText = pct >= 0
            ? (IsVolumeBased(estDirs, volUsed) && pct < 100 ? $"~{pct}%" : $"{pct}%")
            : "--%";
        string head = "  " + PadCols(label, LabelCols) + "[" + MakeBar(pct) + "] " + pctText;

        foreach (var tail in BuildTails(dirs, bytes, estDirs, volUsed))
        {
            if (DisplayWidth(head + tail) <= width) return head + tail;
        }
        return DisplayWidth(head) <= width ? head : CutHead(head, width);
    }

    /// <summary>
    /// 括号说明的候选文本，按信息量递减排序列，供宽度不足时逐级降级。
    ///
    /// 两种语言各有一组（英文表述天然更长，逐级降级会退得更早一档），
    /// 但**排序语义相同**：先给带分母的完整信息，再给只有已扫数的简化版。
    /// </summary>
    private static string[] BuildTails(long dirs, long bytes, long estDirs, long volUsed)
    {
        if (estDirs > 0)
        {
            return new[]
            {
                Lang.F("（已扫 {0} / 约 {1} 个目录）", "(scanned {0} / ~{1} dirs)", Fmt(dirs), Fmt(estDirs)),
                Lang.F("（已扫 {0} 个目录）", "(scanned {0} dirs)", Fmt(dirs)),
                $"（{Fmt(dirs)}/{Fmt(estDirs)}）"
            };
        }

        if (volUsed > 0)
        {
            return new[]
            {
                Lang.F("（已扫 {0} / 卷已用 {1}）", "(scanned {0} / volume used {1})",
                       FormatUtil.Bytes(bytes), FormatUtil.Bytes(volUsed)),
                Lang.F("（已扫 {0}）", "(scanned {0})", FormatUtil.Bytes(bytes))
            };
        }

        return new[]
        {
            Lang.F("（已扫 {0} 个目录 / {1}）", "(scanned {0} dirs / {1})",
                   Fmt(dirs), FormatUtil.Bytes(bytes)),
            Lang.F("（已扫 {0} 个目录）", "(scanned {0} dirs)", Fmt(dirs))
        };
    }

    /// <summary>是否使用容量口径（百分比带 ~ 前缀，提示这是估算）。</summary>
    private static bool IsVolumeBased(long estDirs, long volUsed) => estDirs <= 0 && volUsed > 0;

    /// <summary>
    /// 单根/总计百分比：优先目录口径（已扫目录 ÷ 分母目录），其次容量口径
    /// （已扫字节 ÷ 卷已用字节）；都没有返回 -1（显示 --%）。封顶 99%，完成时 100%。
    /// </summary>
    private static int Percent(long dirs, long bytes, long estDirs, long volUsed, bool final)
    {
        if (final) return 100;
        if (estDirs > 0) return Math.Min(99, (int)(dirs * 100 / estDirs));
        if (volUsed > 0) return Math.Min(99, (int)(bytes * 100 / volUsed));
        return -1;
    }

    private static string MakeBar(int pct)
    {
        if (pct < 0) return new string('░', BarWidth);
        int fill = PlatformCompat.Clamp(pct * BarWidth / 100, 0, BarWidth);
        return new string('█', fill) + new string('░', BarWidth - fill);
    }

    private static string Fmt(long n) => n.ToString("N0");

    // ------------------------------------------------------ 显示列宽（东亚宽度）

    /// <summary>字符串在等宽终端中的显示列数：东亚全宽字符（中文、全角标点等）按 2 列计。</summary>
    public static int DisplayWidth(string s)
    {
        int w = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i])) { w += 2; i++; continue; }
            w += IsWide(s[i]) ? 2 : 1;
        }
        return w;
    }

    private static bool IsWide(char c)
    {
        // East Asian Wide / Fullwidth 主要区段（不包含制表/方块元素 ░█ 等，按 1 列处理）
        if (c >= 0x1100 && c <= 0x115F) return true;   // 谚文字母
        if (c == 0x2329 || c == 0x232A) return true;
        if (c >= 0x2E80 && c <= 0x303E) return true;   // CJK 部首、康熙部首、CJK 符号与标点
        if (c >= 0x3041 && c <= 0x33FF) return true;   // 假名、注音、CJK 兼容与符号
        if (c >= 0x3400 && c <= 0x4DBF) return true;   // CJK 扩展 A
        if (c >= 0x4E00 && c <= 0x9FFF) return true;   // CJK 基本区
        if (c >= 0xA000 && c <= 0xA4CF) return true;   // 彝文
        if (c >= 0xAC00 && c <= 0xD7A3) return true;   // 谚文音节
        if (c >= 0xF900 && c <= 0xFAFF) return true;   // CJK 兼容表意
        if (c >= 0xFE10 && c <= 0xFE19) return true;
        if (c >= 0xFE30 && c <= 0xFE6F) return true;
        if (c >= 0xFF00 && c <= 0xFF60) return true;   // 全角 ASCII（（）、％、：等）
        if (c >= 0xFFE0 && c <= 0xFFE6) return true;
        if (c >= 0x1F300 && c <= 0x1F64F) return true;
        if (c >= 0x20000 && c <= 0x3FFFD) return true;
        return false;
    }

    /// <summary>按显示列宽补齐/截断到指定列数（不足补空格）。</summary>
    private static string PadCols(string s, int cols)
    {
        int w = DisplayWidth(s);
        return w >= cols ? s : s + new string(' ', cols - w);
    }

    /// <summary>从右端截断（保留开头），不切断全角字符与代理对。</summary>
    private static string CutHead(string s, int maxCols)
    {
        int w = 0, i = 0;
        while (i < s.Length)
        {
            int cp = (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) ? 2 : 1;
            int cw = cp == 2 || IsWide(s[i]) ? 2 : 1;
            if (w + cw > maxCols) break;
            w += cw; i += cp;
        }
        return i >= s.Length ? s : s.Substring(0, i);
    }

    /// <summary>从左端截断（保留尾部并前置 ASCII 省略号），用于路径行——最深的当前目录才是关键信息。
    /// 省略号用 "..." 而非 U+2026：后者的东亚宽度含义在不同终端下不一致，可能少算 1 列。</summary>
    private static string CutKeepTail(string s, int maxCols)
    {
        if (DisplayWidth(s) <= maxCols) return s;
        const string ellipsis = "...";
        int keep = Math.Max(1, maxCols - ellipsis.Length);
        int w = 0, start = s.Length;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            int cw;
            if (char.IsLowSurrogate(s[i]) && i - 1 >= 0 && char.IsHighSurrogate(s[i - 1]))
            {
                cw = 2; i--;                        // 代理对（补充平面字符）按 2 列整体保留
            }
            else cw = IsWide(s[i]) ? 2 : 1;
            if (w + cw > keep) break;
            w += cw; start = i;
        }
        return ellipsis + s.Substring(start);
    }
}
