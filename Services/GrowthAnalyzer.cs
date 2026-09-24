using DiskGrowthMonitor.Models;
using System.Data;
using System.Data.SQLite;

namespace DiskGrowthMonitor.Services;

/// <summary>变化规模汇总（仅计数，不含求和，避免父子目录重复计数造成误读）。</summary>
public sealed class ChangeSummary
{
    public long GrownCount;
    public long ShrunkCount;
    public long NewCount;
    public long RemovedCount;
    /// <summary>上一轮存在但本轮因排除/权限原因未扫描、变化不可比的目录数。</summary>
    public long SkippedCount;
    public long SkippedBytes;
    /// <summary>对比基准中参与比较的目录总数。</summary>
    public long ComparedCount;
}

/// <summary>一次分析的全部产出。</summary>
public sealed class AnalysisResult
{
    public List<RootComparison> Roots { get; } = new();
    public List<GrowthItem> GrowthTop { get; } = new();
    public List<GrowthItem> ShrinkTop { get; } = new();
    public List<GrowthItem> NewTop { get; } = new();
    public List<GrowthItem> RemovedTop { get; } = new();
    public List<GrowthItem> Detail { get; } = new();
    public ChangeSummary Summary { get; } = new();
    public bool IsFirstRun { get; set; }
    public DateTime? PrevScanTime { get; set; }
    public long TotalBytes { get; set; }
    public long? PrevTotalBytes { get; set; }

    /// <summary>增长榜在应用阈值后为空、已自动降级为「未应用阈值」时的提示。</summary>
    public bool GrowthFallbackUsed { get; set; }
    /// <summary>详细明细在应用阈值后为空、已自动降级为「未应用阈值」时的提示。</summary>
    public bool DetailFallbackUsed { get; set; }
    /// <summary>是否存在任何体积减小的目录（用于给出准确的空状态说明）。</summary>
    public bool HasAnyShrink { get; set; }
}

/// <summary>
/// 两次快照的对比分析器。
/// 对比全程在 SQLite 内完成：先把「当前 LEFT JOIN 上一轮」的结果物化为临时表并建索引，
/// 再基于该临时表做多次排序取 Top，避免把百万行数据拉进 .NET 内存。
/// </summary>
public sealed class GrowthAnalyzer
{
    private const string TempTable = "cmp_tmp";

    private readonly DatabaseService _db;

    public GrowthAnalyzer(DatabaseService db) => _db = db;

    public AnalysisResult Analyze(
        IReadOnlyList<ScanRoot> roots,
        int top,
        long minBytes,
        double minPercent,
        int detailTop,
        long detailMinBytes,
        bool isFirstRun,
        DateTime? prevScanTime)
    {
        var result = new AnalysisResult
        {
            IsFirstRun = isFirstRun,
            PrevScanTime = prevScanTime
        };

        BuildTempTable(roots);

        try
        {
            // 各扫描根自身的子树累计（depth = 0 的行即为根）
            result.Roots.AddRange(QueryRootComparisons(roots, isFirstRun));
            result.TotalBytes = result.Roots.Sum(r => r.CurrBytes ?? 0);
            result.PrevTotalBytes = isFirstRun
                ? null
                : result.Roots.Sum(r => r.PrevBytes ?? 0);

            QuerySummary(result.Summary, roots);
            result.Summary.ComparedCount = CountRows(null);
            result.HasAnyShrink = result.Summary.ShrunkCount > 0;

            // 根节点自身的变化已由「各扫描根对比」呈现，增长榜从第一层子目录起，避免噪声
            var growth = QueryGrowth(top, minBytes, minPercent);
            if (growth.Count == 0 && !isFirstRun)
            {
                // 日常小幅变化场景下阈值往往过滤掉全部结果。
                // 此时自动降级为「不应用阈值」的第 top 名，报告里明确标注，避免整份报告失去意义。
                growth = QueryGrowth(top, 1, 0);
                result.GrowthFallbackUsed = growth.Count > 0;
            }
            result.GrowthTop.AddRange(growth);

            result.ShrinkTop.AddRange(QueryShrinks(top));
            result.NewTop.AddRange(QueryByKind("NEW", top));
            result.RemovedTop.AddRange(QueryByKind("REMOVED", top));

            var detail = QueryDetail(detailTop, detailMinBytes);
            if (detail.Count == 0)
            {
                detail = QueryDetail(detailTop, 1);
                result.DetailFallbackUsed = detail.Count > 0;
            }
            result.Detail.AddRange(detail);
        }
        finally
        {
            DropTempTable();
        }

        return result;
    }

    // ------------------------------------------------------------ 临时表构建

    /// <summary>
    /// 把两次快照的对比结果物化为临时表。
    /// 第一段处理「当前存在」的目录（含新增），第二段补齐「当前不存在且未被有意跳过」的消失目录。
    /// </summary>
    private void BuildTempTable(IReadOnlyList<ScanRoot> roots)
    {
        var p = new List<SQLiteParameter>();
        string filter = BuildRootFilter(roots, p);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"""
            DROP TABLE IF EXISTS temp.{TempTable};

            CREATE TEMP TABLE {TempTable} AS
            SELECT path, root_key, prev_bytes, curr_bytes, file_delta, kind, depth,
                   (CASE WHEN curr_bytes IS NULL THEN 0 ELSE curr_bytes END) - IFNULL(prev_bytes, 0) AS delta
            FROM (
                SELECT c.path                                          AS path,
                       c.root_key                                      AS root_key,
                       p.size_bytes                                    AS prev_bytes,
                       c.size_bytes                                    AS curr_bytes,
                       (c.file_count - IFNULL(p.file_count, 0))        AS file_delta,
                       CASE WHEN p.path IS NULL THEN 'NEW' ELSE 'BOTH' END AS kind,
                       c.depth                                         AS depth
                FROM dir_snapshots c
                LEFT JOIN dir_snapshots_prev p ON p.path = c.path
                WHERE c.root_key IN ({filter})

                UNION ALL

                SELECT p.path,
                       p.root_key,
                       p.size_bytes,
                       NULL,
                       -p.file_count,
                       'REMOVED',
                       p.depth
                FROM dir_snapshots_prev p
                WHERE p.root_key IN ({filter})
                  AND NOT EXISTS (SELECT 1 FROM dir_snapshots c WHERE c.path = p.path)
                  AND NOT EXISTS (SELECT 1 FROM skip_paths s WHERE s.path = p.path)
            );

            CREATE INDEX temp.idx_cmp_delta ON {TempTable}(delta);
            CREATE INDEX temp.idx_cmp_kind  ON {TempTable}(kind);
            CREATE INDEX temp.idx_cmp_depth ON {TempTable}(depth);
            """;
        foreach (var param in p) cmd.Parameters.Add(param);
        cmd.ExecuteNonQuery();
    }

    private void DropTempTable()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS temp.{TempTable};";
        cmd.ExecuteNonQuery();
    }

    private static string BuildRootFilter(IReadOnlyList<ScanRoot> roots, List<SQLiteParameter> ps)
    {
        if (roots.Count == 0) return "''";
        var names = new List<string>(roots.Count);
        for (int i = 0; i < roots.Count; i++)
        {
            string name = "$rk" + i;
            names.Add(name);
            ps.Add(new SQLiteParameter(name, roots[i].Key));
        }
        return string.Join(", ", names);
    }

    // ------------------------------------------------------------ 各根对比

    private List<RootComparison> QueryRootComparisons(IReadOnlyList<ScanRoot> roots, bool isFirstRun)
    {
        var p = new List<SQLiteParameter>();
        string filter = BuildRootFilter(roots, p);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT c.root_key, c.path, c.size_bytes, c.file_count, c.dir_count,
                   p.size_bytes, p.file_count
            FROM dir_snapshots c
            LEFT JOIN dir_snapshots_prev p ON p.path = c.path
            WHERE c.depth = 0 AND c.root_key IN ({filter});
            """;
        foreach (var param in p) cmd.Parameters.Add(param);

        var byKey = new Dictionary<string, RootComparison>(StringComparer.OrdinalIgnoreCase);
        // 展示名只存在于 ScanRoot（盘符根显示成「C: 盘」），数据库里没有这一列；
        // 查询到的行也必须回填，否则控制台「--- 各扫描根对比 ---」行首与 HTML 报告的
        // 「扫描根」单元格都是空的（2026-09-24 实测，属既有缺陷：这里过去从不赋值）。
        var labelByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots) labelByKey[root.Key] = root.DisplayName;

        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                string key = r.GetString(0);
                long curr = r.GetInt64(2);
                long? prev = r.IsDBNull(5) ? null : r.GetInt64(5);
                long growth = curr - (prev ?? 0);

                var rc = new RootComparison
                {
                    RootKey = key,
                    Path = r.GetString(1),
                    DisplayName = labelByKey.TryGetValue(key, out var label) ? label : key,
                    CurrBytes = curr,
                    CurrFiles = r.GetInt64(3),
                    CurrDirs = r.GetInt64(4),
                    PrevBytes = prev,
                    PrevFiles = r.IsDBNull(6) ? 0 : r.GetInt64(6),
                    GrowthBytes = prev == null ? curr : growth,
                    GrowthPercent = prev == null || prev == 0
                        ? double.NaN
                        : growth * 100.0 / prev.Value,
                    IsFirstRun = isFirstRun
                };

                if (rc.Path.Length >= 2 && rc.Path[1] == ':')
                {
                    string letter = rc.Path.Substring(0, 2).ToUpperInvariant();
                    var di = new DriveInfo(letter);
                    if (di.IsReady)
                    {
                        rc = CopyWithDriveInfo(rc, di.TotalSize, di.AvailableFreeSpace);
                    }
                }
                byKey[key] = rc;
            }
        }

        // 按用户给定顺序输出，保持报告稳定
        var list = new List<RootComparison>();
        foreach (var root in roots)
        {
            if (byKey.TryGetValue(root.Key, out var rc)) list.Add(rc);
            else
                list.Add(new RootComparison
                {
                    RootKey = root.Key,
                    Path = root.Path,
                    DisplayName = root.DisplayName,
                    IsFirstRun = isFirstRun
                });
        }
        return list;
    }

    private static RootComparison CopyWithDriveInfo(RootComparison src, long capacity, long free) => new()
    {
        RootKey = src.RootKey,
        Path = src.Path,
        DisplayName = src.DisplayName,
        CapacityBytes = capacity,
        FreeBytes = free,
        PrevBytes = src.PrevBytes,
        CurrBytes = src.CurrBytes,
        GrowthBytes = src.GrowthBytes,
        GrowthPercent = src.GrowthPercent,
        PrevFiles = src.PrevFiles,
        CurrFiles = src.CurrFiles,
        CurrDirs = src.CurrDirs,
        IsFirstRun = src.IsFirstRun
    };

    // ------------------------------------------------------------ 汇总统计

    private void QuerySummary(ChangeSummary s, IReadOnlyList<ScanRoot> roots)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT
              SUM(CASE WHEN kind = 'BOTH' AND delta > 0 THEN 1 ELSE 0 END),
              SUM(CASE WHEN kind = 'BOTH' AND delta < 0 THEN 1 ELSE 0 END),
              SUM(CASE WHEN kind = 'NEW' THEN 1 ELSE 0 END),
              SUM(CASE WHEN kind = 'REMOVED' THEN 1 ELSE 0 END)
            FROM {TempTable};
            """;
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read())
            {
                s.GrownCount = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                s.ShrunkCount = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                s.NewCount = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                s.RemovedCount = r.IsDBNull(3) ? 0 : r.GetInt64(3);
            }
        }

        // 因跳过（排除/权限）而无法比较的目录：上一轮存在、本轮缺失且已记录在跳过清单
        var p = new List<SQLiteParameter>();
        string filter = BuildRootFilter(roots, p);
        using var cmd2 = _db.Connection.CreateCommand();
        cmd2.CommandText = $"""
            SELECT COUNT(*), IFNULL(SUM(pr.size_bytes), 0)
            FROM dir_snapshots_prev pr
            WHERE pr.root_key IN ({filter})
              AND NOT EXISTS (SELECT 1 FROM dir_snapshots c WHERE c.path = pr.path)
              AND EXISTS (SELECT 1 FROM skip_paths s WHERE s.path = pr.path);
            """;
        foreach (var param in p) cmd2.Parameters.Add(param);
        using var r2 = cmd2.ExecuteReader();
        if (r2.Read())
        {
            s.SkippedCount = r2.GetInt64(0);
            s.SkippedBytes = r2.GetInt64(1);
        }
    }

    private long CountRows(string? where)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {TempTable}" +
                          (string.IsNullOrEmpty(where) ? ";" : $" WHERE {where};");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ------------------------------------------------------------ 榜单查询

    /// <summary>增长榜：按增长量降序。排除 depth=0 的根节点，避免根目录抢占全部位置。</summary>
    private List<GrowthItem> QueryGrowth(int top, long minBytes, double minPercent)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT path, root_key, prev_bytes, curr_bytes, file_delta, depth
            FROM {TempTable}
            WHERE kind <> 'REMOVED'
              AND depth > 0
              AND delta > 0
              AND delta >= $minBytes
              AND (IFNULL(prev_bytes, 0) = 0 OR delta * 100.0 / prev_bytes >= $minPct)
            ORDER BY delta DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$minBytes", minBytes);
        cmd.Parameters.AddWithValue("$minPct", minPercent);
        cmd.Parameters.AddWithValue("$n", top);
        return ReadItems(cmd);
    }

    /// <summary>缩减榜：按缩减量降序。</summary>
    private List<GrowthItem> QueryShrinks(int top)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT path, root_key, prev_bytes, curr_bytes, file_delta, depth
            FROM {TempTable}
            WHERE kind = 'BOTH' AND delta < 0
            ORDER BY delta ASC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", top);
        return ReadItems(cmd);
    }

    private List<GrowthItem> QueryByKind(string kind, int top)
    {
        using var cmd = _db.Connection.CreateCommand();
        string order = kind == "NEW" ? "IFNULL(curr_bytes,0) DESC" : "IFNULL(prev_bytes,0) DESC";
        cmd.CommandText = $"""
            SELECT path, root_key, prev_bytes, curr_bytes, file_delta, depth
            FROM {TempTable}
            WHERE kind = $kind AND depth > 0
            ORDER BY {order}
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$n", top);
        return ReadItems(cmd);
    }

    /// <summary>
    /// 详细明细：所有变化量超过阈值的目录，按变化量绝对值降序，最多 detailTop 行。
    /// </summary>
    private List<GrowthItem> QueryDetail(int detailTop, long detailMinBytes)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT path, root_key, prev_bytes, curr_bytes, file_delta, depth, kind, delta
            FROM {TempTable}
            WHERE ABS(delta) >= $min
            ORDER BY ABS(delta) DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$min", detailMinBytes);
        cmd.Parameters.AddWithValue("$n", detailTop);

        var list = new List<GrowthItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MakeItem(r));
        return list;
    }

    private static List<GrowthItem> ReadItems(SQLiteCommand cmd)
    {
        var list = new List<GrowthItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MakeItem(r));
        return list;
    }

    /// <summary>
    /// 由两侧占用值推导状态，保证同一目录在不同榜单中的状态一致：
    /// 上次不存在 → 新增；本次不存在 → 消失；否则按变化量正负判定增长/缩减/无变化。
    /// </summary>
    private static GrowthItem MakeItem(SQLiteDataReader r)
    {
        long? prev = r.IsDBNull(2) ? null : r.GetInt64(2);
        long? curr = r.IsDBNull(3) ? null : r.GetInt64(3);
        long delta = (curr ?? 0) - (prev ?? 0);

        double pct = double.NaN;
        if (prev.HasValue && prev.Value != 0)
            pct = delta * 100.0 / prev.Value;

        GrowthStatus status =
            prev == null ? GrowthStatus.New :
            curr == null ? GrowthStatus.Removed :
            delta > 0 ? GrowthStatus.Grown :
            delta < 0 ? GrowthStatus.Shrunk :
            GrowthStatus.Unchanged;

        return new GrowthItem
        {
            Path = r.GetString(0),
            RootKey = r.GetString(1),
            PrevBytes = prev,
            CurrBytes = curr,
            GrowthBytes = delta,
            GrowthPercent = pct,
            FileCountDelta = r.IsDBNull(4) ? 0 : r.GetInt64(4),
            Depth = r.FieldCount > 5 && !r.IsDBNull(5) ? r.GetInt32(5) : 0,
            Status = status
        };
    }
}
