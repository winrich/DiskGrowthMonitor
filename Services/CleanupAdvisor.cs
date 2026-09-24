using DiskGrowthMonitor.Models;
using System.Data.SQLite;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// 「可安全清理」清单：从本轮快照里挑出命中知识库、且可清理性为「安全」的目录，
/// 去掉被祖先条目覆盖的子孙（避免合计重复计数），按占用降序返回。
///
/// 数据来源是本轮 <c>dir_snapshots</c>（子树累计占用，已硬链接去重、已剔除被排除的子目录）；
/// 匹配复用 <see cref="KnowledgeService.Match"/>，不另写一套模式匹配逻辑 ——
/// 否则两处语义一旦漂移，报告标注与清单就会互相矛盾。
///
/// 只收录「安全」等级：该清单会被当作「可以放心删」的依据，混入「谨慎」类条目会误导读者
/// （例如 <c>C:\Windows\SoftwareDistribution</c> 整目录删除会导致 Windows Update 重新初始化）。
/// </summary>
public static class CleanupAdvisor
{
    /// <summary>清单只收录的可清理性等级（与 <see cref="DirKnowledge.Cleanable"/> 的取值一致）。</summary>
    public const string SafeLevel = "安全";

    /// <summary>
    /// 生成清单。返回的列表已按占用降序排列，且不含任何被同清单内其它条目覆盖的子目录。
    /// </summary>
    public static List<CleanableDir> Find(
        DatabaseService db, KnowledgeService knowledge, IReadOnlyList<ScanRoot> roots)
    {
        var list = new List<CleanableDir>();
        if (roots.Count == 0 || knowledge.EffectiveCount == 0) return list;

        // 必须限定本轮扫描根：dir_snapshots 会保留非本轮根的旧行（其它查询同样带此过滤），
        // 不过滤就会把历史批次的数据算进来。
        var ps = new List<SQLiteParameter>(roots.Count);
        var names = new List<string>(roots.Count);
        for (int i = 0; i < roots.Count; i++)
        {
            string name = "$rk" + i;
            names.Add(name);
            ps.Add(new SQLiteParameter(name, roots[i].Key));
        }

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT path, size_bytes
            FROM dir_snapshots
            WHERE depth > 0 AND root_key IN ({string.Join(", ", names)});
            """;
        foreach (var p in ps) cmd.Parameters.Add(p);

        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var m = knowledge.Match(r.GetString(0));
                if (m == null || m.Entry.Cleanable != SafeLevel) continue;
                list.Add(new CleanableDir(r.GetString(0), r.GetInt64(1), m.Entry));
            }
        }

        DropNested(list);
        list.Sort((x, y) => y.Bytes.CompareTo(x.Bytes));
        return list;
    }

    /// <summary>
    /// 去掉被同清单内其它条目覆盖的子孙目录（只保留最外层）。
    ///
    /// 做法：把全部命中路径放进大小写不敏感的集合，再逐条回溯它的各级上级路径，
    /// 只要某一级也在集合里，说明该目录已被祖先条目覆盖，丢弃。
    ///
    /// 为什么不用「按路径排序后单遍比较」的写法：字典序并不保证同前缀的子孙连续。
    /// 例如 <c>C:\A</c>、<c>C:\A2</c>、<c>C:\A\zz</c> 三者按序排列时，
    /// 兄弟目录 <c>C:\A2</c>（'2' &lt; '\'）会插在 <c>C:\A</c> 与其子孙之间，
    /// 使 <c>C:\A</c> 被误判为「已离开作用范围」而被弹出栈，
    /// <c>C:\A\zz</c> 随即被当成孤立条目保留 ⇒ 合计重复计数。
    /// 本写法与路径顺序无关，只与「是否存在祖先」有关，故不存在该问题。
    /// 代价为每条路径约「层级数」次哈希查找，清单规模下可忽略。
    /// </summary>
    private static void DropNested(List<CleanableDir> list)
    {
        if (list.Count < 2) return;

        // net45 的 HashSet<T> 没有 (capacity, comparer) 构造函数（4.7.2 才加入），只能传比较器
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list) all.Add(item.Path);

        var kept = new List<CleanableDir>(list.Count);
        foreach (var item in list)
        {
            if (!HasKnownAncestor(item.Path, all)) kept.Add(item);
        }

        list.Clear();
        list.AddRange(kept);
    }

    /// <summary>该路径的任一级上级目录是否也在这个集合里。</summary>
    private static bool HasKnownAncestor(string path, HashSet<string> all)
    {
        // "C:\a\b\c" → 依次检查 "C:\a\b"、"C:\a"；idx <= 2 即已到盘符根（如 "C:\"），无需再查
        int idx = path.LastIndexOf('\\');
        while (idx > 2)
        {
            if (all.Contains(path.Substring(0, idx))) return true;
            idx = path.LastIndexOf('\\', idx - 1);
        }
        return false;
    }
}
