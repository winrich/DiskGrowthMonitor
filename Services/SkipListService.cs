using DiskGrowthMonitor.Models;
using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// 跳过清单服务：统一管理「默认排除 / 自定义排除 / 权限不足 / IO 错误 / 符号链接」五类跳过决策。
///
/// 关键行为（对应需求）：
/// - 默认排除目录（Windows、$Recycle.Bin、System Volume Information）与权限不足的目录都会被记录；
/// - 权限不足 / IO 错误的目录在**下一次扫描时直接从清单加载并跳过**，不再重复尝试，
///   从而避免每次扫描都在同一处反复触发权限异常、浪费时间；
/// - 可用 --reset-skips 清空这类持久化记录，让程序重新尝试；
/// - <b>知识库豁免</b>（默认开启）：目录若命中 <c>dir_knowledge</c>（自身命中，或其祖先链上存在已知
///   目录），则即使被内置默认排除、或已记入历史权限/IO 跳过清单，本次仍<b>重新尝试扫描</b>。
///   理由：知识库里的目录恰是磁盘占用的大头，而默认排除与历史权限记录都可能已经过时
///   （如上次以非管理员身份运行被拒），故应「应扫尽可能扫」。可用 --no-knowledge-rescan 关闭。
///   <c>-x</c> 用户显式排除与目录符号链接不受该豁免影响。
///
/// <b>硬排除</b>：默认排除里另有一小撮目录连豁免都不适用（见 <see cref="HardExcludeNames"/>）。
/// 典型是 <c>System Volume Information</c> —— 它的 ACL 只授予 SYSTEM，管理员也进不去，
/// 「值得重扫」这个理由在这一事实面前不成立；每轮重试只会白撞权限墙，
/// 还会把该目录的跳过原因永久污染成 ACCESS_DENIED。它与知识库里的用途标注并不矛盾：
/// 条目照常参与报告标注，只是不再触发豁免。
/// </summary>
public sealed class SkipListService
{
    /// <summary>内置默认排除（软排除）：无论位于哪一层，目录名命中即排除；命中知识库时仍可被豁免。</summary>
    private static readonly string[] DefaultExcludeNames =
    {
        "$Recycle.Bin",
        "$RECYCLE.BIN"
    };

    /// <summary>
    /// 内置默认排除中的「硬排除」子集：<b>不可被知识库豁免</b>，也不受跳过清单影响，每轮直接跳过。
    ///
    /// 为什么单独列出来：<c>System Volume Information</c> 在目录用途知识库里是有条目的
    /// （报告需要标注它的用途），于是一视同仁的豁免规则把它当成「值得重扫的已知目录」放行了 ——
    /// 实测（2026-09-24，管理员档）它依然返回 ACCESS_DENIED，结果每轮都撞一次权限墙。
    /// 它的内容（VSS 快照数据）不是磁盘占用增长的可疑来源，且任何身份都读不到，
    /// 属于「豁免规则本就不该覆盖」的例外。
    /// </summary>
    private static readonly string[] HardExcludeNames =
    {
        "System Volume Information"
    };

    /// <summary>内置默认排除：位于扫描根下一层且目录名命中即排除（即 C:\Windows 形式）。</summary>
    private static readonly string[] DefaultExcludeRootChildren =
    {
        "Windows"
    };

    private readonly bool _useDefaultExclude;
    private readonly bool _followReparse;

    /// <summary>命令行 -x 指定并规范化后的排除前缀（不含尾部反斜杠）。</summary>
    private readonly List<string> _excludePrefixes = new();

    /// <summary>从数据库加载的历史持久化跳过路径（权限不足 / IO 错误）。</summary>
    private readonly HashSet<string> _persistentSkips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次扫描新产生的跳过记录，按路径去重。</summary>
    private readonly Dictionary<string, SkipRecord> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>目录用途知识库；为 null 表示不启用豁免（--no-knowledge-rescan）。</summary>
    private readonly KnowledgeService? _knowledge;

    /// <summary>本次扫描中因命中知识库而被豁免、重新尝试扫描的目录数。</summary>
    private long _rescued;

    private readonly SyncLock _sync = new();

    public SkipListService(bool useDefaultExclude, bool followReparse, IEnumerable<string> userExcludes,
        KnowledgeService? knowledge = null)
    {
        _useDefaultExclude = useDefaultExclude;
        _followReparse = followReparse;
        _knowledge = knowledge;
        foreach (var raw in userExcludes)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try { full = Path.GetFullPath(raw); }
            catch { continue; }
            if (full.Length > 3 && full[full.Length - 1] == '\\') full = full.Substring(0, full.Length - 1);
            _excludePrefixes.Add(full);
        }
    }

    /// <summary>已加载的历史持久化跳过路径数量。</summary>
    public int PersistentSkipCount => _persistentSkips.Count;

    /// <summary>本次扫描中因命中知识库而被豁免、重新尝试扫描的目录数。</summary>
    public long RescuedCount => Interlocked.Read(ref _rescued);

    /// <summary>是否启用了知识库豁免。</summary>
    public bool KnowledgeRescanEnabled => _knowledge != null;

    /// <summary>
    /// 该路径是否会因命中知识库而豁免跳过（供 --list-skips 标注）。
    /// 仅用于展示，不改变判定结果。
    /// </summary>
    public bool IsKnowledgeRescued(string path) => _knowledge?.ShouldRescan(path) == true;

    /// <summary>
    /// 该跳过原因是否属于「知识库可豁免」的类别：默认排除 / 权限不足 / IO 错误可豁免；
    /// <c>-x</c> 自定义排除（用户显式意图）与目录符号链接（涉及去重正确性）不可豁免。
    /// </summary>
    public static bool ReasonCanBeRescued(SkipReason r)
        => r is SkipReason.DefaultExclude or SkipReason.AccessDenied or SkipReason.IoError;

    /// <summary>目录名是否属于「硬排除」（见 <see cref="HardExcludeNames"/>）——即使命中知识库也不放行。</summary>
    public static bool IsHardExcludedName(string name)
    {
        foreach (var n in HardExcludeNames)
            if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 该路径是否命中硬排除（按最后一段目录名判定）。
    /// 供 <c>--list-skips</c> 这类「只看路径、拿不到扫描上下文」的展示代码复用，
    /// 保证它与 <see cref="ShouldSkip"/> 的实际判定一致 ——
    /// 硬排除的目录若仍被标成「每轮重试」，展示就成了新的误导（2026-09-24）。
    /// </summary>
    public static bool IsHardExcludedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string trimmed = path.TrimEnd('\\');
        int i = trimmed.LastIndexOf('\\');
        return IsHardExcludedName(i >= 0 ? trimmed.Substring(i + 1) : trimmed);
    }

    /// <summary>该条跳过记录是否会因命中知识库而每轮重试（用于 --list-skips 与报告标注）。</summary>
    public bool IsKnowledgeRescued(SkipRecord record)
        => ReasonCanBeRescued(record.Reason) && IsKnowledgeRescued(record.Path);

    /// <summary>
    /// 载入历史持久化跳过记录（权限不足 / IO 错误）。
    /// </summary>
    public void LoadPersistent(IEnumerable<SkipRecord> records)
    {
        foreach (var r in records)
        {
            if (SkipRecord.IsPersistentSkip(r.Reason))
                _persistentSkips.Add(r.Path);
        }
    }

    /// <summary>
    /// 判断某个目录是否应当跳过、以及跳过原因。
    /// </summary>
    /// <param name="fullPath">目录完整路径。</param>
    /// <param name="name">目录名。</param>
    /// <param name="depth">相对扫描根的层级（根的子目录为 1）。</param>
    /// <param name="reason">跳过原因。</param>
    /// <param name="detail">原因说明。</param>
    /// <returns>true 表示应跳过（且不统计其占用）。</returns>
    public bool ShouldSkip(string fullPath, string name, int depth, out SkipReason reason, out string? detail)
    {
        // 0) 硬排除：不可被知识库豁免，也不受跳过清单影响，每轮直接跳过。
        //    必须排在 rescan 计算与持久化跳过之前：该目录当初正是「被豁免放行后才撞的墙」，
        //    若让库里的 ACCESS_DENIED 记录先命中，它就会被永久定性为权限问题而看不到真因。
        if (_useDefaultExclude && IsHardExcludedName(name))
        {
            reason = SkipReason.DefaultExclude;
            detail = Lang.T("内置默认排除目录（系统保护区，不可豁免）",
                            "Built-in default exclusion (system-reserved area; never rescued)");
            return true;
        }

        // 知识库豁免：本目录自身命中 dir_knowledge、或其祖先链上存在已知目录时，
        // 即使命中下面的跳过规则也放行（每轮重新尝试）。该判定只会在「本已决定跳过」的
        // 路径上触发，常规遍历路径不受影响。注：-x 自定义排除优先级最高，不在豁免范围内。
        bool rescan = _knowledge != null && _knowledge.ShouldRescan(fullPath);
        bool rescued = false;

        // 1) 历史持久化跳过（权限不足 / IO 错误）——避免重复撞权限墙；命中知识库时改为每轮重试
        if (_persistentSkips.Contains(fullPath))
        {
            if (!rescan)
            {
                reason = SkipReason.AccessDenied;
                detail = Lang.T("历史扫描中该目录不可访问，已按跳过清单直接跳过",
                                "Inaccessible in a previous scan; skipped directly per the skip list");
                return true;
            }
            rescued = true;
        }

        // 2) 命令行自定义排除（路径前缀匹配，需在分隔符边界上）——用户显式意图，知识库不推翻
        foreach (var prefix in _excludePrefixes)
        {
            if (fullPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
            {
                reason = SkipReason.UserExclude;
                detail = Lang.F("--exclude 命中：{0}", "matched --exclude: {0}", prefix);
                return true;
            }
        }

        // 3) 内置默认排除——命中知识库时放行，否则知识库里 C:\Windows\* 等系统目录条目永远无法生效
        string? defaultDetail = null;
        if (_useDefaultExclude)
        {
            foreach (var n in DefaultExcludeNames)
            {
                if (name.Equals(n, StringComparison.OrdinalIgnoreCase))
                {
                    defaultDetail = Lang.T("内置默认排除目录", "Built-in default exclusion");
                    break;
                }
            }

            if (defaultDetail == null && depth == 1)
            {
                foreach (var n in DefaultExcludeRootChildren)
                {
                    if (name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    {
                        defaultDetail = Lang.T("内置默认排除目录（系统目录）",
                                               "Built-in default exclusion (system directory)");
                        break;
                    }
                }
            }
        }
        if (defaultDetail != null)
        {
            if (!rescan)
            {
                reason = SkipReason.DefaultExclude;
                detail = defaultDetail;
                return true;
            }
            rescued = true;
        }

        // 走到这里说明本次不跳过；若此前是因知识库豁免而放行，计入统计
        if (rescued) Interlocked.Increment(ref _rescued);

        reason = SkipReason.IoError;
        detail = null;
        return false;
    }

    /// <summary>是否跟随目录重解析点。</summary>
    public bool FollowReparse => _followReparse;

    /// <summary>记录一条跳过项。同一路径重复出现时仅保留首次并累加命中次数。</summary>
    public void Record(string path, string rootKey, SkipReason reason, string? detail)
    {
        lock (_sync)
        {
            if (_pending.TryGetValue(path, out var existing))
            {
                existing.ExistingHitCount++; // 复用 ExistingHitCount 字段暂存本次命中次数
                return;
            }
            _pending[path] = new SkipRecord
            {
                Path = path,
                RootKey = rootKey,
                Reason = reason,
                Detail = detail,
                ExistingHitCount = 1
            };
        }
    }

    /// <summary>取出本次运行产生的全部跳过记录快照。</summary>
    public IReadOnlyList<SkipRecord> TakePending()
    {
        lock (_sync)
        {
            var list = _pending.Values.ToList();
            _pending.Clear();
            return list;
        }
    }

    /// <summary>
    /// 统计本次运行各类跳过原因的数量。
    /// 默认排除与用户排除分开计数：二者合并成「排除 N」时，一旦默认排除目录命中知识库被豁免放行，
    /// 就会出现「设置里默认排除启用、统计里排除 0」这样看似矛盾的战报（2026-09-24 用户反馈第 3 条）。
    /// </summary>
    public (int defaultExcluded, int userExcluded, int denied, int reparse, int errors) CountPending()
    {
        lock (_sync)
        {
            int de = 0, ue = 0, dn = 0, rp = 0, er = 0;
            foreach (var r in _pending.Values)
            {
                switch (r.Reason)
                {
                    case SkipReason.DefaultExclude:
                        de++; break;
                    case SkipReason.UserExclude:
                        ue++; break;
                    case SkipReason.AccessDenied:
                        dn++; break;
                    case SkipReason.ReparsePoint:
                        rp++; break;
                    default:
                        er++; break;
                }
            }
            return (de, ue, dn, rp, er);
        }
    }
}
