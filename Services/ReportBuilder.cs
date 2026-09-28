using System.Text;
using DiskGrowthMonitor.Models;
using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// 生成自包含（无外部依赖、可离线打开）的 HTML 报告：
/// 概览区（KPI、各扫描根对比、增长/缩减榜、新增消失目录）+ 详细区（可搜索/排序/筛选的完整明细表）+ 跳过清单与说明。
/// </summary>
public static class ReportBuilder
{
    /// <summary>
    /// 明细表状态筛选按钮的顺序与**稳定键**。
    ///
    /// 🔴 键必须是**与语言无关的 ASCII 串**：它同时落在按钮的 <c>data-status</c> 与每行 <c>tr</c> 的
    /// <c>data-status</c> 上，页面里的 Javascript 靠「两者相等」做筛选。旧实现直接把中文显示文本当键
    /// （<c>{ "增长", "缩减", … }</c>），一旦输出语言切换、两边取值来源稍有不同就会**静默失配**：
    /// 按钮点了没反应，却看不出哪里错。现在键固定为 ASCII，显示文本另经 <see cref="StatusLabel"/> 取词，
    /// 语言切换只影响文字、不影响筛选。
    /// </summary>
    private static readonly string[] StatusKeys = { "grown", "shrunk", "new", "removed", "unchanged" };

    /// <summary>状态稳定键 → 当前语言的显示文本（明细表筛选按钮与徽标共用）。</summary>
    private static string StatusLabel(string key) => key switch
    {
        "grown" => Lang.T("增长", "Grown"),
        "shrunk" => Lang.T("缩减", "Shrunk"),
        "new" => Lang.T("新增", "New"),
        "removed" => Lang.T("消失", "Removed"),
        _ => Lang.T("无变化", "Unchanged")
    };

    /// <summary><see cref="GrowthStatus"/> → 稳定键（与 <see cref="StatusLabel"/> 同一套键）。</summary>
    private static string StatusKey(GrowthStatus s) => s switch
    {
        GrowthStatus.Grown => "grown",
        GrowthStatus.Shrunk => "shrunk",
        GrowthStatus.New => "new",
        GrowthStatus.Removed => "removed",
        GrowthStatus.Skipped => "skipped",
        _ => "unchanged"
    };

    /// <summary>路径列表分隔符：中文用顿号、英文用逗号（与 <c>string.Join</c> 搭配）。</summary>
    private static string ListSep => Lang.T("、", ", ");

    /// <summary>
    /// 状态列的**排序位**（明细表按「状态」列排序时用，非数值）：
    /// 稳定键改 ASCII 后，直接按 <c>data-status</c> 字符串比较会退化成字母序
    /// （grown / new / removed / shrunk / unchanged），与筛选按钮的排列顺序对不上。
    /// 这里给出与 <see cref="StatusKeys"/> 一致的数值顺序，由 <c>data-statusrank</c> 承载。
    /// </summary>
    private static int StatusRank(GrowthStatus s) => s switch
    {
        GrowthStatus.Grown => 0,
        GrowthStatus.Shrunk => 1,
        GrowthStatus.New => 2,
        GrowthStatus.Removed => 3,
        GrowthStatus.Skipped => 4,
        _ => 5
    };

    public static string Build(ReportModel m)
    {
        var sb = new StringBuilder(256 * 1024);
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(Lang.IsEnglish ? "en" : "zh-CN").Append("\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(Lang.T("磁盘空间增长报告 - ", "Disk space growth report - "));
        sb.Append(FormatUtil.Html(FormatUtil.Timestamp(m.ScanTime)));
        sb.Append("</title>\n");
        sb.Append("<style>\n").Append(Css).Append("\n</style>\n");
        sb.Append("</head>\n<body>\n");

        AppendHeader(sb, m);
        AppendOverview(sb, m);
        AppendRootTable(sb, m);
        AppendVolumeGapSection(sb, m);

        if (m.IsFirstRun)
        {
            // 首次运行没有对比基准，任何「增长」都是伪命题。
            // 此时改为展示「占用最大的目录」，这才是用户首次使用时真正需要的信息。
            AppendLargest(sb, m);
        }
        else
        {
            // 阈值过滤后为空时，分析器已自动降级为「未应用阈值」的 Top N，此处据实标注
            string? growthNotice = m.Analysis.GrowthFallbackUsed
                ? Lang.T("本轮没有任何目录的增长量达到 ", "No directory grew by as much as ")
                  + FormatUtil.Html(FormatUtil.MbThreshold(m.MinBytes))
                  + (m.MinPercent > 0
                      ? Lang.T(" 且增长百分比达到 ", " with a growth percentage of at least ")
                        + FormatUtil.Html(FormatUtil.Percent(m.MinPercent))
                      : string.Empty)
                  + Lang.T("，以下为<strong>未应用阈值</strong>时变化量最大的目录。",
                           "; the table below shows the largest changes with <strong>no threshold applied</strong>.")
                : null;

            AppendRankSection(sb, m, "growth",
                Lang.T("增长最快的目录", "Fastest growing directories"),
                Lang.T("按目录自身子树占用变化量降序排列", "Sorted by the size change of each directory's own subtree, descending"),
                m.Analysis.GrowthTop, "up", growthNotice);

            AppendRankSection(sb, m, "shrink",
                Lang.T("缩减最多的目录", "Most reduced directories"),
                Lang.T("按目录自身子树占用变化量升序排列", "Sorted by the size change of each directory's own subtree, ascending"),
                m.Analysis.ShrinkTop, "down", null,
                m.Analysis.HasAnyShrink ? null : Lang.T("本轮没有任何目录体积减小。", "No directory shrank this run."));

            AppendNewRemoved(sb, m);
        }

        AppendDetail(sb, m);
        AppendSkipSection(sb, m);
        AppendFooter(sb, m);

        sb.Append("\n<script>\n").Append(BuildJs()).Append("\n</script>\n");
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 页头

    private static void AppendHeader(StringBuilder sb, ReportModel m)
    {
        sb.Append("<header class=\"page-head\">\n");
        sb.Append("  <div class=\"ph-main\">\n");
        sb.Append("    <h1>").Append(Lang.T("磁盘空间增长报告", "Disk space growth report")).Append("</h1>\n");
        sb.Append("    <div class=\"ph-sub\">").Append(Lang.T("扫描时间 ", "Scan time "))
          .Append(FormatUtil.Html(FormatUtil.Timestamp(m.ScanTime)))
          .Append(Lang.T("　·　批次 #", "　·　run #")).Append(m.RunId)
          .Append(Lang.T("　·　扫描根：", "　·　scan roots: ")).Append(FormatUtil.Html(string.Join(ListSep, m.Roots.Select(r => r.DisplayName))))
          .Append("</div>\n");
        sb.Append("  </div>\n");
        sb.Append("  <div class=\"ph-badge ").Append(m.IsFirstRun ? "badge-new" : "badge-cmp").Append("\">")
          .Append(m.IsFirstRun ? Lang.T("首次运行", "First run") : Lang.T("与上一轮对比", "Compared with previous run"))
          .Append("</div>\n");
        sb.Append("</header>\n");

        if (m.IsFirstRun)
        {
            sb.Append("<div class=\"notice notice-info\">\n");
            sb.Append(Lang.T("  <strong>这是首次运行。</strong>数据库中尚无上一轮快照，因此本轮所有目录均记录为「新增」，"
                           + "增长对比从下一次运行开始生效。本次结果已作为后续对比的基准。\n",
                             "  <strong>This is the first run.</strong> The database holds no previous snapshot yet, so every "
                           + "directory is recorded as \"new\" and growth comparison starts with the next run. This run is now "
                           + "the baseline for future comparisons.\n"));
            sb.Append("</div>\n");
        }
        else if (m.Analysis.HasRootsWithoutBaseline)
        {
            // 混合场景：本轮扩回了此前没扫的根（如 -d c,d → -d c → -d c,d）。
            // 新根没有自己的基准，其全部目录记为「新增」；页头必须说明，否则增长榜会被误读。
            var fresh = string.Join(ListSep, m.Analysis.Roots
                .Where(r => r.PrevBytes == null)
                .Select(r => FormatUtil.Html(r.DisplayName)));
            sb.Append("<div class=\"notice notice-info\">\n");
            sb.Append("  <strong>").Append(Lang.T("以下扫描根为首次记录：", "The following scan roots are recorded for the first time: "))
              .Append(fresh).Append("。</strong>\n");
            sb.Append(Lang.T("  它们在库中没有自己的历史基准，本轮其全部目录均记为「新增」，不计入「相比上次变化」；"
                           + "本次结果已作为这些根后续对比的基准。\n",
                             "  They have no baseline of their own in the database, so all of their directories are "
                           + "recorded as \"new\" this run and are excluded from the overall change figure; this run "
                           + "becomes their baseline for future comparisons.\n"));
            sb.Append("</div>\n");
        }
    }

    // ---------------------------------------------------------------- 概览区

    private static void AppendOverview(StringBuilder sb, ReportModel m)
    {
        var a = m.Analysis;
        var s = m.Statistics;

        sb.Append("<section>\n<h2>").Append(Lang.T("概览", "Overview")).Append("</h2>\n");

        // ---- KPI 卡片 ----
        sb.Append("<div class=\"kpis\">\n");
        Kpi(sb, Lang.T("本次扫描总占用", "Total scanned size"), FormatUtil.Bytes(a.TotalBytes), "sub",
            Lang.T("所有扫描根子树累计", "sum of every scan root subtree"));
        if (m.IsFirstRun)
        {
            Kpi(sb, Lang.T("相比上次变化", "Change vs. previous run"), "—", "flat",
                Lang.T("无历史基准", "no historical baseline"));
        }
        else if (a.HasRootsWithoutBaseline)
        {
            // 混合场景：只有带基准的根参与「相比上次」计算，首次记录的根若计入会被虚增成增长
            long net = a.Roots.Where(r => r.PrevBytes != null)
                .Sum(r => (r.CurrBytes ?? 0) - (r.PrevBytes ?? 0));
            string cls = net > 0 ? "up" : net < 0 ? "down" : "flat";
            Kpi(sb, Lang.T("相比上次变化", "Change vs. previous run"), FormatUtil.SignedBytes(net), cls,
                Lang.F("另有 {0} 个扫描根首次记录，未计入对比",
                       "{0} root(s) recorded for the first time, excluded from this figure",
                       a.Roots.Count(r => r.PrevBytes == null)));
        }
        else
        {
            long net = a.TotalBytes - (a.PrevTotalBytes ?? 0);
            string cls = net > 0 ? "up" : net < 0 ? "down" : "flat";
            string pct = a.PrevTotalBytes is > 0
                ? FormatUtil.SignedPercent(net * 100.0 / a.PrevTotalBytes.Value)
                : "—";
            Kpi(sb, Lang.T("相比上次变化", "Change vs. previous run"), FormatUtil.SignedBytes(net), cls,
                Lang.F("上次 {0}（{1}）", "previous {0} ({1})", FormatUtil.Bytes(a.PrevTotalBytes ?? 0), pct));
        }
        Kpi(sb, Lang.T("目录总数", "Total directories"),
            FormatUtil.Count(a.Roots.Sum(r => r.CurrDirs) + a.Roots.Count), "sub",
            Lang.F("其中本轮新增 {0}", "{0} added this run", FormatUtil.Count(a.Summary.NewCount)));
        Kpi(sb, Lang.T("文件总数", "Total files"), FormatUtil.Count(a.Roots.Sum(r => r.CurrFiles)), "sub",
            s.DedupedFileCount > 0
                ? Lang.F("已去重硬链接 {0} 个", "{0} hardlinked files deduplicated", FormatUtil.Count(s.DedupedFileCount))
                : Lang.T("含全部文件", "all files included"));
        Kpi(sb, Lang.T("扫描耗时", "Scan duration"), FormatUtil.Duration(s.ElapsedMs), "sub",
            Lang.F("开始于 {0}", "started at {0}", FormatUtil.Timestamp(m.StartedAt)));
        Kpi(sb, Lang.T("跳过项", "Skipped items"),
            FormatUtil.Count(s.ExcludedCount + s.AccessDeniedCount + s.ReparseCount + s.ErrorCount),
            s.AccessDeniedCount + s.ErrorCount > 0 ? "warn" : "sub",
            Lang.F("排除 {0} · 权限 {1} · 链接 {2} · 异常 {3}",
                   "excluded {0} · denied {1} · links {2} · errors {3}",
                   s.ExcludedCount, s.AccessDeniedCount, s.ReparseCount, s.ErrorCount));
        sb.Append("</div>\n");

        // ---- 变化规模小结 ----
        sb.Append("<div class=\"grid-2\">\n");

        sb.Append("<div class=\"panel\">\n<h3>").Append(Lang.T("目录变化规模", "Scale of directory changes")).Append("</h3>\n");
        sb.Append("<table class=\"mini\">\n<tbody>\n");
        MiniRow(sb, Lang.T("体积增大的目录", "Directories that grew"), a.Summary.GrownCount, "up");
        MiniRow(sb, Lang.T("体积减小的目录", "Directories that shrank"), a.Summary.ShrunkCount, "down");
        MiniRow(sb, Lang.T("本轮新增目录", "Directories added"), a.Summary.NewCount, "new");
        MiniRow(sb, Lang.T("本轮消失目录", "Directories removed"), a.Summary.RemovedCount, "removed");
        if (a.Summary.SkippedCount > 0)
            MiniRow(sb, Lang.T("因跳过而不可比", "Not comparable (skipped)"), a.Summary.SkippedCount, "muted",
                Lang.F("{0}（排除/权限原因，未计入上表）",
                       "{0} (excluded or denied, not counted above)",
                       FormatUtil.Bytes(a.Summary.SkippedBytes)));
        sb.Append("</tbody>\n</table>\n");
        sb.Append("<p class=\"hint\">")
          .Append(Lang.T("计数为目录级统计：父目录的变化天然包含其子目录的变化，因此各类目录数量不可直接相加理解。",
                         "Counts are per directory: a parent's change already contains its children's changes, so these "
                       + "numbers must not be added together."))
          .Append("</p>\n");
        sb.Append("</div>\n");

        sb.Append("<div class=\"panel\">\n<h3>").Append(Lang.T("本次扫描配置", "Scan settings for this run")).Append("</h3>\n");
        sb.Append("<table class=\"mini\">\n<tbody>\n");
        sb.Append($"<tr><th>{Lang.T("扫描根", "Scan roots")}</th><td>{FormatUtil.Html(string.Join(ListSep, m.Roots.Select(r => r.DisplayName)))}</td></tr>\n");
        sb.Append($"<tr><th>{Lang.T("默认排除", "Default exclusions")}</th><td>{(m.UseDefaultExclude ? Lang.T("已启用（Windows / $Recycle.Bin / System Volume Information）", "enabled (Windows / $Recycle.Bin / System Volume Information)") : Lang.T("已禁用", "disabled"))}</td></tr>\n");
        sb.Append($"<tr><th>{Lang.T("硬链接去重", "Hardlink dedup")}</th><td>{(m.DedupEnabled ? Lang.T("已启用", "enabled") : Lang.T("已禁用", "disabled"))}</td></tr>\n");
        if (!string.IsNullOrEmpty(m.ScanModeNote))
            sb.Append($"<tr><th>{Lang.T("扫描模式", "Scan mode")}</th><td>{FormatUtil.Html(m.ScanModeNote)}</td></tr>\n");
        sb.Append($"<tr><th>{Lang.T("跟随符号链接", "Follow symlinks")}</th><td>{(m.FollowReparse ? Lang.T("是", "yes") : Lang.T("否", "no"))}</td></tr>\n");
        sb.Append($"<tr><th>{Lang.T("增长榜阈值", "Growth threshold")}</th><td>≥ {FormatUtil.Html(FormatUtil.MbThreshold(m.MinBytes))}")
          .Append(m.MinPercent > 0 ? $" {Lang.T("且", "and")} ≥ {FormatUtil.Html(FormatUtil.Percent(m.MinPercent))}" : string.Empty)
          .Append($"{Lang.F("，取前 {0} 条", ", top {0} rows", m.Top)}</td></tr>\n");
        sb.Append($"<tr><th>{Lang.T("明细表阈值", "Detail threshold")}</th><td>{Lang.F("变化量 ≥ {0}，最多 {1} 行", "change ≥ {0}, up to {1} rows", FormatUtil.Html(FormatUtil.MbThreshold(m.DetailMinBytes)), FormatUtil.Count(m.DetailTop))}</td></tr>\n");
        sb.Append($"<tr><th>{Lang.T("数据库", "Database")}</th><td class=\"mono wrap\">{FormatUtil.Html(m.DatabasePath)}</td></tr>\n");
        sb.Append("</tbody>\n</table>\n");
        if (s.DedupedBytes > 0)
        {
            sb.Append("<p class=\"hint\">")
              .Append(Lang.F("硬链接去重共避免重复计入 {0} 个文件、{1}，防止同一物理数据被多个路径重复统计而产生虚假增长。",
                             "Hardlink dedup avoided counting {0} files and {1} twice, so the same physical data behind several "
                           + "paths cannot show up as fake growth.",
                             FormatUtil.Count(s.DedupedFileCount), FormatUtil.Bytes(s.DedupedBytes)))
              .Append("</p>\n");
        }
        sb.Append("</div>\n");
        sb.Append("</div>\n");

        AppendCleanable(sb, m);

        if (m.UseDefaultExclude)
        {
            sb.Append("<div class=\"notice notice-warn\">\n");
            sb.Append(Lang.T("  <strong>注意：</strong>已排除默认目录（如 <span class=\"mono\">C:\\Windows</span>），",
                             "  <strong>Note:</strong> the default directories (such as <span class=\"mono\">C:\\Windows</span>) are excluded,"));
            sb.Append(Lang.T("被排除目录及其子目录的占用<strong>不计入</strong>本次统计，因此各根的总占用会小于系统实际使用量。",
                             " so the excluded directories and their children are <strong>not counted</strong> in this run. Each root's total "
                           + "is therefore smaller than the space the system actually uses."));
            sb.Append(Lang.T("如需完整统计，请使用 <span class=\"mono\">--no-default-exclude</span> 重新扫描。详见文末「跳过清单」。\n",
                             " For a complete count, rescan with <span class=\"mono\">--no-default-exclude</span>. See the "
                           + "\"Skip list\" at the end of this report.\n"));
            sb.Append("</div>\n");
        }

        sb.Append("</section>\n");
    }

    private static void Kpi(StringBuilder sb, string label, string value, string cls, string sub)
    {
        sb.Append("<div class=\"kpi\">\n");
        sb.Append("  <div class=\"kpi-label\">").Append(FormatUtil.Html(label)).Append("</div>\n");
        sb.Append("  <div class=\"kpi-value kpi-").Append(cls).Append("\">").Append(FormatUtil.Html(value)).Append("</div>\n");
        sb.Append("  <div class=\"kpi-sub\">").Append(FormatUtil.Html(sub)).Append("</div>\n");
        sb.Append("</div>\n");
    }

    private static void MiniRow(StringBuilder sb, string label, long value, string cls, string? extra = null)
    {
        sb.Append("<tr><th>").Append(FormatUtil.Html(label)).Append("</th>");
        sb.Append("<td class=\"num v-").Append(cls).Append("\">").Append(FormatUtil.Count(value)).Append("</td>");
        sb.Append("<td class=\"sub\">").Append(FormatUtil.Html(extra ?? string.Empty)).Append("</td></tr>\n");
    }

    // ------------------------------------------------------ 可安全清理的目录

    /// <summary>
    /// 「可安全清理的目录」清单：把命中知识库、可清理性为「安全」的目录汇总成一张表并给出合计，
    /// 让读者不必逐行翻明细表去拼凑可回收空间。
    ///
    /// 两处必须写明的口径（否则容易被误读成「这台机器没东西可清」）：
    /// ① 占用是目录子树的累计值，清单只收最外层目录 ⇒ 合计不会重复计算；
    /// ② 清单只覆盖<b>本轮实际扫描到</b>的目录 —— 关闭知识库豁免或非管理员运行时，
    ///    位于「排除目录 / 权限不足」下的可清理目录不会出现在快照里，合计会偏小。
    /// </summary>
    private static void AppendCleanable(StringBuilder sb, ReportModel m)
    {
        var items = m.Cleanable;

        sb.Append("<div class=\"panel panel-clean\">\n");
        sb.Append("<h3>").Append(Lang.T("可安全清理的目录", "Directories safe to clean"));
        if (items.Count > 0)
            sb.Append("<span class=\"skip-count\">").Append(Lang.T("　共 ", " — "))
              .Append(Lang.F("{0} 个，合计 {1}", "{0} entries, {1} in total",
                             FormatUtil.Count(items.Count), FormatUtil.Bytes(m.CleanableTotalBytes)))
              .Append("</span>");
        sb.Append("</h3>\n");

        if (items.Count == 0)
        {
            sb.Append("<div class=\"empty\">")
              .Append(Lang.T("本轮扫描到的目录中，没有命中知识库「可安全清理」标记的目录。",
                             "None of the directories scanned this run matched a knowledge-base entry marked \"safe to clean\"."))
              .Append(Lang.T("若本次未以管理员身份运行、或使用了 --no-knowledge-rescan，",
                             "If this run was not elevated, or --no-knowledge-rescan was used, "))
              .Append(Lang.T("则被排除目录下的可清理目录不在扫描范围内。",
                             "then cleanable directories under excluded paths were outside the scan scope."))
              .Append("</div>\n");
            sb.Append("</div>\n");
            return;
        }

        sb.Append("<div class=\"table-wrap\">\n<table class=\"data compact\">\n<thead>\n<tr>");
        sb.Append("<th class=\"rank\">#</th><th>").Append(Lang.T("路径", "Path")).Append("</th><th>")
          .Append(Lang.T("用途", "Purpose")).Append("</th><th class=\"num\">").Append(Lang.T("占用", "Size")).Append("</th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        int rank = 1;
        foreach (var it in items)
        {
            var e = it.Entry;
            sb.Append("<tr>\n");
            sb.Append("  <td class=\"rank\">").Append(rank++).Append("</td>\n");
            sb.Append("  <td class=\"mono wrap path\">").Append(FormatUtil.Html(it.Path)).Append("</td>\n");
            sb.Append("  <td>");
            sb.Append("<span class=\"use-title\">").Append(FormatUtil.Html(e.TitleText)).Append("</span>");
            sb.Append("<span class=\"tag tag-safe\">").Append(FormatUtil.Html(e.CleanableText)).Append("</span>");
            if (!string.IsNullOrEmpty(e.NoteText))
                sb.Append("<span class=\"dir-note\">").Append(FormatUtil.Html(e.NoteText)).Append("</span>");
            sb.Append("</td>\n");
            sb.Append("  <td class=\"num strong\">").Append(FormatUtil.Bytes(it.Bytes)).Append("</td>\n");
            sb.Append("</tr>\n");
        }

        sb.Append("<tr class=\"clean-total\"><th colspan=\"3\">")
          .Append(Lang.T("可安全清理空间合计", "Total space safe to clean"))
          .Append("</th>")
          .Append("<td class=\"num\">").Append(FormatUtil.Bytes(m.CleanableTotalBytes)).Append("</td></tr>\n");
        sb.Append("</tbody>\n</table>\n</div>\n");

        sb.Append("<p class=\"hint\">")
          .Append(Lang.T("占用为该目录子树的累计值（已扣除硬链接重复、已剔除被排除的子目录）；",
                         "Size is the accumulated size of that directory's subtree (hardlink duplicates removed, excluded "
                       + "children taken out); "))
          .Append(Lang.T("清单只收录最外层目录，因此合计不会重复计算。清单仅覆盖<strong>本轮实际扫描到</strong>的目录：",
                         "only the outermost directories are listed, so the total is not double counted. The list only covers "
                       + "directories that were <strong>actually scanned this run</strong>: "))
          .Append(Lang.T("未以管理员身份运行、或使用了 <span class=\"mono\">--no-knowledge-rescan</span> 时，",
                         "when the process is not elevated, or <span class=\"mono\">--no-knowledge-rescan</span> is used, "))
          .Append(Lang.T("被排除目录下的可清理目录不在扫描范围内，合计会偏小。",
                         "cleanable directories under excluded paths are outside the scan scope and the total will be too low. "))
          .Append(Lang.T("标记为「谨慎清理」的目录（如 <span class=\"mono\">C:\\Windows\\SoftwareDistribution</span>）",
                         "Directories marked \"clean with care\" (such as <span class=\"mono\">C:\\Windows\\SoftwareDistribution</span>) "))
          .Append(Lang.T("未计入本合计。</p>\n", "are not included in this total.</p>\n"));

        sb.Append("</div>\n");
    }

    // ------------------------------------------------------------ 各根对比表

    private static void AppendRootTable(StringBuilder sb, ReportModel m)
    {
        sb.Append("<section>\n<h2>").Append(Lang.T("各扫描根对比", "Scan root comparison")).Append("</h2>\n");
        sb.Append("<div class=\"table-wrap\">\n<table class=\"data\">\n<thead>\n<tr>");
        sb.Append("<th>").Append(Lang.T("扫描根", "Scan root")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("本次占用", "Current size")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("上次占用", "Previous size")).Append("</th>");
        sb.Append("<th class=\"num\">").Append(Lang.T("变化量", "Change")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("变化率", "Change %")).Append("</th>");
        sb.Append("<th class=\"num\">").Append(Lang.T("目录数", "Directories")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("文件数", "Files")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("磁盘容量", "Capacity")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("剩余空间", "Free space")).Append("</th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var r in m.Analysis.Roots)
        {
            // 按根判定「首次记录」：该根在库里没有自己的基准（与整体是否首次运行无关）
            bool noBase = r.PrevBytes == null;
            string cls = noBase ? "flat" : r.GrowthBytes > 0 ? "up" : r.GrowthBytes < 0 ? "down" : "flat";
            sb.Append("<tr>\n");
            sb.Append("  <td class=\"root-name\">").Append(FormatUtil.Html(r.DisplayName));
            if (noBase)
            {
                sb.Append("<div class=\"sub\">").Append(Lang.T("首次记录（本轮建立基准）", "First record (baseline established this run)")).Append("</div>");
            }
            else if (r.BaselineRunId.HasValue && r.BaselineRunId.Value != m.PreviousRunId)
            {
                // 基准不是紧邻的上一轮（多盘轮换后扩回该根）：如实标注基准来自哪一轮
                sb.Append("<div class=\"sub\">").Append(Lang.F(
                    "与该根上次扫描对比（第 {0} 轮，{1}）",
                    "Compared with this root's own last scan (run #{0}, {1})",
                    r.BaselineRunId.Value,
                    FormatUtil.Timestamp(r.BaselineTime ?? DateTime.MinValue))).Append("</div>");
            }
            sb.Append("</td>\n");
            sb.Append("  <td class=\"num\">").Append(FormatUtil.Bytes(r.CurrBytes ?? 0)).Append("</td>\n");
            sb.Append("  <td class=\"num sub\">")
              .Append(noBase ? "—" : FormatUtil.Bytes(r.PrevBytes ?? 0)).Append("</td>\n");
            sb.Append("  <td class=\"num v-").Append(cls).Append("\">")
              .Append(noBase ? "—" : FormatUtil.SignedBytes(r.GrowthBytes)).Append("</td>\n");
            sb.Append("  <td class=\"num v-").Append(cls).Append("\">")
              .Append(noBase ? "—" : FormatUtil.SignedPercent(r.GrowthPercent)).Append("</td>\n");
            sb.Append("  <td class=\"num sub\">").Append(FormatUtil.Count(r.CurrDirs)).Append("</td>\n");
            sb.Append("  <td class=\"num sub\">").Append(FormatUtil.Count(r.CurrFiles)).Append("</td>\n");
            sb.Append("  <td class=\"num sub\">")
              .Append(r.CapacityBytes > 0 ? FormatUtil.Bytes(r.CapacityBytes) : "—").Append("</td>\n");
            sb.Append("  <td class=\"num sub\">")
              .Append(r.FreeBytes >= 0 ? FormatUtil.Bytes(r.FreeBytes) : "—").Append("</td>\n");
            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
        sb.Append("<p class=\"hint\">")
          .Append(Lang.T("「本次占用」为目录树逐层累计的真实占用（已扣除硬链接重复、已剔除被排除目录）。",
                         "\"Current size\" is the real usage accumulated level by level through the directory tree (hardlink "
                       + "duplicates removed, excluded directories taken out)."))
          .Append("</p>\n");
        sb.Append("</section>\n");
    }

    // ------------------------------------------------------ 容量口径对照

    /// <summary>
    /// 「本次统计 vs 卷已用」对照表：回答「为什么这里统计的占用小于资源管理器显示的已用空间」。
    /// 只列出整卷扫描根所在的卷（过滤在 <see cref="VolumeGap.Build"/> 里做，渲染器不再判定）。
    /// </summary>
    private static void AppendVolumeGapSection(StringBuilder sb, ReportModel m)
    {
        if (m.VolumeGaps.Count == 0) return;

        sb.Append("<section id=\"volgap\">\n<h2>").Append(Lang.T("容量口径对照", "Size accounting comparison")).Append("</h2>\n");
        sb.Append("<p class=\"section-desc\">")
          .Append(Lang.T("本表回答一个常见疑问：<strong>为什么这里统计的占用小于资源管理器显示的已用空间？</strong>",
                         "This table answers a common question: <strong>why is the size counted here smaller than the used space "
                       + "shown by Explorer?</strong>"))
          .Append(Lang.T("「本次统计」是逐目录累计的<strong>去重后文件逻辑大小之和</strong>，「卷已用」是卷属性显示的<strong>已分配簇数</strong>，",
                         " \"Counted here\" is the <strong>sum of deduplicated logical file sizes</strong> accumulated per directory, "
                       + "while \"volume used\" is the <strong>number of allocated clusters</strong> reported by the volume."))
          .Append(Lang.T("后者包含不属任何目录的部分，因此前者通常小于后者几个百分点，<strong>属口径差异、不代表有目录被漏扫</strong>。</p>\n",
                         " The latter includes parts that belong to no directory, so the former is usually a few percent smaller. "
                       + "<strong>This is an accounting difference, not evidence of missed directories.</strong></p>\n"));

        sb.Append("<div class=\"table-wrap\">\n<table class=\"data\">\n<thead>\n<tr>");
        sb.Append("<th>").Append(Lang.T("卷", "Volume")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("卷容量", "Volume capacity")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("卷已用", "Volume used")).Append("</th>");
        sb.Append("<th class=\"num\">").Append(Lang.T("本次统计", "Counted here")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("差额", "Difference")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("差额率", "Difference %")).Append("</th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var g in m.VolumeGaps)
        {
            sb.Append("<tr>\n");
            sb.Append("  <td class=\"root-name\">").Append(FormatUtil.Html(g.DriveLabel)).Append("</td>\n");
            sb.Append("  <td class=\"num sub\">").Append(FormatUtil.Bytes(g.CapacityBytes)).Append("</td>\n");
            sb.Append("  <td class=\"num\">").Append(FormatUtil.Bytes(g.UsedBytes)).Append("</td>\n");
            sb.Append("  <td class=\"num\">").Append(FormatUtil.Bytes(g.ScannedBytes)).Append("</td>\n");
            sb.Append("  <td class=\"num sub\">").Append(FormatUtil.SignedBytes(g.GapBytes)).Append("</td>\n");
            sb.Append("  <td class=\"num sub\">").Append(FormatUtil.Percent(g.GapPercent)).Append("</td>\n");
            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
        sb.Append("<p class=\"hint\">")
          .Append(Lang.T("差额来自三处，都不是漏扫：", "The difference comes from three places, none of which is a missed scan: "))
          .Append(Lang.T("① NTFS 元数据与 MFT 保留区（<span class=\"mono\">$MFT</span>、目录索引 <span class=\"mono\">$I30</span>、",
                         "1) NTFS metadata and MFT reserved areas (<span class=\"mono\">$MFT</span>, directory indexes "
                       + "<span class=\"mono\">$I30</span>, "))
          .Append(Lang.T("<span class=\"mono\">$Secure:$SDS</span> 等）不属任何目录，系统却计入卷已用；",
                         "<span class=\"mono\">$Secure:$SDS</span> and so on) belong to no directory, yet the system counts them "
                       + "as volume used; "))
          .Append(Lang.T("② 被排除或被跳过的目录，其占用不计入统计（见「跳过清单」）；",
                         "2) excluded or skipped directories are not counted at all (see the \"Skip list\"); "))
          .Append(Lang.T("③ 若关闭硬链接去重，跨目录指向的同一份物理数据会被重复计入，此时统计可能反而<strong>大于</strong>卷已用、差额为负。</p>\n",
                         "3) with hardlink dedup disabled, the same physical data reached through several directories is counted "
                       + "more than once, and the counted size can then be <strong>larger</strong> than volume used, making the "
                       + "difference negative.</p>\n"));

        if (m.Statistics.AccessDeniedCount > 0)
        {
            sb.Append("<div class=\"notice notice-warn\">\n");
            sb.Append(Lang.T("  <strong>权限不足的影响：</strong>本次有 ", "  <strong>Effect of denied access:</strong> "))
              .Append(FormatUtil.Count(m.Statistics.AccessDeniedCount))
              .Append(Lang.T(" 个目录因权限不足未能统计，其占用<strong>未计入</strong>上表的「本次统计」，因此统计值可能偏小。",
                             " directories could not be measured because access was denied; their size is <strong>not counted</strong> "
                           + "in \"Counted here\" above, so that figure may be too low."))
              .Append(Lang.T("以管理员身份运行可消除大部分此类跳过（管理员也会被拒的只剩 SYSTEM 专属的系统保护区）。",
                             " Running as administrator removes most of these skips (only areas exclusively owned by SYSTEM "
                           + "still refuse even an administrator)."))
              .Append(Lang.T("<strong>注意：</strong>这些目录已写入跳过清单，下次扫描会<strong>直接略过、不再重试</strong>，",
                             "<strong>Note:</strong> these directories are on the skip list and the next run will "
                           + "<strong>pass over them without retrying</strong>, "))
              .Append(Lang.T("因此提权后还需要加 <span class=\"mono\">--reset-skips</span> 才会重新尝试一次。",
                             "so even after elevating you need <span class=\"mono\">--reset-skips</span> to try them once more. "))
              .Append(Lang.T("具体路径见「跳过清单」。\n</div>\n",
                             "See the \"Skip list\" for the exact paths.\n</div>\n"));
        }

        sb.Append("</section>\n");
    }

    // ------------------------------------------------------------ 首次运行：最大目录

    /// <summary>
    /// 首次运行时没有对比基准，展示「占用最大的目录」替代增长榜。
    /// 数据源复用 Analysis.NewTop —— 首次运行下它已按占用降序排列。
    /// </summary>
    private static void AppendLargest(StringBuilder sb, ReportModel m)
    {
        var items = m.Analysis.NewTop;
        sb.Append("<section id=\"largest\">\n<h2>").Append(Lang.T("占用最大的目录", "Largest directories")).Append("</h2>\n");
        sb.Append("<p class=\"section-desc\">")
          .Append(Lang.T("首次运行尚无对比基准，此处按目录占用降序展示，",
                         "There is no comparison baseline on the first run, so directories are listed by size, descending, "))
          .Append(Lang.T("便于立即了解空间分布；下次运行将显示增长对比结果。</p>\n",
                         "so that the space distribution is immediately visible. The next run will show growth comparison.</p>\n"));

        if (items.Count == 0)
        {
            sb.Append("<div class=\"empty\">").Append(Lang.T("没有可展示的目录。", "No directories to show."))
              .Append("</div>\n</section>\n");
            return;
        }

        long maxSize = items.Max(i => i.CurrBytes ?? 0);
        if (maxSize <= 0) maxSize = 1;

        sb.Append("<div class=\"table-wrap\">\n<table class=\"data\">\n<thead>\n<tr>");
        sb.Append("<th class=\"rank\">#</th><th>").Append(Lang.T("路径", "Path")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("占用", "Size")).Append("</th>");
        sb.Append("<th class=\"num\">").Append(Lang.T("文件数", "Files")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("层级", "Depth")).Append("</th>")
          .Append("<th>").Append(Lang.T("相对幅度", "Relative scale")).Append("</th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        int rank = 1;
        foreach (var it in items)
        {
            long size = it.CurrBytes ?? 0;
            sb.Append("<tr>\n");
            sb.Append("  <td class=\"rank\">").Append(rank++).Append("</td>\n");
            AppendPathCell(sb, it);
            sb.Append("  <td class=\"num strong\">").Append(FormatUtil.Bytes(size)).Append("</td>\n");
            sb.Append("  <td class=\"num sub\">").Append(FormatUtil.Count(it.FileCountDelta)).Append("</td>\n");
            sb.Append("  <td class=\"num sub\">").Append(it.Depth).Append("</td>\n");
            sb.Append("  <td class=\"bar-cell\"><span class=\"bar bar-up\" style=\"width:")
              .Append(Math.Max(1.0, size * 100.0 / maxSize).ToString("0.##",
                  System.Globalization.CultureInfo.InvariantCulture))
              .Append("%\"></span></td>\n");
            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n</section>\n");
    }

    // ------------------------------------------------------------ 榜单小节

    private static void AppendRankSection(
        StringBuilder sb, ReportModel m, string id, string title, string desc,
        IReadOnlyList<GrowthItem> items, string dir,
        string? fallbackNotice = null, string? emptyReason = null)
    {
        sb.Append("<section id=\"").Append(id).Append("\">\n");
        sb.Append("<h2>").Append(FormatUtil.Html(title)).Append("</h2>\n");
        sb.Append("<p class=\"section-desc\">").Append(FormatUtil.Html(desc)).Append("</p>\n");

        if (fallbackNotice != null && items.Count > 0)
        {
            sb.Append("<div class=\"notice notice-warn\">").Append(fallbackNotice).Append("</div>\n");
        }

        if (items.Count == 0)
        {
            sb.Append("<div class=\"empty\">")
              .Append(FormatUtil.Html(emptyReason ?? Lang.T("没有符合阈值的目录。", "No directory matched the threshold.")))
              .Append("</div>\n</section>\n");
            return;
        }

        long maxAbs = items.Max(i => Math.Abs(i.GrowthBytes));
        if (maxAbs <= 0) maxAbs = 1;

        sb.Append("<div class=\"table-wrap\">\n<table class=\"data\">\n<thead>\n<tr>");
        sb.Append("<th class=\"rank\">#</th><th>").Append(Lang.T("路径", "Path")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("变化量", "Change")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("变化率", "Change %")).Append("</th>");
        sb.Append("<th class=\"num\">").Append(Lang.T("本次占用", "Current size")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("上次占用", "Previous size")).Append("</th>")
          .Append("<th class=\"num\">").Append(Lang.T("文件数变化", "File count change")).Append("</th>")
          .Append("<th>").Append(Lang.T("相对幅度", "Relative scale")).Append("</th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        int rank = 1;
        foreach (var it in items)
        {
            sb.Append("<tr>\n");
            sb.Append("  <td class=\"rank\">").Append(rank++).Append("</td>\n");
            AppendPathCell(sb, it);
            sb.Append("  <td class=\"num v-").Append(dir).Append(" strong\">")
              .Append(FormatUtil.SignedBytes(it.GrowthBytes)).Append("</td>\n");
            sb.Append("  <td class=\"num v-").Append(dir).Append("\">")
              .Append(FormatUtil.SignedPercent(it.GrowthPercent)).Append("</td>\n");
            sb.Append("  <td class=\"num\">").Append(FormatUtil.Bytes(it.CurrBytes ?? 0)).Append("</td>\n");
            sb.Append("  <td class=\"num sub\">")
              .Append(it.PrevBytes.HasValue ? FormatUtil.Bytes(it.PrevBytes.Value) : "—").Append("</td>\n");
            sb.Append("  <td class=\"num sub\">")
              .Append(it.FileCountDelta > 0 ? "+" : "").Append(FormatUtil.Count(it.FileCountDelta)).Append("</td>\n");
            sb.Append("  <td class=\"bar-cell\"><span class=\"bar bar-").Append(dir).Append("\" style=\"width:")
              .Append(Math.Max(1.0, Math.Abs(it.GrowthBytes) * 100.0 / maxAbs).ToString("0.##",
                  System.Globalization.CultureInfo.InvariantCulture))
              .Append("%\"></span></td>\n");
            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n</section>\n");
    }

    // ------------------------------------------------------- 新增 / 消失目录

    private static void AppendNewRemoved(StringBuilder sb, ReportModel m)
    {
        var a = m.Analysis;
        sb.Append("<section id=\"newremoved\">\n<h2>").Append(Lang.T("新增与消失的目录", "Added and removed directories")).Append("</h2>\n");
        sb.Append("<p class=\"section-desc\">")
          .Append(Lang.T("新增 = 上一轮快照中不存在；消失 = 本轮不存在且未被主动跳过。",
                         "Added = absent from the previous snapshot; removed = absent now and not deliberately skipped."))
          .Append("</p>\n");

        sb.Append("<div class=\"grid-2\">\n");

        sb.Append("<div class=\"panel\">\n<h3>").Append(Lang.T("新增目录", "Added directories"));
        if (a.NewTop.Count > 0)
            sb.Append(Lang.F("（共 {0} 个，显示占用最大的前 {1} 个）",
                             " ({0} in total, showing the {1} largest)",
                             FormatUtil.Count(a.Summary.NewCount), a.NewTop.Count));
        sb.Append("</h3>\n");
        if (a.NewTop.Count == 0)
            sb.Append("<div class=\"empty\">").Append(Lang.T("无", "none")).Append("</div>\n");
        else
        {
            sb.Append("<table class=\"data compact\">\n<thead><tr><th>").Append(Lang.T("路径", "Path"))
              .Append("</th><th class=\"num\">").Append(Lang.T("占用", "Size"))
              .Append("</th><th class=\"num\">").Append(Lang.T("文件数变化", "File count change"))
              .Append("</th></tr></thead>\n<tbody>\n");
            foreach (var it in a.NewTop)
            {
                sb.Append("  <tr><td class=\"mono wrap path\">");
                AppendPathInner(sb, it);
                sb.Append("</td><td class=\"num v-new\">").Append(FormatUtil.Bytes(it.CurrBytes ?? 0))
                  .Append("</td><td class=\"num sub\">+").Append(FormatUtil.Count(it.FileCountDelta)).Append("</td></tr>\n");
            }
            sb.Append("</tbody>\n</table>\n");
        }
        sb.Append("</div>\n");

        sb.Append("<div class=\"panel\">\n<h3>").Append(Lang.T("消失目录", "Removed directories"));
        if (a.RemovedTop.Count > 0)
            sb.Append(Lang.F("（共 {0} 个，显示原占用最大的前 {1} 个）",
                             " ({0} in total, showing the {1} that were largest)",
                             FormatUtil.Count(a.Summary.RemovedCount), a.RemovedTop.Count));
        sb.Append("</h3>\n");
        if (a.RemovedTop.Count == 0)
            sb.Append("<div class=\"empty\">").Append(Lang.T("无", "none")).Append("</div>\n");
        else
        {
            sb.Append("<table class=\"data compact\">\n<thead><tr><th>").Append(Lang.T("路径", "Path"))
              .Append("</th><th class=\"num\">").Append(Lang.T("原占用", "Previous size"))
              .Append("</th><th class=\"num\">").Append(Lang.T("文件数变化", "File count change"))
              .Append("</th></tr></thead>\n<tbody>\n");
            foreach (var it in a.RemovedTop)
            {
                sb.Append("  <tr><td class=\"mono wrap path\">");
                AppendPathInner(sb, it);
                sb.Append("</td><td class=\"num v-removed\">").Append(FormatUtil.Bytes(it.PrevBytes ?? 0))
                  .Append("</td><td class=\"num sub\">").Append(FormatUtil.Count(it.FileCountDelta)).Append("</td></tr>\n");
            }
            sb.Append("</tbody>\n</table>\n");
        }
        sb.Append("</div>\n</div>\n</section>\n");
    }

    // ------------------------------------------------------------ 详细明细表

    private static void AppendDetail(StringBuilder sb, ReportModel m)
    {
        var items = m.Analysis.Detail;
        sb.Append("<section id=\"detail\">\n<h2>").Append(Lang.T("详细明细", "Details")).Append("</h2>\n");
        if (m.IsFirstRun)
        {
            sb.Append("<p class=\"section-desc\">")
              .Append(Lang.F("首次运行：列出占用 ≥ {0} 的全部目录（按占用降序，最多 {1} 行，实际 {2} 行）。",
                             "First run: all directories of size ≥ {0} (by size, descending, up to {1} rows, {2} shown).",
                             FormatUtil.Html(FormatUtil.MbThreshold(m.DetailMinBytes)),
                             FormatUtil.Count(m.DetailTop), FormatUtil.Count(items.Count)))
              .Append(Lang.T("「变化量」列此时即目录占用，「变化率」显示 NEW 属正常。支持搜索与排序。</p>\n",
                             " The \"Change\" column is the directory size in this case, and \"Change %\" showing NEW is normal. "
                           + "Search and sorting are supported.</p>\n"));
        }
        else
        {
            sb.Append("<p class=\"section-desc\">")
              .Append(m.Analysis.DetailFallbackUsed
                  ? Lang.T("按变化量绝对值降序", "All directories, by absolute change, descending")
                  : Lang.F("所有变化量 ≥ {0} 的目录，按变化量绝对值降序",
                           "All directories with a change ≥ {0}, by absolute change, descending",
                           FormatUtil.Html(FormatUtil.MbThreshold(m.DetailMinBytes))))
              .Append(Lang.F("，最多显示 {0} 行（实际 {1} 行）。支持搜索、排序与筛选。</p>\n",
                             ", up to {0} rows ({1} shown). Search, sorting and filtering are supported.</p>\n",
                             FormatUtil.Count(m.DetailTop), FormatUtil.Count(items.Count)));
        }

        if (m.Analysis.DetailFallbackUsed && items.Count > 0)
        {
            sb.Append("<div class=\"notice notice-warn\">")
              .Append(Lang.F("本轮所有目录的变化量都低于 {0}，下表为<strong>未应用阈值</strong>时变化量最大的 {1} 个目录。",
                             "Every directory changed by less than {0} this run; the table below shows the {1} largest changes "
                           + "with <strong>no threshold applied</strong>.",
                             FormatUtil.Html(FormatUtil.MbThreshold(m.DetailMinBytes)), FormatUtil.Count(items.Count)))
              .Append("</div>\n");
        }

        if (items.Count == 0)
        {
            sb.Append("<div class=\"empty\">")
              .Append(Lang.T("本轮没有任何目录的占用发生变化。", "No directory changed size this run."))
              .Append("</div>\n</section>\n");
            return;
        }

        sb.Append("<div class=\"toolbar\">\n");
        sb.Append("  <input id=\"dtSearch\" type=\"search\" placeholder=\"")
          .Append(Lang.T("按路径关键字搜索…", "Search by path keyword…"))
          .Append("\" autocomplete=\"off\">\n");
        sb.Append("  <div class=\"seg\" id=\"dtStatus\">\n");
        sb.Append("    <button class=\"seg-btn active\" data-status=\"\">").Append(Lang.T("全部", "All")).Append("</button>\n");
        foreach (var st in StatusKeys)
            sb.Append("    <button class=\"seg-btn\" data-status=\"").Append(st).Append("\">")
              .Append(FormatUtil.Html(StatusLabel(st))).Append("</button>\n");
        sb.Append("  </div>\n");
        sb.Append("  <label class=\"chk\"><input id=\"dtLeafOnly\" type=\"checkbox\"> <span>")
          .Append(Lang.T("仅显示最深层级（隐藏已被下层级覆盖的父目录）",
                         "Deepest level only (hide parents covered by a deeper entry)"))
          .Append("</span></label>\n");
        sb.Append("  <span class=\"toolbar-info\" id=\"dtCount\"></span>\n");
        sb.Append("</div>\n");

        sb.Append("<div class=\"table-wrap\">\n<table class=\"data sortable\" id=\"dtTable\">\n<thead>\n<tr>");
        sb.Append("<th data-key=\"status\">").Append(Lang.T("状态", "Status")).Append("</th>");
        sb.Append("<th data-key=\"delta\" class=\"num sorted-desc\">").Append(Lang.T("变化量", "Change")).Append("</th>");
        sb.Append("<th data-key=\"pct\" class=\"num\">").Append(Lang.T("变化率", "Change %")).Append("</th>");
        sb.Append("<th data-key=\"curr\" class=\"num\">").Append(Lang.T("本次占用", "Current size")).Append("</th>");
        sb.Append("<th data-key=\"prev\" class=\"num\">").Append(Lang.T("上次占用", "Previous size")).Append("</th>");
        sb.Append("<th data-key=\"fdelta\" class=\"num\">").Append(Lang.T("文件数变化", "File count change")).Append("</th>");
        sb.Append("<th data-key=\"depth\" class=\"num\">").Append(Lang.T("层级", "Depth")).Append("</th>");
        sb.Append("<th data-key=\"path\">").Append(Lang.T("路径", "Path")).Append("</th>");
        sb.Append("</tr>\n</thead>\n<tbody id=\"dtBody\">\n");

        foreach (var it in items)
        {
            string cls = it.GrowthBytes > 0 ? "up" : it.GrowthBytes < 0 ? "down" : "flat";
            double pctForSort = double.IsNaN(it.GrowthPercent) ? double.PositiveInfinity : it.GrowthPercent;
            sb.Append("<tr class=\"row-").Append(cls).Append("\"")
              .Append(" data-status=\"").Append(StatusKey(it.Status)).Append("\"")
              .Append(" data-statusrank=\"").Append(StatusRank(it.Status)).Append("\"")
              .Append(" data-delta=\"").Append(it.GrowthBytes).Append("\"")
              .Append(" data-pct=\"").Append(pctForSort.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append("\"")
              .Append(" data-curr=\"").Append(it.CurrBytes ?? 0).Append("\"")
              .Append(" data-prev=\"").Append(it.PrevBytes ?? 0).Append("\"")
              .Append(" data-fdelta=\"").Append(it.FileCountDelta).Append("\"")
              .Append(" data-depth=\"").Append(it.Depth).Append("\"")
              .Append(" data-path=\"").Append(FormatUtil.Html(it.Path)).Append("\">\n");

            sb.Append("  <td><span class=\"tag tag-").Append(cls).Append("\">")
              .Append(FormatUtil.Html(it.StatusText)).Append("</span></td>\n");
            sb.Append("  <td class=\"num v-").Append(cls).Append(" strong\">")
              .Append(FormatUtil.SignedBytes(it.GrowthBytes)).Append("</td>\n");
            sb.Append("  <td class=\"num\">").Append(FormatUtil.SignedPercent(it.GrowthPercent)).Append("</td>\n");
            sb.Append("  <td class=\"num\">").Append(FormatUtil.Bytes(it.CurrBytes ?? 0)).Append("</td>\n");
            sb.Append("  <td class=\"num sub\">")
              .Append(it.PrevBytes.HasValue ? FormatUtil.Bytes(it.PrevBytes.Value) : "—").Append("</td>\n");
            sb.Append("  <td class=\"num sub\">")
              .Append(it.FileCountDelta > 0 ? "+" : "").Append(FormatUtil.Count(it.FileCountDelta)).Append("</td>\n");
            sb.Append("  <td class=\"num sub\">").Append(it.Depth).Append("</td>\n");
            AppendPathCell(sb, it);
            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n</section>\n");
    }

    // ------------------------------------------------------- 路径单元格渲染

    /// <summary>渲染完整的路径单元格（独立成行）。</summary>
    private static void AppendPathCell(StringBuilder sb, GrowthItem it)
    {
        sb.Append("  <td class=\"mono wrap path\">");
        AppendPathInner(sb, it);
        sb.Append("</td>\n");
    }

    /// <summary>
    /// 路径单元格内容：路径正文 +（命中知识库时）一行浅色用途小字。
    /// 小字不新增列、不引起横向滚动，明细表（已有 8 列）也能容纳；
    /// 完整说明放在 title 属性里，鼠标悬停可看。
    /// </summary>
    private static void AppendPathInner(StringBuilder sb, GrowthItem it)
    {
        sb.Append(FormatUtil.Html(it.Path));
        if (it.Knowledge is not { } match) return;

        var e = match.Entry;
        sb.Append("<span class=\"dir-note ").Append(e.CleanableCss).Append("\" title=\"")
          .Append(FormatUtil.Html(e.NoteText.Length > 0 ? e.NoteText : e.TitleText)).Append("\">")
          .Append(FormatUtil.Html(e.TitleText));
        if (e.CleanableText.Length > 0)
            sb.Append(" · ").Append(FormatUtil.Html(e.CleanableText));
        sb.Append("</span>");
    }

    // ------------------------------------------------------------ 跳过清单

    private static void AppendSkipSection(StringBuilder sb, ReportModel m)
    {
        sb.Append("<section id=\"skips\">\n<h2>").Append(Lang.T("跳过清单", "Skip list")).Append("</h2>\n");
        sb.Append("<p class=\"section-desc\">")
          .Append(Lang.T("这些目录本次<strong>未被扫描或未被深入</strong>，其占用不计入统计。",
                         "These directories were <strong>not scanned or not descended into</strong> this run, and their size is not "
                       + "counted."));
        if (m.KnowledgeRescanEnabled)
        {
            sb.Append(Lang.T("其中「权限不足」与「IO 错误」的目录已写入数据库，通常下次扫描会直接跳过；",
                             " The \"access denied\" and \"IO error\" entries are stored in the database and are normally skipped "
                           + "outright by the next run; "));
            sb.Append(Lang.T("但标有 <span class=\"tag tag-retry\">每轮重试</span> 的目录命中<strong>目录用途知识库</strong>",
                             "however, entries tagged <span class=\"tag tag-retry\">retried every run</span> match the "
                           + "<strong>directory-purpose knowledge base</strong> "));
            sb.Append(Lang.T("（自身是已知目录，或其下含已知目录），<strong>每次扫描都会重新尝试</strong>，",
                             "(either they are a known directory themselves or contain one), so they are "
                           + "<strong>retried on every scan</strong> "));
            sb.Append(Lang.T("以免历史权限记录或默认排除造成长期盲区。如需让全部记录都重新尝试，可用 ",
                             "to keep a historical denial or a default exclusion from becoming a permanent blind spot. "
                           + "To retry every record, use "));
            sb.Append("<span class=\"mono\">--reset-skips</span>");
            sb.Append(Lang.T("；如需关闭该豁免，可用 ", "; to disable the exemption, use "));
            sb.Append("<span class=\"mono\">--no-knowledge-rescan</span>")
              .Append(Lang.T("。</p>\n", ".</p>\n"));
        }
        else
        {
            sb.Append(Lang.T("其中「权限不足」与「IO 错误」的目录已写入数据库，<strong>下次扫描会直接跳过</strong>，不再重复尝试；",
                             " The \"access denied\" and \"IO error\" entries are stored in the database and the "
                           + "<strong>next run will skip them outright</strong> without retrying; "));
            sb.Append(Lang.T("如需重新尝试，请使用 ", "to retry them, use "));
            sb.Append("<span class=\"mono\">--reset-skips</span>")
              .Append(Lang.T("。</p>\n", ".</p>\n"));
        }

        if (m.SkipRecords.Count == 0)
        {
            sb.Append("<div class=\"empty\">")
              .Append(Lang.T("本次扫描没有跳过任何目录。", "No directory was skipped this run."))
              .Append("</div>\n</section>\n");
            return;
        }

        var groups = m.SkipRecords
            .GroupBy(r => r.Reason)
            .OrderBy(g => (int)g.Key)
            .ToList();

        foreach (var g in groups)
        {
            bool persistent = SkipRecord.IsPersistentSkip(g.Key);
            int retryCount = m.KnowledgeRescanEnabled
                ? g.Count(x => m.KnowledgeRescuedPaths.Contains(x.Path))
                : 0;

            sb.Append("<details class=\"skip-group\"").Append(g.Count() <= 20 ? " open" : "").Append(">\n");
            sb.Append("  <summary><span class=\"skip-title\">").Append(FormatUtil.Html(SkipRecord.ReasonText(g.Key)))
              .Append("</span><span class=\"skip-count\">")
              .Append(Lang.F("{0} 项", "{0} items", FormatUtil.Count(g.Count())))
              .Append("</span>");
            if (retryCount > 0)
                sb.Append("<span class=\"tag tag-retry\">")
                  .Append(Lang.F("{0} 项每轮重试", "{0} retried every run", FormatUtil.Count(retryCount)))
                  .Append("</span>");
            else if (persistent)
                sb.Append("<span class=\"tag tag-warn\">").Append(Lang.T("下次自动跳过", "skipped automatically next run")).Append("</span>");
            sb.Append("</summary>\n");

            sb.Append("  <div class=\"table-wrap\">\n<table class=\"data compact\">\n<thead><tr>");
            sb.Append("<th>").Append(Lang.T("目录", "Directory")).Append("</th>")
              .Append("<th class=\"num\">").Append(Lang.T("命中次数", "Hits")).Append("</th>")
              .Append("<th>").Append(Lang.T("说明", "Detail")).Append("</th>");
            sb.Append("</tr></thead>\n<tbody>\n");
            foreach (var rec in g.OrderBy(x => x.Path))
            {
                sb.Append("    <tr><td class=\"mono wrap path\">").Append(FormatUtil.Html(rec.Path));
                if (m.KnowledgeRescuedPaths.Contains(rec.Path))
                    sb.Append("<span class=\"dir-note clean-safe\" title=\"")
                      .Append(Lang.T("命中目录用途知识库，每次扫描都会重新尝试遍历",
                                     "matches the directory-purpose knowledge base; traversal is retried on every scan"))
                      .Append("\">").Append(Lang.T("每轮重试", "retried every run")).Append("</span>");
                sb.Append("</td>");
                sb.Append("<td class=\"num sub\">").Append(FormatUtil.Count(Math.Max(1, rec.ExistingHitCount))).Append("</td>");
                sb.Append("<td class=\"sub\">").Append(FormatUtil.Html(rec.Detail ?? string.Empty)).Append("</td></tr>\n");
            }
            sb.Append("  </tbody>\n</table>\n</div>\n");
            sb.Append("</details>\n");
        }

        sb.Append("</section>\n");
    }

    // ------------------------------------------------------------------ 页脚

    private static void AppendFooter(StringBuilder sb, ReportModel m)
    {
        sb.Append("<footer class=\"page-foot\">\n");
        sb.Append("<h3>").Append(Lang.T("指标说明", "Metric notes")).Append("</h3>\n<ul class=\"notes\">\n");
        sb.Append("<li>")
          .Append(Lang.T("<strong>占用</strong>：目录子树的累计字节数，包含所有后代目录与文件；硬链接指向的同一份物理数据只计一次（计入首次出现的路径）。",
                         "<strong>Size</strong>: the accumulated byte count of a directory subtree, including all descendant "
                       + "directories and files; the same physical data behind hardlinks is counted once, under the first path "
                       + "it appears at."))
          .Append("</li>\n");
        sb.Append("<li>")
          .Append(Lang.T("<strong>变化量</strong> = 本次占用 − 上次占用；<strong>变化率</strong> = 变化量 ÷ 上次占用。上次占用为 0 或不存在时变化率显示为 NEW。",
                         "<strong>Change</strong> = current size − previous size; <strong>Change %</strong> = change ÷ previous "
                       + "size. When the previous size is 0 or absent, the percentage shows NEW."))
          .Append("</li>\n");
        sb.Append("<li>")
          .Append(Lang.T("<strong>目录级变化天然重叠</strong>：父目录的变化量已经包含其子目录的变化量，因此榜单中出现嵌套路径属于正常现象，可勾选「仅显示最深层级」查看最细粒度的增长点。",
                         "<strong>Per-directory changes overlap by nature</strong>: a parent's change already includes its "
                       + "children's, so nested paths in the rankings are expected. Tick \"Deepest level only\" to see the finest "
                       + "grained growth points."))
          .Append("</li>\n");
        sb.Append("<li>")
          .Append(Lang.T("<strong>增长榜默认从第一层子目录开始统计</strong>，根目录自身的变化请查看「各扫描根对比」。",
                         "<strong>The growth ranking starts at the first level of subdirectories</strong>; for the scan root's own "
                       + "change see \"Scan root comparison\"."))
          .Append("</li>\n");
        sb.Append("<li>")
          .Append(Lang.T("<strong>统计占用 vs 卷已用</strong>：前者是去重后的文件逻辑大小之和，后者是卷已分配的簇数（含不属任何目录的 NTFS 元数据与保留区），因此前者通常小几个百分点，详见「容量口径对照」。",
                         "<strong>Counted size vs. volume used</strong>: the former is the sum of deduplicated logical file "
                       + "sizes, the latter the number of allocated clusters (including NTFS metadata and reserved areas that "
                       + "belong to no directory), so the former is usually a few percent smaller. See \"Size accounting "
                       + "comparison\"."))
          .Append("</li>\n");
        sb.Append("<li>")
          .Append(Lang.T("被排除目录（默认排除清单与 <span class=\"mono\">--exclude</span>）与被跳过的目录，其占用不计入任何祖先目录的累计值。",
                         "Excluded directories (the default list and <span class=\"mono\">--exclude</span>) and skipped directories "
                       + "are not counted towards any ancestor's accumulated size."))
          .Append("</li>\n");
        sb.Append("</ul>\n");
        sb.Append("<div class=\"foot-meta\">").Append(Lang.T("报告生成于 ", "Report generated at "))
          .Append(FormatUtil.Html(FormatUtil.Timestamp(m.GeneratedAt)))
          .Append(Lang.T("　·　扫描批次 #", "　·　run #")).Append(m.RunId)
          .Append(Lang.T("　·　上次对比基准：", "　·　previous baseline: "))
          .Append(m.Analysis.PrevScanTime.HasValue
              ? Lang.F("批次 #{0}（{1}）", "run #{0} ({1})", m.PreviousRunId,
                       FormatUtil.Html(FormatUtil.Timestamp(m.Analysis.PrevScanTime.Value)))
              : Lang.T("无", "none"))
          .Append("</div>\n");
        sb.Append("<div class=\"foot-meta\">").Append(Lang.T("数据库：", "Database: "))
          .Append(FormatUtil.Html(m.DatabasePath)).Append("</div>\n");
        sb.Append("</footer>\n");
    }

    // ------------------------------------------------------------------ 资源

    private const string Css = """
    :root{
      --bg:#f4f6f9; --card:#ffffff; --border:#e2e7ee; --border-strong:#cfd8e3;
      --text:#1e2633; --muted:#6b7684; --muted-2:#8b95a3;
      --up:#c62828; --up-bg:#fdecec;
      --down:#177245; --down-bg:#eaf6ef;
      --new:#1d5fc4; --new-bg:#eaf1fd;
      --removed:#5a6472; --removed-bg:#eef1f5;
      --warn:#a35a00; --warn-bg:#fdf5e6;
      --accent:#2b6cb0;
    }
    *{box-sizing:border-box}
    body{
      margin:0; padding:28px 32px 64px; background:var(--bg); color:var(--text);
      font-family:"Segoe UI","Microsoft YaHei UI","Microsoft YaHei",system-ui,-apple-system,sans-serif;
      font-size:14px; line-height:1.6;
    }
    h1{font-size:24px; margin:0 0 6px; font-weight:650; letter-spacing:.2px}
    h2{font-size:18px; margin:0 0 10px; font-weight:650; padding-bottom:8px; border-bottom:2px solid var(--border)}
    h3{font-size:15px; margin:0 0 10px; font-weight:650}
    section{background:var(--card); border:1px solid var(--border); border-radius:10px;
      padding:20px 22px; margin-bottom:20px; box-shadow:0 1px 2px rgba(16,24,40,.04)}
    .page-head{display:flex; align-items:flex-start; justify-content:space-between; gap:16px;
      background:var(--card); border:1px solid var(--border); border-radius:10px;
      padding:20px 22px; margin-bottom:20px; box-shadow:0 1px 2px rgba(16,24,40,.04)}
    .ph-sub{color:var(--muted); font-size:13px}
    .ph-badge{flex:0 0 auto; padding:6px 14px; border-radius:999px; font-size:13px; font-weight:650}
    .badge-cmp{background:var(--new-bg); color:var(--new)}
    .badge-new{background:var(--warn-bg); color:var(--warn)}

    .notice{border-radius:8px; padding:12px 16px; margin-bottom:20px; font-size:13.5px; border:1px solid}
    .notice-info{background:var(--new-bg); border-color:#c6daf8; color:#153f85}
    .notice-warn{background:var(--warn-bg); border-color:#f0dcb0; color:#7a4400}

    .kpis{display:grid; grid-template-columns:repeat(auto-fit,minmax(210px,1fr)); gap:14px; margin-bottom:20px}
    .kpi{background:#fbfcfe; border:1px solid var(--border); border-radius:8px; padding:14px 16px}
    .kpi-label{font-size:12.5px; color:var(--muted); margin-bottom:6px}
    .kpi-value{font-size:21px; font-weight:660; letter-spacing:.2px; font-variant-numeric:tabular-nums}
    .kpi-sub{font-size:12px; color:var(--muted-2); margin-top:5px}
    .kpi-up{color:var(--up)} .kpi-down{color:var(--down)} .kpi-warn{color:var(--warn)}
    .kpi-sub,.kpi-flat{color:var(--text)}

    .grid-2{display:grid; grid-template-columns:1fr 1fr; gap:18px}
    @media (max-width:1100px){.grid-2{grid-template-columns:1fr}}
    .panel{background:#fbfcfe; border:1px solid var(--border); border-radius:8px; padding:16px 18px}
    /* Full-width panel inside the overview: keep the same spacing as the two columns above */
    .panel-clean{margin-top:18px}

    table{border-collapse:separate; border-spacing:0; width:100%}
    table.data{font-size:13px}
    table.data th{background:#f2f5f9; color:#42505f; font-weight:620; text-align:left;
      padding:9px 12px; border-bottom:1px solid var(--border-strong); white-space:nowrap; position:sticky; top:0; z-index:2}
    table.data td{padding:8px 12px; border-bottom:1px solid #eef1f5; vertical-align:middle}
    table.data tbody tr:hover{background:#f7fafd}
    table.data.compact td, table.data.compact th{padding:6px 10px}
    table.mini{font-size:13px}
    table.mini th{text-align:left; padding:7px 10px 7px 0; color:#42505f; font-weight:600; white-space:nowrap; vertical-align:top}
    table.mini td{padding:7px 10px; border-bottom:1px solid #eef1f5}
    table.mini td.num{text-align:right; font-variant-numeric:tabular-nums; white-space:nowrap}
    table.mini td.sub{color:var(--muted-2); font-size:12.5px}

    .table-wrap{overflow:auto; max-height:none; border:1px solid var(--border); border-radius:8px; background:var(--card)}
    #detail .table-wrap{max-height:70vh}
    .num{text-align:right; font-variant-numeric:tabular-nums; white-space:nowrap}
    .rank{width:44px; text-align:right; color:var(--muted); font-variant-numeric:tabular-nums}
    .mono{font-family:"Cascadia Mono",Consolas,"Courier New",monospace; font-size:12.5px}
    .wrap{word-break:break-all; white-space:normal; min-width:280px}
    .path{color:#243447}
    /* Purpose note for known directories: extra column-free line under the path; cleanability by colour */
    .dir-note{display:block; margin-top:3px; font-size:11.5px; line-height:1.45; font-weight:400;
      color:var(--muted-2);
      font-family:"Segoe UI","Microsoft YaHei UI","Microsoft YaHei",system-ui,-apple-system,sans-serif}
    .dir-note::before{content:"▸ "; color:#aab4c0}
    .dir-note.clean-safe{color:#177245}
    .dir-note.clean-cautious{color:#a35a00}
    .dir-note.clean-forbidden{color:#c62828}
    .sub{color:var(--muted-2)}
    .strong{font-weight:660}
    .root-name{font-weight:620; white-space:nowrap}

    .v-up{color:var(--up)} .v-down{color:var(--down)} .v-new{color:var(--new)}
    .v-removed{color:var(--removed)} .v-muted{color:var(--muted-2)} .v-flat{color:var(--text)}
    .v-warn{color:var(--warn)}

    .tag{display:inline-block; padding:2px 9px; border-radius:999px; font-size:12px; font-weight:600; white-space:nowrap}
    .tag-up{background:var(--up-bg); color:var(--up)}
    .tag-down{background:var(--down-bg); color:var(--down)}
    .tag-new{background:var(--new-bg); color:var(--new)}
    .tag-removed{background:var(--removed-bg); color:var(--removed)}
    .tag-flat{background:#f0f2f5; color:var(--muted)}
    .tag-warn{background:var(--warn-bg); color:var(--warn); margin-left:8px; font-weight:600}
    .tag-retry{background:#e8f5ee; color:#177245; margin-left:8px; font-weight:600}
    /* "Safe to clean" list: short purpose name + badge in the purpose cell, note on the next line */
    .use-title{font-weight:620; margin-right:8px}
    .tag-safe{background:#e8f5ee; color:#177245}
    tr.clean-total th,tr.clean-total td{background:#f2f7ff; border-bottom:0; font-weight:640; color:#153f85}

    .bar-cell{width:150px}
    .bar{display:block; height:9px; border-radius:5px; min-width:2px}
    .bar-up{background:linear-gradient(90deg,#e57373,#c62828)}
    .bar-down{background:linear-gradient(90deg,#7fc9a1,#177245)}

    .toolbar{display:flex; flex-wrap:wrap; align-items:center; gap:12px; margin-bottom:12px}
    .toolbar input[type=search]{flex:1 1 260px; min-width:200px; padding:7px 12px;
      border:1px solid var(--border-strong); border-radius:7px; font-size:13px; background:var(--card); color:var(--text)}
    .toolbar input[type=search]:focus{outline:2px solid #bcd6f5; outline-offset:-1px; border-color:#8bb6e8}
    .seg{display:inline-flex; border:1px solid var(--border-strong); border-radius:7px; overflow:hidden; background:var(--card)}
    .seg-btn{border:0; background:transparent; padding:7px 13px; font-size:12.5px; cursor:pointer;
      color:var(--muted); font-family:inherit; border-right:1px solid var(--border)}
    .seg-btn:last-child{border-right:0}
    .seg-btn.active{background:#e8f0fb; color:var(--accent); font-weight:650}
    .chk{display:inline-flex; align-items:center; gap:6px; font-size:12.5px; color:var(--muted); cursor:pointer; user-select:none}
    .toolbar-info{font-size:12.5px; color:var(--muted-2); margin-left:auto; font-variant-numeric:tabular-nums}

    th[data-key]{cursor:pointer; user-select:none}
    th[data-key]:hover{background:#e9eef5}
    th.sorted-asc::after{content:" ▲"; color:var(--accent); font-size:10px}
    th.sorted-desc::after{content:" ▼"; color:var(--accent); font-size:10px}

    details.skip-group{border:1px solid var(--border); border-radius:8px; margin-bottom:12px; background:#fbfcfe}
    details.skip-group > summary{cursor:pointer; padding:11px 15px; font-weight:600; display:flex; align-items:center;
      gap:10px; list-style:none}
    details.skip-group > summary::-webkit-details-marker{display:none}
    details.skip-group > summary::before{content:"▸"; color:var(--muted); font-size:12px; transition:transform .15s}
    details.skip-group[open] > summary::before{transform:rotate(90deg)}
    .skip-title{flex:0 0 auto}
    .skip-count{color:var(--muted-2); font-weight:500; font-size:12.5px}
    details.skip-group .table-wrap{margin:0 15px 15px}

    .empty{color:var(--muted-2); padding:18px; text-align:center; background:#fafbfd;
      border:1px dashed var(--border-strong); border-radius:8px}
    .hint{color:var(--muted-2); font-size:12.5px; margin:10px 0 0}
    .section-desc{color:var(--muted); font-size:13px; margin:0 0 14px}

    .page-foot{background:var(--card); border:1px solid var(--border); border-radius:10px; padding:20px 22px;
      color:var(--muted); font-size:13px}
    .page-foot h3{margin-bottom:10px; color:var(--text)}
    ul.notes{margin:0 0 14px; padding-left:20px}
    ul.notes li{margin-bottom:6px}
    .foot-meta{font-size:12.5px; color:var(--muted-2); margin-top:4px; word-break:break-all}
    """;

    /// <summary>
    /// 内嵌脚本的模板。三处与语言相关的内容用占位符留白，由 <see cref="BuildJs"/> 注入：
    /// <c>__LOCALE__</c>（路径列比较用的区域设置）、<c>__SHOWING__</c> / <c>__ROWS__</c>（行数提示的前后缀）。
    /// 其余部分是纯逻辑，不含可见文本。
    /// </summary>
    private const string JsTemplate = """
    (function(){
      // ---------- Detail table: sorting ----------
      var table = document.getElementById('dtTable');
      if (table) {
        var body = document.getElementById('dtBody');
        var ths = table.querySelectorAll('th[data-key]');
        var state = { key: 'delta', desc: true };

        function num(el, attr){ var v = parseFloat(el.getAttribute('data-' + attr)); return isNaN(v) ? 0 : v; }
        function cmp(a, b){
          var k = state.key, r;
          if (k === 'path') {
            r = (a.getAttribute('data-path') || '').localeCompare(b.getAttribute('data-path') || '', '__LOCALE__');
          } else if (k === 'status') {
            // The status column compares the numeric rank (data-statusrank), not data-status:
            // the latter is the stable key used by the filter buttons
            r = num(a, 'statusrank') - num(b, 'statusrank');
          } else {
            r = num(a, k) - num(b, k);
          }
          return state.desc ? -r : r;
        }
        ths.forEach(function(th){
          th.addEventListener('click', function(){
            var k = th.getAttribute('data-key');
            if (state.key === k) { state.desc = !state.desc; }
            else { state.key = k; state.desc = !(k === 'path' || k === 'status'); }
            ths.forEach(function(x){ x.classList.remove('sorted-asc','sorted-desc'); });
            th.classList.add(state.desc ? 'sorted-desc' : 'sorted-asc');
            applyFilter();
          });
        });

        // ---------- Detail table: search / status filter / collapse parents ----------
        var searchBox = document.getElementById('dtSearch');
        var leafOnly = document.getElementById('dtLeafOnly');
        var countInfo = document.getElementById('dtCount');
        var statusButtons = document.querySelectorAll('#dtStatus .seg-btn');
        var currentStatus = '';
        var allRows = Array.prototype.slice.call(body.querySelectorAll('tr'));

        statusButtons.forEach(function(btn){
          btn.addEventListener('click', function(){
            statusButtons.forEach(function(b){ b.classList.remove('active'); });
            btn.classList.add('active');
            currentStatus = btn.getAttribute('data-status') || '';
            applyFilter();
          });
        });

        function applyFilter(){
          var kw = (searchBox && searchBox.value ? searchBox.value : '').trim().toLowerCase();
          var visible = [];
          allRows.forEach(function(tr){
            var ok = true;
            if (kw && (tr.getAttribute('data-path') || '').toLowerCase().indexOf(kw) < 0) ok = false;
            if (ok && currentStatus && tr.getAttribute('data-status') !== currentStatus) ok = false;
            tr.style.display = ok ? '' : 'none';
            if (ok) visible.push(tr);
          });

          if (leafOnly && leafOnly.checked) {
            // Hide parents whose children are also in the list, keeping only the finest changes
            var paths = visible.map(function(tr){ return (tr.getAttribute('data-path') || '').toLowerCase(); });
            visible.forEach(function(tr){
              var self = (tr.getAttribute('data-path') || '').toLowerCase() + '\\';
              var covered = paths.some(function(other){
                return other !== self.slice(0, -1) && other.indexOf(self) === 0;
              });
              if (covered) tr.style.display = 'none';
            });
          }

          var shown = visible.filter(function(tr){ return tr.style.display !== 'none'; }).length;
          if (countInfo) countInfo.textContent = '__SHOWING__' + shown + ' / ' + allRows.length + '__ROWS__';

          // Sort (re-ordering the whole visible set)
          visible.sort(cmp).forEach(function(tr){ body.appendChild(tr); });
          allRows.filter(function(tr){ return visible.indexOf(tr) < 0; }).forEach(function(tr){ body.appendChild(tr); });
        }

        if (searchBox) searchBox.addEventListener('input', applyFilter);
        if (leafOnly) leafOnly.addEventListener('change', applyFilter);
        window.__dtApply = applyFilter;
        applyFilter();
      }
    })();
    """;

    /// <summary>把语言相关的内容注入脚本模板（见 <see cref="JsTemplate"/>）。</summary>
    private static string BuildJs() => JsTemplate
        .Replace("__LOCALE__", Lang.IsEnglish ? "en" : "zh-CN")
        .Replace("__SHOWING__", Lang.T("显示 ", "Showing "))
        .Replace("__ROWS__", Lang.T(" 行", " rows"));
}
