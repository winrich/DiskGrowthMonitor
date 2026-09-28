using System.Globalization;

namespace DiskGrowthMonitor.Util;

/// <summary>
/// 字节量/百分比/时间的统一格式化工具。
/// </summary>
public static class FormatUtil
{
    private static readonly string[] UnitNames = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

    /// <summary>将字节数格式化为人类可读字符串，例如 "12.34 GB"。</summary>
    public static string Bytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        double v = bytes;
        int unit = 0;
        while (Math.Abs(v) >= 1024.0 && unit < UnitNames.Length - 1)
        {
            v /= 1024.0;
            unit++;
        }
        // 字节级别不显示小数
        return unit == 0
            ? $"{v:0} {UnitNames[unit]}"
            : $"{v:0.00} {UnitNames[unit]}";
    }

    /// <summary>带符号字节量，例如 "+1.23 GB" / "-456.00 MB"。零值返回 "0 B"。</summary>
    public static string SignedBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        return (bytes > 0 ? "+" : "-") + Bytes(Math.Abs(bytes));
    }

    /// <summary>带符号百分比，例如 "+218.70%" / "-12.30%"。</summary>
    public static string SignedPercent(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent)) return "NEW";
        return (percent > 0 ? "+" : "") + percent.ToString("0.00", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>普通百分比（无符号），用于展示占比。</summary>
    public static string Percent(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent)) return "-";
        return percent.ToString("0.00", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>将毫秒格式化为 "3m12s" / "12.4s" 形式。</summary>
    public static string Duration(long milliseconds)
    {
        if (milliseconds < 1000) return $"{milliseconds}ms";
        var ts = TimeSpan.FromMilliseconds(milliseconds);
        if (ts.TotalMinutes < 1) return $"{ts.TotalSeconds:0.0}s";
        if (ts.TotalHours < 1) return $"{(int)ts.TotalMinutes}m{ts.Seconds}s";
        return $"{(int)ts.TotalHours}h{ts.Minutes}m{ts.Seconds}s";
    }

    /// <summary>本地时间字符串，同时用于数据库存储（ISO8601 可排序）与展示。</summary>
    public static string Timestamp(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>本地日期时间，用于生成文件名。</summary>
    public static string FileStamp(DateTime dt) => dt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

    /// <summary>千分位整数。</summary>
    public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// 以 MB 为单位格式化阈值（与命令行 --min-mb / --detail-min-mb 的输入单位一致，
    /// 避免用户输入 5000 却看到 "4.88 GB" 的对不上号）。
    /// </summary>
    public static string MbThreshold(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        return mb.ToString(mb == Math.Floor(mb) ? "0" : "0.##", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>HTML 文本转义，防止目录名中的特殊字符破坏报告结构。</summary>
    public static string Html(string? s) => string.IsNullOrEmpty(s)
        ? string.Empty
        : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
           .Replace("\"", "&quot;").Replace("'", "&#39;");

    /// <summary>JavaScript 字符串字面量转义（用于内嵌 JSON 数据）。</summary>
    public static string JsString(string? s) => string.IsNullOrEmpty(s)
        ? "\"\""
        : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    // ---------------------------------------------------------------- 控制台列对齐（第 57 轮新增）

    /// <summary>
    /// 按目标**显示宽度**右侧补空格；宽度已够则只追加一个空格作最小间隔，绝不截断内容。
    ///
    /// 🔴 不能直接用 <c>PadRight</c>：它数的是字符个数，而终端里一个汉字占**两列**。
    /// 中文档下「名称」（2 字 / 4 列）与「当前权限」（4 字 / 8 列）用同一个目标补齐后，
    /// 实际列宽会差 4 列 —— 标签列与表格都会错位（第 57 轮首跑实测踩到）。
    /// 英文档标签全是 ASCII，1 字符 = 1 列，所以两档各给一个目标宽度即可。
    /// </summary>
    public static string Cell(string text, int targetWidth)
    {
        int width = DisplayWidth(text);
        return width >= targetWidth ? text + " " : text + new string(' ', targetWidth - width);
    }

    /// <summary>
    /// 按目标**显示宽度**左侧补空格（数字右对齐）。宽度已够则只追加一个空格作最小间隔，
    /// 绝不截断内容。与 <see cref="Cell"/> 的区别仅在「补空格在左还是右」：
    /// 文本列（标签/名称/路径）用 Cell 左对齐，数值列用 CellRight 右对齐，
    /// 让同一列里的 "294.84 GB" 与 "850 MB" 小数点/数字右缘对齐。
    /// </summary>
    public static string CellRight(string text, int targetWidth)
    {
        int width = DisplayWidth(text);
        return width >= targetWidth ? text + " " : new string(' ', targetWidth - width) + text;
    }

    /// <summary>字符串在等宽终端里占的列数（宽字符按 2 列计）。</summary>
    public static int DisplayWidth(string s)
    {
        int w = 0;
        for (int i = 0; i < s.Length; i++) w += IsWide(s[i]) ? 2 : 1;
        return w;
    }

    /// <summary>是否为占两列的字符：CJK 汉字/假名/谚文、全角形式、CJK 标点与兼容区。</summary>
    public static bool IsWide(char c)
        => (c >= 0x1100 && c <= 0x115F)     // 谚文字母
        || (c >= 0x2E80 && c <= 0xA4CF)     // CJK 部首 … 彝文（含中日韩汉字、假名、注音）
        || (c >= 0xAC00 && c <= 0xD7A3)     // 谚文音节
        || (c >= 0xF900 && c <= 0xFAFF)     // CJK 兼容汉字
        || (c >= 0xFE30 && c <= 0xFE6F)     // CJK 兼容形式
        || (c >= 0xFF00 && c <= 0xFF60)     // 全角 ASCII 变体
        || (c >= 0xFFE0 && c <= 0xFFE6);    // 全角符号
}
