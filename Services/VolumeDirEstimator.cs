using DiskGrowthMonitor.Models;
using DiskGrowthMonitor.Compat;
using DiskGrowthMonitor.Native;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// 用 NTFS MFT 枚举精确统计「某扫描根下的目录总数」，作为首次扫描（无历史基准时）
/// 进度百分比的分母。
///
/// 需要管理员权限打开卷设备（FSCTL_ENUM_USN_DATA 要求卷的 FILE_READ_DATA），
/// 非 NTFS / 无权限 / 路径解析失败一律返回 0，调用方自动降级（容量近似或 --%）。
///
/// 口径与 SkipListService 严格一致：统计时扣除
/// - 任意层级名称命中 $Recycle.Bin / $RECYCLE.BIN / System Volume Information 的子树；
/// - 扫描根下一层名称命中 Windows 的子树；
/// - 命令行 --exclude 前缀对应的子树。
/// 但「权限不足」「IO 错误」「符号链接」的子树无法预知，故分母可能略大于实际会扫描到的
/// 目录数，表现为进度尾段偏慢（收尾时统一置 100%）。
/// </summary>
public static class VolumeDirEstimator
{
    /// <summary>内置默认排除：任意层级命中即排除。</summary>
    private static readonly string[] DefaultExcludeNames =
    {
        "$Recycle.Bin",
        "$RECYCLE.BIN",
        "System Volume Information"
    };

    /// <summary>内置默认排除：仅扫描根下一层命中（即 C:\Windows 形式）。</summary>
    private static readonly string[] DefaultExcludeRootChildren =
    {
        "Windows"
    };

    /// <summary>
    /// 估算失败的阶段。之所以要把它带出来：过去所有失败都只表现为「0 条记录」，
    /// 于是「记录布局读错」与「权限不足」被混为一谈，提示文案直接写成「需要管理员权限」，
    /// 把诊断变成了误导（2026-09-24 用户反馈）。错误码只能说明「设备说了什么」，
    /// 阶段才能说明「我们走到哪一步断的」。
    /// </summary>
    public enum EstimateStage
    {
        Ok = 0,
        /// <summary>扫描根不是「X:\...」形式，无法定位所在卷。</summary>
        NotDrivePath,
        /// <summary>打开卷设备失败（非管理员 / 非 NTFS）。</summary>
        OpenVolume,
        /// <summary>MFT 枚举本身失败（Win32 错误码见 LastError）。</summary>
        EnumRecords,
        /// <summary>枚举成功但一条目录记录都没有解析出来。</summary>
        NoDirRecords,
        /// <summary>设备有数据返回，但记录不是 USN_RECORD_V2（如 ReFS 的 128 位 V3），本工具不解析。</summary>
        RecordVersion,
        /// <summary>枚举出目录树了，但目标路径在其中解析不出（FRN 链断裂 / 路径不存在）。</summary>
        PathNotFound,
        /// <summary>找不到卷根 FRN（问 API 与反查枚举结果都失败）。宁可失败降级，
        /// 也不退回常量「记录 5」——那会得到一个看似合理却错误的分母。</summary>
        RootNotFound,
        /// <summary>未预期异常（LastError = -1）。</summary>
        Exception
    }

    /// <summary>
    /// 估算结果：DirCount = 0 表示无法估算（调用方需降级）。
    ///
    /// LastError 是失败原因，取值约定：
    ///   · 0   —— 没有 Win32 错误（正常结束却没拿到记录，或未走到设备调用）；
    ///   · &gt;0 —— Win32 错误码（5 = ERROR_ACCESS_DENIED，即真的权限不足）；
    ///   · &lt;0 —— 本类内部状态（-1 = 未捕获异常，-3 = 枚举批次超限）。
    /// Stage 说明断在哪一步；两者一起才能给出可核对的文案。
    ///
    /// Detail 是枚举过程的**自证信息**（缓冲布局、记录版本、输入结构），只在 --dev 下回显，
    /// 用于事后核对「当时到底按什么布局/版本解析的」，不参与任何判定。
    /// </summary>
    public readonly record struct Result(long DirCount, long ElapsedMs, int LastError, EstimateStage Stage, string Detail)
    {
        public bool Ok => DirCount > 0;
    }

    /// <summary>
    /// 尝试精确估算。绝不抛异常——任何失败都返回 DirCount = 0。
    /// </summary>
    /// <param name="rescueByKnowledgeBase">
    /// 目录用途知识库的豁免判定：传入完整路径，返回 true 表示该目录虽命中默认排除规则、
    /// 但扫描器会因命中知识库而重新扫描它，因此**也必须计入分母**。
    /// 传 null 表示不考虑豁免（老行为）。
    ///
    /// 为什么必须传：扫描器会豁免命中知识库的目录（如 C:\Windows），口径不一致时分母会
    /// 比真实要扫的目录数小得多（实测 -d c：分母 58,660 vs 实际 191,704），进度条会提前
    /// 冲到 100% 然后长时间停住 —— 分母不准等于没分母。
    /// </param>
    public static Result TryEstimate(ScanRoot root, bool useDefaultExclude, IReadOnlyList<string> userExcludes,
        Func<string, bool>? rescueByKnowledgeBase = null)
    {
        long t0 = PlatformCompat.TickCount64();
        long count = 0;
        int error = 0;
        string detail = string.Empty;
        EstimateStage stage = EstimateStage.Ok;
        try { count = Estimate(root, useDefaultExclude, userExcludes, rescueByKnowledgeBase, out error, out stage, out detail); }
        catch { count = 0; error = -1; stage = EstimateStage.Exception; detail = string.Empty; }
        if (count <= 0 && stage == EstimateStage.Ok) stage = EstimateStage.NoDirRecords;
        return new Result(count, PlatformCompat.TickCount64() - t0, error, stage, detail);
    }

    /// <summary>
    /// 能否打开卷设备（决定 MFT 估算是否可行）。仅做一次开关句柄的探测，
    /// 用于在真正枚举前给出准确提示，避免"提示要枚举却其实没权限"的误导。
    /// </summary>
    public static bool CanEnumerate(ScanRoot root)
    {
        try
        {
            string path = ResolveRealPath(root.Path);
            if (path.Length < 2 || path[1] != ':') return false;
            var vol = FileSystemNative.TryOpenVolume(char.ToUpperInvariant(path[0]), out _);
            if (vol == null) return false;
            vol.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long Estimate(ScanRoot root, bool useDefaultExclude, IReadOnlyList<string> userExcludes,
        Func<string, bool>? rescueByKnowledgeBase, out int lastError, out EstimateStage stage, out string detail)
    {
        lastError = 0;
        stage = EstimateStage.Ok;
        detail = string.Empty;

        // 扫描根可能位于 junction / 符号链接之下（目录项不在本卷 MFT 里），
        // 先解析为最终真实路径，否则会在错误的卷里找不到路径而白白放弃估算
        string path = ResolveRealPath(root.Path);
        if (path.Length < 2 || path[1] != ':') { stage = EstimateStage.NotDrivePath; return 0; }
        char letter = char.ToUpperInvariant(path[0]);

        var vol = FileSystemNative.TryOpenVolume(letter, out lastError);
        if (vol == null) { stage = EstimateStage.OpenVolume; return 0; }  // 非管理员 / 非 NTFS / 卷不可读
        using (vol)
        {
            // 只保留目录记录：几十万条，远小于全量文件记录数
            var dirs = new Dictionary<ulong, (ulong Parent, string Name)>(1 << 16);
            if (!FileSystemNative.TryEnumMftRecords(vol, (frn, parent, attrs, name) =>
                {
                    if ((attrs & FileSystemNative.FILE_ATTRIBUTE_DIRECTORY) != 0)
                        dirs[frn] = (parent, name);
                    return true;
                }, out long enumerated, out lastError, out var mftInfo))
            {
                stage = EstimateStage.EnumRecords;
                return 0;
            }
            detail = mftInfo.Describe();

            // 「设备层没故障」≠「内容可用」：记录版本不认识时一条也解不出，
            // 若把它当成「这个卷没有目录」，就会得到错误的结论（分母 1 或误判无变更）
            if (mftInfo.Outcome == FileSystemNative.MftEnumOutcome.UnrecognizedRecord)
            {
                stage = EstimateStage.RecordVersion;
                return 0;
            }

            if (enumerated == 0 || dirs.Count == 0) { stage = EstimateStage.NoDirRecords; return 0; }

            var children = BuildChildren(dirs);

            // 卷根 FRN 先问 API、再反查数据，都失败则明确失败（见 ResolveVolumeRootFrn）
            if (!ResolveVolumeRootFrn(letter, dirs, out ulong volumeRootFrn))
            {
                stage = EstimateStage.RootNotFound;
                return 0;
            }

            ulong rootFrn = ResolvePath(path, dirs, children, volumeRootFrn);
            if (rootFrn == 0) { stage = EstimateStage.PathNotFound; return 0; }

            // 用户 --exclude 前缀：逐段解析为 FRN（数量少，成本可忽略）
            var excludeFrns = new HashSet<ulong>();
            foreach (var raw in userExcludes)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string full;
                try { full = Path.GetFullPath(raw); } catch { continue; }
                full = ResolveRealPath(full);
                if (full.Length > 3 && full[full.Length - 1] == '\\') full = full.Substring(0, full.Length - 1);
                ulong f = ResolvePath(full, dirs, children, volumeRootFrn);
                if (f != 0 && f != rootFrn) excludeFrns.Add(f);
            }

            return CountSubtree(rootFrn, volumeRootFrn, dirs, children, excludeFrns,
                useDefaultExclude, letter, rescueByKnowledgeBase);
        }
    }

    /// <summary>
    /// 取卷根的完整 FRN。两级来源，按可信度排序：
    ///   1) 问 API：<see cref="FileSystemNative.TryGetRootFileId"/>（GetFileInformationByHandle）
    ///      —— **权威**，不依赖任何硬编码记录号，换文件系统、换机器都成立；
    ///   2) 反查：根级子项的 Parent 字段（MFT 枚举不返回记录 0..26 ⇒ 卷根自己不在结果里，
    ///      但它的孩子都会带着它）。
    /// 两级都失败 ⇒ 返回 false，由调用方明确失败并降级。
    ///
    /// 🔴 曾经的写法是「找不到就返回常量 5」。后果不是报错，而是**静默错数**：
    /// ResolvePath("C:\") 返回 5 ⇒ children[5] 恒为空 ⇒ 子树只数到自己 = 1 ⇒
    /// 调用方以「DirCount &gt; 0」判定成功 ⇒ 分母变成 1，进度条瞬间满格还显示「已按 MFT 统计」。
    /// 这就是「宁可失败也不要假数字」的典型：失败会降级，假数字只会骗人。
    /// </summary>
    private static bool ResolveVolumeRootFrn(char letter, Dictionary<ulong, (ulong Parent, string Name)> dirs,
        out ulong volumeRootFrn)
    {
        if (FileSystemNative.TryGetRootFileId(letter, out volumeRootFrn) && volumeRootFrn != 0)
            return true;

        return FileSystemNative.TryFindRootFileIdFromParents(EnumerateParents(dirs), out volumeRootFrn);
    }

    /// <summary>惰性枚举所有目录记录的 Parent 字段（用于反查卷根 FRN，避免为此复制一份大列表）。</summary>
    private static IEnumerable<ulong> EnumerateParents(Dictionary<ulong, (ulong Parent, string Name)> dirs)
    {
        foreach (var kv in dirs) yield return kv.Value.Parent;
    }

    /// <summary>构建「父 FRN → 子目录 FRN 列表」索引。
    /// 注意：NTFS 卷根（FRN 5）的父指向自身，必须跳过这条自指边，
    /// 否则子树遍历会把根当作自己的子节点而无限循环。</summary>
    private static Dictionary<ulong, List<ulong>> BuildChildren(
        Dictionary<ulong, (ulong Parent, string Name)> dirs)
    {
        var children = new Dictionary<ulong, List<ulong>>(dirs.Count);
        foreach (var kv in dirs)
        {
            if (kv.Value.Parent == kv.Key) continue;    // 根目录自指：跳过
            if (!children.TryGetValue(kv.Value.Parent, out var list))
                children[kv.Value.Parent] = list = new List<ulong>(4);
            list.Add(kv.Key);
        }
        return children;
    }

    /// <summary>
    /// 迭代统计子树目录数（含自身），跳过被排除的子树。用显式栈避免深目录递归爆栈。
    ///
    /// 默认排除项若命中知识库（如 C:\Windows），扫描器会重新扫描它 ⇒ 这里也必须计入，
    /// 否则分母会显著小于真实要扫的目录数。判定与 SkipListService 的区别仅此一处：
    /// SkipListService 能拿到完整路径（它本来就在遍历文件系统），本类只有 FRN 树，
    /// 因此对被判为「默认排除」的候选目录按需重建一次路径，再交给知识库判定。
    /// 成本可忽略：只有命中排除规则的少数目录才会走重建。
    /// </summary>
    private static long CountSubtree(
        ulong rootFrn,
        ulong volumeRootFileId,
        Dictionary<ulong, (ulong Parent, string Name)> dirs,
        Dictionary<ulong, List<ulong>> children,
        HashSet<ulong> excludeFrns,
        bool useDefaultExclude,
        char letter,
        Func<string, bool>? rescueByKnowledgeBase)
    {
        long count = 0;
        long limit = dirs.Count + 1;                    // 兜底：每个目录最多计一次
        var stack = new Stack<(ulong Frn, int Depth)>();
        stack.Push((rootFrn, 0));

        while (stack.Count > 0)
        {
            var (frn, depth) = stack.Pop();
            if (++count > limit) break;                 // 出现环的极端情况：立即停止，避免挂死

            if (!children.TryGetValue(frn, out var kids)) continue;
            foreach (var kid in kids)
            {
                if (kid == frn) continue;               // 自指边防御
                if (excludeFrns.Contains(kid)) continue;
                if (useDefaultExclude && dirs.TryGetValue(kid, out var info)
                                      && IsDefaultExcluded(info.Name, depth + 1)
                                      && !(rescueByKnowledgeBase?.Invoke(
                                              BuildPath(kid, dirs, letter, volumeRootFileId)) ?? false))
                {
                    continue;
                }
                stack.Push((kid, depth + 1));
            }
        }
        return count;
    }

    /// <summary>
    /// 自底向上拼出某目录的完整路径（"C:\Windows\System32"）。链断在未被枚举的祖先
    /// （MFT 记录 0..26，含卷根）时自然终止，拼出的路径正好从盘符根开始。
    /// 仅在判定默认排除豁免时按需调用，调用次数极少。
    ///
    /// <paramref name="volumeRootFileId"/> 用**精确比较**判定「下一跳就是卷根」，
    /// 只有 API 与反查都拿不到卷根 FRN 时才退回「记录号 5」这一格式不变量。
    /// </summary>
    private static string BuildPath(ulong frn, Dictionary<ulong, (ulong Parent, string Name)> dirs, char letter,
        ulong volumeRootFileId)
    {
        var parts = new List<string>(8);
        ulong cur = frn;
        for (int guard = 0; guard < 512; guard++)
        {
            if (!dirs.TryGetValue(cur, out var info)) break;
            if (string.IsNullOrEmpty(info.Name)) break;   // 卷根记录名（"."）不作为路径段
            parts.Add(info.Name);
            if (info.Parent == cur) break;                // 自指 = 已到根
            if (info.Parent == volumeRootFileId) break;   // 下一跳就是卷根（精确）
            if (volumeRootFileId == 0 && FileSystemNative.IsVolumeRootFileId(info.Parent)) break;
            cur = info.Parent;
        }
        parts.Reverse();
        return parts.Count == 0 ? letter + ":\\" : letter + ":\\" + string.Join("\\", parts);
    }

    /// <summary>默认排除判定：与 SkipListService 的规则逐条对应。</summary>
    private static bool IsDefaultExcluded(string name, int depth)
    {
        foreach (var n in DefaultExcludeNames)
        {
            if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (depth == 1)
        {
            foreach (var n in DefaultExcludeRootChildren)
            {
                if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 把 "C:\Users\henry\x" 逐段解析为目录 FRN。
    /// 起点用 <paramref name="volumeRootFrn"/>（由 <see cref="ResolveVolumeRootFrn"/> 取得：
    /// 先问 API、再反查数据，实测 0x0005000000000005 —— 写死裸的 5 会让 children[5] 永远为空）。
    /// 任一段匹配不到（不存在、经过 junction 改名等）返回 0。
    /// </summary>
    private static ulong ResolvePath(
        string path,
        Dictionary<ulong, (ulong Parent, string Name)> dirs,
        Dictionary<ulong, List<ulong>> children,
        ulong volumeRootFrn)
    {
        if (path.Length < 2 || path[1] != ':') return 0;
        string rest = path.Substring(2).Trim('\\');
        if (rest.Length == 0) return volumeRootFrn;

        ulong cur = volumeRootFrn;
        foreach (var seg in rest.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!children.TryGetValue(cur, out var kids)) return 0;

            ulong next = 0;
            foreach (var k in kids)
            {
                if (dirs.TryGetValue(k, out var info)
                    && info.Name.Equals(seg, StringComparison.OrdinalIgnoreCase))
                {
                    next = k;
                    break;
                }
            }

            if (next == 0) return 0;
            cur = next;
        }
        return cur;
    }

    /// <summary>
    /// 解析路径上所有 junction / 符号链接，返回最终真实路径（已去掉 \\?\ 前缀）；
    /// 失败时原样返回路径，绝不抛异常。
    /// 目的：扫描根常位于 junction 之下（目录项实际在别的卷），必须先落到真实卷
    /// 才能在对应卷的 MFT 里找到它。
    /// 实现说明：原先用 Directory.ResolveLinkTarget（.NET 6+ 专有）逐段解析，
    /// 现改用 GetFinalPathNameByHandleW —— 一次系统调用即展开整条路径，
    /// 且 net45 / net9.0 通用，无需 #if。
    /// </summary>
    private static string ResolveRealPath(string path)
    {
        try
        {
            string full;
            try { full = Path.GetFullPath(path); } catch { return path; }

            return FileSystemNative.TryGetFinalPath(full, out string real) ? StripPrefix(real) : full;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>剥掉 \\?\ 或 \??\ 设备路径前缀。</summary>
    private static string StripPrefix(string p)
    {
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) return p.Substring(4);
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) return p.Substring(4);
        return p;
    }
}
