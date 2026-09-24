using System.Text.RegularExpressions;
using DiskGrowthMonitor.Models;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// 目录用途知识库：把 <c>dir_knowledge</c> 表中的条目编译成匹配器，
/// 为报告中的目录附上用途说明。
///
/// 匹配语义为<b>后缀对齐</b>：从路径尾部逐段向前比较，模式段全部匹配即命中，路径允许多出前缀。
/// 因此内置条目不写盘符与用户名，可跨机器、跨用户命中（<c>*\AppData\Local\Temp</c> 能同时
/// 命中 C 盘与 D 盘下的任意用户）。
///
/// 性能：先用「末段名」建索引筛出候选项，再做段序列比较 —— 即便全量 27 万目录逐一询问，
/// 每个路径也只需比较少数几个模式。报告标注的调用点只覆盖候选集（榜单 + 明细，千行量级）。
///
/// 除报告标注外，本类还为扫描期的「跳过清单豁免」提供判定：命中知识库的目录
/// （自身命中，或其祖先链上存在已知目录）即使被内置默认排除或曾因权限不足被记入跳过清单，
/// 也每轮重新尝试扫描 —— 见 <see cref="ShouldRescan"/>。该判定只在「本已决定跳过」的目录上触发，
/// 不参与常规遍历路径，因此对扫描性能无影响。
/// </summary>
public sealed class KnowledgeService
{
    /// <summary>编译后的模式。</summary>
    private sealed class Compiled
    {
        public required DirKnowledge Entry { get; init; }
        public required string[] Segments { get; init; }
        public bool LastIsWildcard => Segments[Segments.Length - 1] is "*" or "**";
    }

    /// <summary>%VAR% 环境变量占位符。</summary>
    private static readonly Regex VarPattern =
        new("%([A-Za-z_][A-Za-z0-9_]*)%", RegexOptions.Compiled);

    private readonly List<Compiled> _all = new();
    private readonly Dictionary<string, List<Compiled>> _byLastSegment =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Compiled> _wildcardLastSegment = new();

    /// <summary>「祖先片段」索引：判断某个目录的后代里是否可能存在已知目录（见 <see cref="DeriveAncestors"/>）。</summary>
    private readonly Dictionary<string, List<Compiled>> _ancestorByLastSegment =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Compiled> _ancestorWildcardLastSegment = new();

    /// <summary>库中全部条目（含已停用），供 --list-knowledge 展示。</summary>
    public IReadOnlyList<DirKnowledge> AllEntries { get; }

    /// <summary>本次启动新补齐的内置条目数（已存在的条目不会被覆盖）。</summary>
    public int SeedInserted { get; }

    /// <summary>实际参与匹配的条目数（已启用且模式有效）。</summary>
    public int EffectiveCount => _all.Count;

    public KnowledgeService(DatabaseService db)
    {
        SeedInserted = db.SeedKnowledge(KnowledgeSeed.Entries);
        AllEntries = db.GetKnowledgeEntries();

        // 祖先片段去重表：多条模式常派生出同一片段（如 C:\Windows\Temp 与 C:\Windows\Logs
        // 都会派生出 C:\Windows），去重后索引规模很小
        var ancestorSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in AllEntries)
        {
            if (!e.Enabled) continue;
            var c = Compile(e);
            if (c == null) continue;

            _all.Add(c);
            AddToIndex(c, _byLastSegment, _wildcardLastSegment);

            DeriveAncestors(c, segs =>
            {
                if (!ancestorSeen.Add(string.Join("\\", segs))) return;
                AddToIndex(new Compiled { Entry = c.Entry, Segments = segs },
                    _ancestorByLastSegment, _ancestorWildcardLastSegment);
            });
        }
    }

    /// <summary>把编译后的模式按「末段名」放进索引；末段为通配符时单独成列。</summary>
    private static void AddToIndex(Compiled c, Dictionary<string, List<Compiled>> byLast,
        List<Compiled> wildcardLast)
    {
        if (c.LastIsWildcard)
        {
            wildcardLast.Add(c);
            return;
        }
        string key = c.Segments[c.Segments.Length - 1];
        if (!byLast.TryGetValue(key, out var bucket))
            byLast[key] = bucket = new List<Compiled>();
        bucket.Add(c);
    }

    /// <summary>
    /// 由模式派生「祖先片段」：取模式的前 j+1 段（j = 0 … n-2）。
    ///
    /// 为什么需要：模式是<b>后缀对齐</b>匹配的，若目录 D 命中某个祖先片段，说明 D 的路径
    /// 恰好是「某条完整模式的路径」的前缀 —— 即 D 的后代里可能存在已知目录。
    /// 只有这样，被排除或被跳过清单拦下的<b>祖先</b>目录才会被放行进入：
    /// 例如知识库只有 <c>C:\Windows\WinSxS</c>，<c>C:\Windows</c> 本身并非条目，
    /// 但若不放行 <c>C:\Windows</c>，就永远走不到 <c>WinSxS</c>（枚举被 continue 截断）。
    ///
    /// 全部段均为通配符的片段（<c>*</c>、<c>**</c>）几乎能匹配任何路径、毫无区分度，直接丢弃。
    /// </summary>
    private static void DeriveAncestors(Compiled c, Action<string[]> emit)
    {
        for (int len = 1; len <= c.Segments.Length - 1; len++)
        {
            bool concrete = false;
            for (int i = 0; i < len; i++)
            {
                if (c.Segments[i] is not ("*" or "**")) { concrete = true; break; }
            }
            if (!concrete) continue;

            var segs = new string[len];
            Array.Copy(c.Segments, segs, len);
            emit(segs);
        }
    }

    // ---------------------------------------------------------------- 编译

    private static Compiled? Compile(DirKnowledge e)
    {
        string raw = e.Pattern.Trim();
        if (raw.Length == 0) return null;

        // 展开 %VAR%；取不到该环境变量时保留原样，自然不会命中任何路径
        string expanded = VarPattern.Replace(raw, m =>
            Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);

        // net45 无 Split(char, StringSplitOptions)，统一用 char[] 重载
        var segs = expanded.Replace('/', '\\')
                           .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return segs.Length == 0 ? null : new Compiled { Entry = e, Segments = segs };
    }

    // ---------------------------------------------------------------- 匹配

    /// <summary>为单个路径匹配用途；未命中返回 null。</summary>
    public KnowledgeMatch? Match(string path)
    {
        if (_all.Count == 0 || string.IsNullOrEmpty(path)) return null;

        var segs = path.Replace('/', '\\')
                       .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return null;

        int pi = segs.Length - 1;
        Compiled? best = null;

        // 只检查「末段名相同」与「末段为通配」两类候选，其余模式不可能后缀对齐
        if (_byLastSegment.TryGetValue(segs[pi], out var candidates))
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!MatchTail(segs, pi, c.Segments, c.Segments.Length - 1)) continue;
                if (best == null || IsBetter(c, best)) best = c;
            }
        }
        for (int i = 0; i < _wildcardLastSegment.Count; i++)
        {
            var c = _wildcardLastSegment[i];
            if (!MatchTail(segs, pi, c.Segments, c.Segments.Length - 1)) continue;
            if (best == null || IsBetter(c, best)) best = c;
        }

        return best == null ? null : new KnowledgeMatch(best.Entry, best.Segments.Length);
    }

    /// <summary>该路径自身是否命中知识库（即它本身就是一条已知目录）。</summary>
    public bool IsKnown(string path) => Match(path) != null;

    /// <summary>该路径的后代里是否可能存在已知目录（即该路径是某条知识库模式的祖先）。</summary>
    public bool MayContainKnown(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (_ancestorByLastSegment.Count == 0 && _ancestorWildcardLastSegment.Count == 0) return false;

        var segs = path.Replace('/', '\\')
                       .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return false;

        int pi = segs.Length - 1;

        if (_ancestorByLastSegment.TryGetValue(segs[pi], out var candidates))
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (MatchTail(segs, pi, candidates[i].Segments, candidates[i].Segments.Length - 1))
                    return true;
            }
        }
        for (int i = 0; i < _ancestorWildcardLastSegment.Count; i++)
        {
            var c = _ancestorWildcardLastSegment[i];
            if (MatchTail(segs, pi, c.Segments, c.Segments.Length - 1)) return true;
        }
        return false;
    }

    /// <summary>
    /// 该路径是否应「豁免跳过、每轮重新尝试扫描」：自身命中知识库，或其后代中可能存在已知目录。
    ///
    /// 供跳过清单豁免使用。理由：知识库里的目录是磁盘占用的大头，而内置默认排除与历史权限记录
    /// 都可能已经过时（例如上次以非管理员身份运行被拒），因此值得每轮重新尝试 —— 即「应扫尽可能扫」。
    /// 注意：本条<b>不</b>豁免 <c>-x</c> 用户显式排除，也<b>不</b>豁免目录符号链接（涉及去重正确性）。
    /// </summary>
    public bool ShouldRescan(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Match(path) != null || MayContainKnown(path);
    }

    /// <summary>
    /// 从尾部递归比较：<paramref name="pi"/>/<paramref name="qi"/> 均为「从末尾数的当前索引」，
    /// 小于 0 表示该侧已用完。
    /// </summary>
    private static bool MatchTail(string[] pathSegs, int pi, string[] patSegs, int qi)
    {
        if (qi < 0) return true;            // 模式段全部匹配完毕 → 命中（路径多出的前缀即后缀对齐部分）
        string p = patSegs[qi];

        if (p == "**")
        {
            // ** 可吞掉任意段数（含 0 段）
            for (int take = 0; take <= pi + 1; take++)
                if (MatchTail(pathSegs, pi - take, patSegs, qi - 1)) return true;
            return false;
        }

        if (pi < 0) return false;           // 路径段不够，模式还剩段 → 不命中
        if (p != "*" && !string.Equals(p, pathSegs[pi], StringComparison.OrdinalIgnoreCase))
            return false;
        return MatchTail(pathSegs, pi - 1, patSegs, qi - 1);
    }

    /// <summary>优先级高者优先；同优先级时模式更具体（段数更多）者优先。</summary>
    private static bool IsBetter(Compiled a, Compiled b)
    {
        if (a.Entry.Priority != b.Entry.Priority) return a.Entry.Priority > b.Entry.Priority;
        return a.Segments.Length > b.Segments.Length;
    }

    // ---------------------------------------------------------------- 批量标注

    /// <summary>为一批目录标注用途，返回本次命中数（已有标注的行会被跳过）。</summary>
    public int Annotate(IEnumerable<GrowthItem> items)
    {
        if (_all.Count == 0) return 0;

        int hits = 0;
        foreach (var it in items)
        {
            if (it.Knowledge != null) continue;
            var m = Match(it.Path);
            if (m == null) continue;
            it.Knowledge = m;
            hits++;
        }
        return hits;
    }

    /// <summary>为报告的全部候选行（榜单 / 新增消失 / 明细）标注用途，返回本次命中数。</summary>
    public int Annotate(AnalysisResult a)
    {
        int hits = Annotate(a.GrowthTop);
        hits += Annotate(a.ShrinkTop);
        hits += Annotate(a.NewTop);
        hits += Annotate(a.RemovedTop);
        hits += Annotate(a.Detail);
        return hits;
    }
}
