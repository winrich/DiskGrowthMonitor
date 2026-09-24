using DiskGrowthMonitor.Models;
using DiskGrowthMonitor.Util;
using System.Data;
using System.Data.SQLite;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// SQLite 持久化服务。
///
/// 快照更新采用「临时表 + 两阶段提交」，保证任意时刻数据库中至少保留一份完整快照：
///   阶段一：把当前 dir_snapshots 中待重扫根的数据复制到 dir_snapshots_prev（作为「上一轮」基准）
///   阶段二：扫描结果全部写入临时表 dir_snapshots_stage
///   阶段三：单个事务内完成 删除旧数据 → 迁入临时表 → 写扫描批次 → 落增长历史
/// 因此即使扫描中途崩溃或断电，dir_snapshots 仍保持上一轮完整数据，不会出现数据真空。
/// </summary>
public sealed class DatabaseService : IDisposable
{
    private const string StageTable = "dir_snapshots_stage";

    private readonly SQLiteConnection _conn;

    private SQLiteTransaction? _batchTx;
    private SQLiteCommand? _stageInsertCmd;

    public string DatabasePath { get; }

    /// <summary>
    /// 最近一次 <see cref="CommitStaging"/> 清理掉的「不属于本轮任何扫描根」的旧快照行数。
    ///
    /// 仅供调用方打印提示用（快照表已改为整表重建，只保留最近一轮）。正常情况下它只在
    /// 「上一轮的扫描根集合与本轮不同」时非 0 —— 例如先用 <c>-d c,d</c> 跑过一轮、之后
    /// 一直只跑 <c>-d c</c>，则 D 盘的旧行会在这轮被清掉（2026-09-24 实测残量 144,296 行）。
    /// </summary>
    public long LastStaleRowsPurged { get; private set; }

    /// <summary>
    /// 目录行路径前缀匹配键。根路径已是 <c>"C:\"</c>（盘符根）时原样返回，否则补一个分隔符。
    ///
    /// 🔴 旧实现无条件拼 <c>"\\"</c>：盘符根变成 <c>"C:\\"</c>，而 <c>substr(path,1,4)</c>
    /// 对任何正常路径（如 <c>C:\foo</c>）都取到 <c>C:\f</c>，永远匹配不上 ——
    /// 后果是 CommitStaging 清不掉「上一轮用 -r 扫子目录」留下的旧 root_key 行，
    /// 主键冲突直接崩（exit 127）；TryReuseSubtree 里则表现为整盘根的增量复用静默失效。
    /// BeginPrevSnapshot / CountPrevRows / TryReuseSubtree / CommitStaging 四处共用。
    /// </summary>
    private static string PrefixOf(string path)
        => path.EndsWith("\\", StringComparison.Ordinal) ? path : path + "\\";

    /// <summary>
    /// 前缀范围查询的**上界**：递增最后一个字符。供 <see cref="TryReuseSubtree"/> 把
    /// <c>substr(path,1,len) = prefix</c> 这种「函数调用、索引用不上」的写法换成
    /// <c>path &gt;= p AND path &lt; hi</c>，让 SQLite 走主键索引的 SEARCH。
    ///
    /// 🔴 为什么值得这么绕：`TryReuseSubtree` 在整卷根上会被「每个干净子目录」各调用一次
    /// （实测一轮约 4,200 次），而旧写法每次都是对 prev 的全表扫描（192k 行 ≈ 60 ms）
    /// ⇒ 累计 ≈250 s，比全量扫描还慢一个数量级。换成范围后单次 0.1~8 ms。
    ///
    /// ⚠ 上界必须递增 <paramref name="path"/> 的**末字符**，不是递增末尾的分隔符：
    /// 子树里的路径在「末字符」那一位与 path 相同、要到更靠后的一位才分叉，故必定小于
    /// 「末字符 + 1」；若改成递增分隔符，像 <c>C:\^foo</c> 这种「分隔符后首字符 &gt; ']'」
    /// 的路径会**越过上界被静默漏掉**。
    ///
    /// 注意范围只是**超集**（如 <c>…\AppDataX</c> 也落在界内），调用处必须保留残余的
    /// <c>substr</c> 复核才能保证逐行等价。
    /// </summary>
    private static string PrefixUpperBound(string path)
    {
        char last = path[path.Length - 1];
        if (last == char.MaxValue)
        {
            // 末字符已是最大码位（实际不可能出现）：退化为「path + 最大字符」。
            // 子树路径紧跟其后的必定是分隔符（0x5C < 0xEF…），故仍是合法上界。
            return path + char.MaxValue;
        }
        return path.Substring(0, path.Length - 1) + (char)(last + 1);
    }

    public DatabaseService(string dbPath)
    {
        DatabasePath = Path.GetFullPath(dbPath);
        string? dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // 连接字符串：System.Data.SQLite 的属性名与 Microsoft.Data.Sqlite 不同，对应关系：
        //   M.D.S 的 Mode=ReadWriteCreate  →  S.D.S 的 FailIfMissing=false（缺库时自动建）
        //   M.D.S 的 Cache=Private         →  S.D.S 无此属性；其默认即为 private cache
        //                                     （未启用共享缓存），行为等价，故不再显式设置
        // 同 M.D.S 一样不启用连接池（S.D.S 默认 Pooling=true，与 M.D.S 默认一致，保持不动）。
        var csb = new SQLiteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            FailIfMissing = false
        };

        _conn = new SQLiteConnection(csb.ToString());
        _conn.Open();
        ApplyPragmas();
        EnsureSchema();
    }

    private void ApplyPragmas()
    {
        using var cmd = _conn.CreateCommand();
        // WAL：批量写入快得多，且崩溃恢复更可靠
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteScalar();

        cmd.CommandText = "PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();

        // 对比阶段会为百万级目录建临时表做排序，用 FILE 存储避免内存被撑爆
        cmd.CommandText = "PRAGMA temp_store=FILE;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "PRAGMA foreign_keys=OFF;";
        cmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        const string ddl = """
        CREATE TABLE IF NOT EXISTS scan_runs (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            scan_time     TEXT    NOT NULL,
            roots         TEXT    NOT NULL,
            total_files   INTEGER NOT NULL,
            total_dirs    INTEGER NOT NULL,
            total_bytes   INTEGER NOT NULL,
            elapsed_ms    INTEGER NOT NULL,
            is_first_run  INTEGER NOT NULL DEFAULT 0
        );

        -- 目录当前快照：每个目录一行，只保留最近一次扫描结果
        CREATE TABLE IF NOT EXISTS dir_snapshots (
            path        TEXT    NOT NULL PRIMARY KEY,
            root_key    TEXT    NOT NULL,
            depth       INTEGER NOT NULL,
            size_bytes  INTEGER NOT NULL,
            file_count  INTEGER NOT NULL,
            dir_count   INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_snap_root  ON dir_snapshots(root_key);
        CREATE INDEX IF NOT EXISTS idx_snap_depth ON dir_snapshots(depth);
        CREATE INDEX IF NOT EXISTS idx_snap_size  ON dir_snapshots(size_bytes);

        -- 上一轮快照：仅保存本次待重扫根的上一版数据，每次扫描前重建
        CREATE TABLE IF NOT EXISTS dir_snapshots_prev (
            path        TEXT    NOT NULL PRIMARY KEY,
            root_key    TEXT    NOT NULL,
            depth       INTEGER NOT NULL DEFAULT 0,
            size_bytes  INTEGER NOT NULL,
            file_count  INTEGER NOT NULL,
            dir_count   INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_prev_root ON dir_snapshots_prev(root_key);

        -- 增长对比历史：每轮进入报告的增长项落库，便于长期回溯
        CREATE TABLE IF NOT EXISTS growth_history (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id            INTEGER NOT NULL,
            path              TEXT    NOT NULL,
            root_key          TEXT    NOT NULL,
            prev_bytes        INTEGER,
            curr_bytes        INTEGER,
            growth_bytes      INTEGER NOT NULL,
            growth_percent    REAL,
            status            TEXT    NOT NULL,
            file_count_delta  INTEGER
        );
        CREATE INDEX IF NOT EXISTS idx_growth_run ON growth_history(run_id, growth_bytes DESC);

        -- 跳过清单：默认排除、自定义排除、权限不足、IO 错误、符号链接
        -- 其中 ACCESS_DENIED / IO_ERROR 会在下次扫描时被直接跳过，避免重复撞权限墙
        CREATE TABLE IF NOT EXISTS skip_paths (
            path           TEXT    NOT NULL PRIMARY KEY,
            root_key       TEXT    NOT NULL,
            reason         TEXT    NOT NULL,
            detail         TEXT,
            first_seen_run INTEGER NOT NULL,
            last_seen_run  INTEGER NOT NULL,
            hit_count      INTEGER NOT NULL DEFAULT 1
        );
        CREATE INDEX IF NOT EXISTS idx_skip_reason ON skip_paths(reason);

        -- 卷级 USN 增量状态：记录各 NTFS 卷上次扫描对应的 USN 日志位置
        CREATE TABLE IF NOT EXISTS volume_state (
            volume      TEXT    NOT NULL PRIMARY KEY,
            journal_id  INTEGER NOT NULL,
            next_usn    INTEGER NOT NULL,
            run_id      INTEGER NOT NULL,
            updated     TEXT    NOT NULL
        );

        -- 目录用途知识库：给报告中的已知目录附上用途说明（浏览器缓存 / 系统日志 / 临时文件…）
        -- 属「配置数据」而非业务数据：ResetAll() 不会清空本表。
        -- builtin=1 的内置条目仅在 pattern 缺失时补齐，绝不覆盖用户对已有条目的修改。
        -- category_en / title_en / note_en：用途说明是「数据」而不是提示文本，不随界面语言自动翻译，
        -- 因此自带英文列；英文档取英文列、缺失则回退中文（见 Models/DirKnowledge.Pick）。
        -- 老库由 MigrateSchema 补列、SeedKnowledge 只对「英文列为空」的行回填，同样不覆盖用户改动。
        CREATE TABLE IF NOT EXISTS dir_knowledge (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            pattern     TEXT    NOT NULL UNIQUE,
            category    TEXT    NOT NULL,
            title       TEXT    NOT NULL,
            note        TEXT,
            cleanable   TEXT,
            priority    INTEGER NOT NULL DEFAULT 50,
            builtin     INTEGER NOT NULL DEFAULT 1,
            enabled     INTEGER NOT NULL DEFAULT 1,
            source      TEXT,
            updated     TEXT    NOT NULL,
            category_en TEXT,
            title_en    TEXT,
            note_en     TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_knowledge_cat ON dir_knowledge(category);
        """;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();

        MigrateSchema();
    }

    /// <summary>
    /// 轻量迁移：为早期版本创建的库补齐新列。
    /// dir_snapshots_prev 每次扫描前都会被整体重建，因此补列时无需回填历史值。
    /// </summary>
    private void MigrateSchema()
    {
        if (!ColumnExists("dir_snapshots_prev", "depth"))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE dir_snapshots_prev ADD COLUMN depth INTEGER NOT NULL DEFAULT 0;";
            cmd.ExecuteNonQuery();
        }

        // 知识库英文列（第 57 轮）。老库的 dir_knowledge 已存在，CREATE TABLE IF NOT EXISTS 不会补列，
        // 必须显式 ALTER。这里只加列、不回填 —— 回填由 SeedKnowledge 按「英文列为空」逐条补，
        // 这样既能把内置条目英文补齐，又绝不会覆盖用户自己填过的英文。
        AddColumnIfMissing("dir_knowledge", "category_en", "TEXT");
        AddColumnIfMissing("dir_knowledge", "title_en", "TEXT");
        AddColumnIfMissing("dir_knowledge", "note_en", "TEXT");
    }

    /// <summary>补列（已存在则不动）。表名/列名均为代码内常量，不做转义。</summary>
    private void AddColumnIfMissing(string table, string column, string type)
    {
        if (ColumnExists(table, column)) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        cmd.ExecuteNonQuery();
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- 批次

    /// <summary>读取最近一次扫描批次（用于报告的「上次扫描时间」）。</summary>
    public ScanRun? GetLatestRun()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, scan_time, roots, total_files, total_dirs, total_bytes, elapsed_ms, is_first_run
            FROM scan_runs ORDER BY id DESC LIMIT 1
            """;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ScanRun
        {
            Id = r.GetInt64(0),
            ScanTime = DateTime.Parse(r.GetString(1)),
            Roots = r.GetString(2),
            TotalFiles = r.GetInt64(3),
            TotalDirs = r.GetInt64(4),
            TotalBytes = r.GetInt64(5),
            ElapsedMs = r.GetInt64(6),
            IsFirstRun = r.GetInt64(7) != 0
        };
    }

    // ------------------------------------------------- 阶段一：建立上一轮基准

    /// <summary>
    /// 把当前快照中待重扫根的上一版数据固化到 dir_snapshots_prev。
    /// 必须在扫描开始前调用。返回复制的行数（0 表示首次运行）。
    /// </summary>
    public long BeginPrevSnapshot(IReadOnlyList<ScanRoot> roots)
    {
        using var tx = _conn.BeginTransaction();

        // prev 只保存本次要重扫的根，其他根的历史数据仍完整保留在 dir_snapshots 中
        using (var del = _conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM dir_snapshots_prev;";
            del.ExecuteNonQuery();
        }

        long copied = 0;
        foreach (var root in roots)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            // dir_count 必须一并复制：TryReuseSubtree 读它来回填「复用的目录数」。漏掉这一列
            // 会让 prev 里的 dir_count 停在 DEFAULT 0 ⇒ 复用子树返回 Dirs=0 ⇒ 总目录数少算
            // （整根复用时会显示成「1 个目录」），而且复用行会以 0 写回新快照 ⇒ 每复用一轮
            // 归零一次、不可恢复，报告里的目录数也跟着错（2026-09-24 实测）。
            cmd.CommandText = """
                INSERT OR REPLACE INTO dir_snapshots_prev(path, root_key, depth, size_bytes, file_count, dir_count)
                SELECT path, root_key, depth, size_bytes, file_count, dir_count
                FROM dir_snapshots
                WHERE root_key = $key OR path = $path OR substr(path, 1, $plen) = $prefix;
                """;
            cmd.Parameters.AddWithValue("$key", root.Key);
            cmd.Parameters.AddWithValue("$path", root.Path);
            cmd.Parameters.AddWithValue("$plen", PrefixOf(root.Path).Length);
            cmd.Parameters.AddWithValue("$prefix", PrefixOf(root.Path));
            copied += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return copied;
    }

    /// <summary>上一轮基线中的目录行数（用于判断是否首次运行）。</summary>
    public long CountPrevSnapshot()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dir_snapshots_prev;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>上一轮快照中属于指定扫描根的行数（判断该根是否有可对比/可复用的基准）。</summary>
    public long CountPrevRows(ScanRoot root)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM dir_snapshots_prev
            WHERE root_key = $key OR path = $path OR substr(path, 1, $plen) = $prefix;
            """;
        cmd.Parameters.AddWithValue("$key", root.Key);
        cmd.Parameters.AddWithValue("$path", root.Path);
        cmd.Parameters.AddWithValue("$plen", PrefixOf(root.Path).Length);
        cmd.Parameters.AddWithValue("$prefix", PrefixOf(root.Path));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // -------------------------------------------------------- USN 卷状态

    /// <summary>某卷上次保存的 USN 日志状态。</summary>
    public sealed record VolumeStateInfo(ulong JournalId, long NextUsn, long RunId);

    public VolumeStateInfo? GetVolumeState(char letter)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT journal_id, next_usn, run_id FROM volume_state WHERE volume = $v;";
        cmd.Parameters.AddWithValue("$v", letter.ToString());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new VolumeStateInfo(unchecked((ulong)r.GetInt64(0)), r.GetInt64(1), r.GetInt64(2));
    }

    public void SaveVolumeState(char letter, ulong journalId, long nextUsn, long runId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO volume_state(volume, journal_id, next_usn, run_id, updated)
            VALUES ($v, $j, $n, $r, $u)
            ON CONFLICT(volume) DO UPDATE SET
                journal_id = excluded.journal_id,
                next_usn   = excluded.next_usn,
                run_id     = excluded.run_id,
                updated    = excluded.updated;
            """;
        cmd.Parameters.AddWithValue("$v", letter.ToString());
        cmd.Parameters.AddWithValue("$j", unchecked((long)journalId));
        cmd.Parameters.AddWithValue("$n", nextUsn);
        cmd.Parameters.AddWithValue("$r", runId);
        cmd.Parameters.AddWithValue("$u", FormatUtil.Timestamp(DateTime.Now));
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------- 增量复用：prev → stage

    /// <summary>
    /// 尝试复用 dirPath 的整个子树：上一轮快照存在该目录时，把其子树全部行复制进暂存表。
    /// 仅在扫描期间（暂存表存在时）由扫描线程经 StageWriter 串行调用。
    /// </summary>
    /// <param name="currentDepth">该目录在本次扫描中的层级，用于校正 depth 偏移（上次扫描根可能不同）。</param>
    /// <param name="stat">该目录自身的子树统计（来自上一轮快照行）。</param>
    public bool TryReuseSubtree(string rootKey, string dirPath, int currentDepth, out DirStat stat)
    {
        stat = default;

        int offset;
        long size, files, dirs;
        {
            using var get = _conn.CreateCommand();
            get.CommandText = """
                SELECT depth, size_bytes, file_count, dir_count
                FROM dir_snapshots_prev WHERE path = $p;
                """;
            get.Parameters.AddWithValue("$p", dirPath);
            using var r = get.ExecuteReader();
            if (!r.Read()) return false;
            offset = currentDepth - r.GetInt32(0);
            size = r.GetInt64(1);
            files = r.GetInt64(2);
            dirs = r.GetInt64(3);
        }

        using var copy = _conn.CreateCommand();
        // 🔴 前缀匹配一律走「范围谓词 + 残余复核」，不要用 substr(path,1,len)=prefix：
        // substr 是函数调用 ⇒ 主键索引用不上 ⇒ 每次调用全表扫描 prev（192k 行 ≈ 60 ms）。
        // 而本方法在整卷根上会被「每个干净子目录」各调用一次（实测一轮约 4,200 次）
        // ⇒ 累计 ≈250 s，比全量扫描（21 s）还慢一个数量级。
        // 改成 path >= $p AND path < $hi 后 SQLite 走 SEARCH ... USING COVERING INDEX；
        // 范围给出的是超集（"…\AppDataX" 之类也会落在界内），故必须保留 substr 复核，
        // 由它保证与旧写法**逐行等价**（已用 6 个样本逐行集对照验证，含 C:\ 与 13.3 万行的 C:\Windows）。
        copy.CommandText = """
            INSERT INTO dir_snapshots_stage(path, root_key, depth, size_bytes, file_count, dir_count)
            SELECT path, $rk, depth + $off, size_bytes, file_count, dir_count
            FROM dir_snapshots_prev
            WHERE path >= $p AND path < $hi
              AND (path = $p OR substr(path, 1, $plen) = $prefix);
            """;
        copy.Parameters.AddWithValue("$rk", rootKey);
        copy.Parameters.AddWithValue("$off", offset);
        copy.Parameters.AddWithValue("$p", dirPath);
        copy.Parameters.AddWithValue("$hi", PrefixUpperBound(dirPath));
        copy.Parameters.AddWithValue("$plen", PrefixOf(dirPath).Length);
        copy.Parameters.AddWithValue("$prefix", PrefixOf(dirPath));
        copy.ExecuteNonQuery();

        stat = new DirStat(size, files, dirs);
        return true;
    }

    // --------------------------------------------- 阶段二：扫描结果写临时表

    /// <summary>创建（重建）扫描结果暂存表。</summary>
    public void CreateStageTable()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            DROP TABLE IF EXISTS {StageTable};
            CREATE TABLE {StageTable} (
                path        TEXT    NOT NULL,
                root_key    TEXT    NOT NULL,
                depth       INTEGER NOT NULL,
                size_bytes  INTEGER NOT NULL,
                file_count  INTEGER NOT NULL,
                dir_count   INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>批量写入暂存表。整个批次共用一个事务，显著提升写入吞吐。</summary>
    public void InsertStageBatch(IReadOnlyList<DirSnapshot> batch)
    {
        if (batch.Count == 0) return;

        _batchTx ??= _conn.BeginTransaction();
        if (_stageInsertCmd == null)
        {
            _stageInsertCmd = _conn.CreateCommand();
            _stageInsertCmd.Transaction = _batchTx;
            _stageInsertCmd.CommandText = $"""
                INSERT INTO {StageTable}(path, root_key, depth, size_bytes, file_count, dir_count)
                VALUES ($path, $root, $depth, $size, $files, $dirs);
                """;
            _stageInsertCmd.Parameters.Add("$path", DbType.String);
            _stageInsertCmd.Parameters.Add("$root", DbType.String);
            _stageInsertCmd.Parameters.Add("$depth", DbType.Int64);
            _stageInsertCmd.Parameters.Add("$size", DbType.Int64);
            _stageInsertCmd.Parameters.Add("$files", DbType.Int64);
            _stageInsertCmd.Parameters.Add("$dirs", DbType.Int64);
            _stageInsertCmd.Prepare();
        }

        var p = _stageInsertCmd.Parameters;
        foreach (var s in batch)
        {
            p["$path"].Value = s.Path;
            p["$root"].Value = s.RootKey;
            p["$depth"].Value = s.Depth;
            p["$size"].Value = s.SizeBytes;
            p["$files"].Value = s.FileCount;
            p["$dirs"].Value = s.DirCount;
            _stageInsertCmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------ 阶段三：单事务提交并轮换快照

    /// <summary>
    /// 单个事务内完成：清空旧快照（整表重建）→ 迁入暂存表 → 删除暂存表 → 写入扫描批次。
    /// 返回新批次的 id。被清掉的「非本轮扫描根」残留行数见 <see cref="LastStaleRowsPurged"/>。
    /// </summary>
    public long CommitStaging(IReadOnlyList<ScanRoot> roots, ScanRun run)
    {
        _stageInsertCmd?.Dispose();
        _stageInsertCmd = null;
        _batchTx?.Commit();
        _batchTx?.Dispose();
        _batchTx = null;

        using var tx = _conn.BeginTransaction();

        // 1) 整表清空后整体重建：dir_snapshots 的语义是「**只保存最近一轮**的扫描结果」。
        //
        // 🔴 为什么不能像旧实现那样「只删本轮根范围内的旧行」：旧写法会让其它扫描根的历史行
        // 永久驻留（本意是「留住其它盘的历史」），但 dir_snapshots 里没有任何列标记这些行的
        // 轮次 ⇒ BeginPrevSnapshot 在下一轮扫到那个根时会把它们当成「上一轮基准」捞出来，
        // 而它们实际来自若干轮之前 —— 报告于是声称「与上一轮对比」，报出来的却是跨越 N 轮的
        // 累积增长（2026-09-24 实测：只扫 C 的库里躺着 144,296 行 D 盘旧行，来自更早一轮的
        // -d c,d）。整表重建后「上一轮」必然是真的上一轮；代价是多盘轮换使用时，切回某个盘的
        // 那一轮会被判为「首次运行」（无基准）—— 这是**诚实**的行为，拿旧行冒充基准才是错的。
        //
        // 安全性：stage 表即本轮全部扫描结果（含增量复用复制进来的行），故整体重建不会丢数据。
        // 原「按根+路径前缀删」的用途（覆盖「上轮 -r 扫子目录、本轮 -d 扫整盘」的根变化、避免
        // 同路径两条 root_key 冲突）本就包含在整表清空里。
        long staleRows = 0;
        if (roots.Count > 0)
        {
            // 先数出「不属于本轮任何扫描根」的残留行：仅用于向用户交代清理量（逻辑上整表都要重建）。
            // 判据必须与 BeginPrevSnapshot 的取行口径一致，否则同一盘换根（如 -r C:\Users → -d c）
            // 造成的残留会被漏报。roots 为空时不做此查询（谓词会退化成空串导致语法错）。
            var ors = new List<string>();
            using var cnt = _conn.CreateCommand();
            cnt.Transaction = tx;
            for (int i = 0; i < roots.Count; i++)
            {
                ors.Add($"(root_key = $k{i} OR path = $p{i} OR substr(path, 1, $l{i}) = $x{i})");
                cnt.Parameters.AddWithValue($"$k{i}", roots[i].Key);
                cnt.Parameters.AddWithValue($"$p{i}", roots[i].Path);
                cnt.Parameters.AddWithValue($"$l{i}", PrefixOf(roots[i].Path).Length);
                cnt.Parameters.AddWithValue($"$x{i}", PrefixOf(roots[i].Path));
            }
            cnt.CommandText = $"SELECT COUNT(*) FROM dir_snapshots WHERE NOT ({string.Join(" OR ", ors)});";
            staleRows = Convert.ToInt64(cnt.ExecuteScalar());
        }
        LastStaleRowsPurged = staleRows;

        using (var del = _conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM dir_snapshots;";
            del.ExecuteNonQuery();
        }

        // 2) 迁入本轮扫描结果
        using (var move = _conn.CreateCommand())
        {
            move.Transaction = tx;
            move.CommandText = $"""
                INSERT INTO dir_snapshots(path, root_key, depth, size_bytes, file_count, dir_count)
                SELECT path, root_key, depth, size_bytes, file_count, dir_count FROM {StageTable};
                """;
            move.ExecuteNonQuery();
        }

        // 3) 清理暂存表
        using (var drop = _conn.CreateCommand())
        {
            drop.Transaction = tx;
            drop.CommandText = $"DROP TABLE IF EXISTS {StageTable};";
            drop.ExecuteNonQuery();
        }

        // 4) 写入本次扫描批次
        using (var ins = _conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO scan_runs(scan_time, roots, total_files, total_dirs, total_bytes, elapsed_ms, is_first_run)
                VALUES ($t, $r, $f, $d, $b, $e, $first);
                SELECT last_insert_rowid();
                """;
            ins.Parameters.AddWithValue("$t", FormatUtil.Timestamp(run.ScanTime));
            ins.Parameters.AddWithValue("$r", run.Roots);
            ins.Parameters.AddWithValue("$f", run.TotalFiles);
            ins.Parameters.AddWithValue("$d", run.TotalDirs);
            ins.Parameters.AddWithValue("$b", run.TotalBytes);
            ins.Parameters.AddWithValue("$e", run.ElapsedMs);
            ins.Parameters.AddWithValue("$first", run.IsFirstRun ? 1 : 0);
            run.Id = Convert.ToInt64(ins.ExecuteScalar());
        }

        tx.Commit();
        return run.Id;
    }

    // ------------------------------------------------------------ 跳过清单

    /// <summary>读取全部跳过清单记录。</summary>
    public List<SkipRecord> GetSkipRecords()
    {
        var list = new List<SkipRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT path, root_key, reason, detail, hit_count
            FROM skip_paths ORDER BY reason, path;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SkipRecord
            {
                Path = r.GetString(0),
                RootKey = r.GetString(1),
                Reason = SkipRecord.ParseReason(r.GetString(2)),
                Detail = r.IsDBNull(3) ? null : r.GetString(3),
                ExistingHitCount = r.GetInt32(4)
            });
        }
        return list;
    }

    /// <summary>
    /// 合并写入本轮跳过记录：已存在的累加命中次数与最后出现批次，新出现的插入。
    /// </summary>
    public void SaveSkipRecords(IEnumerable<SkipRecord> records, long runId)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO skip_paths(path, root_key, reason, detail, first_seen_run, last_seen_run, hit_count)
            VALUES ($path, $root, $reason, $detail, $run, $run, $hits)
            ON CONFLICT(path) DO UPDATE SET
                reason        = excluded.reason,
                detail        = excluded.detail,
                last_seen_run = excluded.last_seen_run,
                hit_count     = skip_paths.hit_count + excluded.hit_count;
            """;
        var p = cmd.Parameters;
        p.Add("$path", DbType.String);
        p.Add("$root", DbType.String);
        p.Add("$reason", DbType.String);
        p.Add("$detail", DbType.String);
        p.Add("$run", DbType.Int64);
        p.Add("$hits", DbType.Int64);

        foreach (var rec in records)
        {
            p["$path"].Value = rec.Path;
            p["$root"].Value = rec.RootKey;
            p["$reason"].Value = SkipRecord.ReasonCode(rec.Reason);
            p["$detail"].Value = (object?)rec.Detail ?? DBNull.Value;
            p["$run"].Value = runId;
            p["$hits"].Value = Math.Max(1, rec.ExistingHitCount);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>清空「权限不足 / IO 错误」跳过记录，让下次扫描重新尝试这些目录。</summary>
    public int ResetPersistentSkips()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM skip_paths WHERE reason IN ('ACCESS_DENIED','IO_ERROR');";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>重置结果：各表被清掉的行数（用于打印摘要）。</summary>
    public sealed record ResetSummary(
        long ScanRuns, long Snapshots, long PrevSnapshots,
        long GrowthHistory, long SkipPaths, long VolumeStates)
    {
        public long Total => ScanRuns + Snapshots + PrevSnapshots + GrowthHistory + SkipPaths + VolumeStates;
    }

    /// <summary>
    /// 把数据库重置回「从未扫描」状态：清空全部业务表（批次/快照/上一轮快照/对比历史/
    /// 跳过清单/USN 状态），删除扫描暂存表，并把 AUTOINCREMENT 序号复位到 1。
    /// 全过程在单个事务内完成（失败整体回滚）；表结构保留，不需要删除库文件。
    /// </summary>
    public ResetSummary ResetAll()
    {
        // 注意：dir_knowledge（目录用途知识库）属配置数据，**不在**清空范围内。
        // 重置只针对扫描业务数据，知识库（含用户自定义条目）必须原样保留。
        string[] tables =
        {
            "scan_runs", "dir_snapshots", "dir_snapshots_prev",
            "growth_history", "skip_paths", "volume_state"
        };

        using var tx = _conn.BeginTransaction();

        long CountOf(string table)
        {
            using var c = _conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = $"SELECT COUNT(*) FROM {table};";
            return Convert.ToInt64(c.ExecuteScalar());
        }

        var before = new ResetSummary(
            CountOf("scan_runs"), CountOf("dir_snapshots"), CountOf("dir_snapshots_prev"),
            CountOf("growth_history"), CountOf("skip_paths"), CountOf("volume_state"));

        foreach (var t in tables)
        {
            using var del = _conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {t};";
            del.ExecuteNonQuery();
        }

        // 复位自增序号：让下次扫描的批次号重新从 #1 开始（sqlite_sequence 表可能不存在）
        using (var seq = _conn.CreateCommand())
        {
            seq.Transaction = tx;
            seq.CommandText = "DELETE FROM sqlite_sequence WHERE name IN ('scan_runs','growth_history');";
            try { seq.ExecuteNonQuery(); }
            catch (SQLiteException) { /* 无 AUTOINCREMENT 表时该表不存在，忽略 */ }
        }

        // 上次扫描中途中断时可能残留暂存表，一并清掉
        using (var stage = _conn.CreateCommand())
        {
            stage.Transaction = tx;
            stage.CommandText = $"DROP TABLE IF EXISTS {StageTable};";
            stage.ExecuteNonQuery();
        }

        tx.Commit();
        return before;
    }

    // ------------------------------------------------------ 目录用途知识库

    /// <summary>
    /// 补齐缺失的内置知识条目（<c>INSERT OR IGNORE</c>）。
    /// <b>只新增、不修改也不删除已有条目</b>：用户对内置条目的任何编辑（改说明、调优先级、停用）
    /// 以及自行新增的条目，在程序升级后都会原样保留。返回本次实际插入的条数。
    ///
    /// <para><b>英文列的两步处理（第 57 轮）</b>：
    /// ① 新插入的条目连同 <c>category_en/title_en/note_en</c> 一次写入；
    /// ② 老库里<b>早已存在</b>的内置条目英文列为 NULL，用一条「仅当英文列为空才写」的 UPDATE 回填。
    /// 回填的判定条件刻意是「英文列为空」而不是「builtin=1」：用户可能自己填过英文，那就必须让位，
    /// 否则程序一升级就把人家的翻译冲掉 —— 这与本方法「绝不覆盖用户修改」的一贯策略一致。</para>
    /// </summary>
    public int SeedKnowledge(IEnumerable<DirKnowledge> entries)
    {
        string stamp = FormatUtil.Timestamp(DateTime.Now);
        int inserted = 0;

        using var tx = _conn.BeginTransaction();

        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO dir_knowledge
                    (pattern, category, title, note, cleanable, priority, builtin, enabled, source, updated,
                     category_en, title_en, note_en)
                VALUES ($pattern, $category, $title, $note, $cleanable, $priority, 1, 1, $source, $updated,
                        $categoryEn, $titleEn, $noteEn);
                """;
            var p = cmd.Parameters;
            p.Add("$pattern", DbType.String);
            p.Add("$category", DbType.String);
            p.Add("$title", DbType.String);
            p.Add("$note", DbType.String);
            p.Add("$cleanable", DbType.String);
            p.Add("$priority", DbType.Int64);
            p.Add("$source", DbType.String);
            p.Add("$updated", DbType.String);
            p.Add("$categoryEn", DbType.String);
            p.Add("$titleEn", DbType.String);
            p.Add("$noteEn", DbType.String);

            foreach (var e in entries)
            {
                p["$pattern"].Value = e.Pattern;
                p["$category"].Value = e.Category;
                p["$title"].Value = e.Title;
                p["$note"].Value = (object?)e.Note ?? DBNull.Value;
                p["$cleanable"].Value = (object?)e.Cleanable ?? DBNull.Value;
                p["$priority"].Value = e.Priority;
                p["$source"].Value = (object?)e.Source ?? DBNull.Value;
                p["$updated"].Value = stamp;
                p["$categoryEn"].Value = (object?)e.CategoryEn ?? DBNull.Value;
                p["$titleEn"].Value = (object?)e.TitleEn ?? DBNull.Value;
                p["$noteEn"].Value = (object?)e.NoteEn ?? DBNull.Value;
                inserted += cmd.ExecuteNonQuery();
            }
        }

        // 老库回填：只补「英文列为空」的行。中文列与其它字段一律不碰。
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE dir_knowledge
                   SET category_en = $categoryEn, title_en = $titleEn, note_en = $noteEn
                 WHERE pattern = $pattern
                   AND (title_en IS NULL OR title_en = '');
                """;
            var p = cmd.Parameters;
            p.Add("$pattern", DbType.String);
            p.Add("$categoryEn", DbType.String);
            p.Add("$titleEn", DbType.String);
            p.Add("$noteEn", DbType.String);

            foreach (var e in entries)
            {
                // 该条目本来就没有英文（用户自定义未填）⇒ 不写空值，避免把列从 NULL 变成 ''
                if (string.IsNullOrEmpty(e.TitleEn)) continue;
                p["$pattern"].Value = e.Pattern;
                p["$categoryEn"].Value = (object?)e.CategoryEn ?? DBNull.Value;
                p["$titleEn"].Value = e.TitleEn;
                p["$noteEn"].Value = (object?)e.NoteEn ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return inserted;
    }

    /// <summary>读取知识库全部条目（含已停用），按「分类 → 优先级降序 → 模式」排序。</summary>
    public List<DirKnowledge> GetKnowledgeEntries()
    {
        var list = new List<DirKnowledge>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, pattern, category, title, note, cleanable, priority, builtin, enabled, source,
                   category_en, title_en, note_en
            FROM dir_knowledge
            ORDER BY category, priority DESC, pattern;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DirKnowledge
            {
                Id = r.GetInt64(0),
                Pattern = r.GetString(1),
                Category = r.GetString(2),
                Title = r.GetString(3),
                Note = r.IsDBNull(4) ? null : r.GetString(4),
                Cleanable = r.IsDBNull(5) ? null : r.GetString(5),
                Priority = r.GetInt32(6),
                IsBuiltin = r.GetInt64(7) != 0,
                Enabled = r.GetInt64(8) != 0,
                Source = r.IsDBNull(9) ? null : r.GetString(9),
                CategoryEn = r.IsDBNull(10) ? null : r.GetString(10),
                TitleEn = r.IsDBNull(11) ? null : r.GetString(11),
                NoteEn = r.IsDBNull(12) ? null : r.GetString(12)
            });
        }
        return list;
    }

    // ------------------------------------------------------------ 增长历史

    public void InsertGrowthHistory(long runId, IEnumerable<GrowthItem> items)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO growth_history(run_id, path, root_key, prev_bytes, curr_bytes,
                                       growth_bytes, growth_percent, status, file_count_delta)
            VALUES ($run, $path, $root, $prev, $curr, $growth, $pct, $status, $fdelta);
            """;
        var p = cmd.Parameters;
        p.Add("$run", DbType.Int64);
        p.Add("$path", DbType.String);
        p.Add("$root", DbType.String);
        p.Add("$prev", DbType.Int64);
        p.Add("$curr", DbType.Int64);
        p.Add("$growth", DbType.Int64);
        p.Add("$pct", DbType.Double);
        p.Add("$status", DbType.String);
        p.Add("$fdelta", DbType.Int64);

        foreach (var it in items)
        {
            p["$run"].Value = runId;
            p["$path"].Value = it.Path;
            p["$root"].Value = it.RootKey;
            p["$prev"].Value = (object?)it.PrevBytes ?? DBNull.Value;
            p["$curr"].Value = (object?)it.CurrBytes ?? DBNull.Value;
            p["$growth"].Value = it.GrowthBytes;
            p["$pct"].Value = double.IsNaN(it.GrowthPercent) ? DBNull.Value : it.GrowthPercent;
            p["$status"].Value = it.Status.ToString().ToUpperInvariant();
            p["$fdelta"].Value = it.FileCountDelta;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>查询已落库的增长历史（按批次倒序）。</summary>
    public List<(long RunId, DateTime ScanTime, string Path, long GrowthBytes, double? Pct)> QueryGrowthHistory(int limit)
    {
        var list = new List<(long, DateTime, string, long, double?)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.run_id, r.scan_time, g.path, g.growth_bytes, g.growth_percent
            FROM growth_history g
            JOIN scan_runs r ON r.id = g.run_id
            WHERE g.growth_bytes > 0
            ORDER BY g.run_id DESC, g.growth_bytes DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetInt64(0), DateTime.Parse(r.GetString(1)), r.GetString(2),
                r.GetInt64(3), r.IsDBNull(4) ? null : r.GetDouble(4)));
        }
        return list;
    }

    internal SQLiteConnection Connection => _conn;

    public void Dispose()
    {
        _stageInsertCmd?.Dispose();
        _batchTx?.Dispose();
        _conn.Dispose();
    }
}
