using DiskGrowthMonitor.Native;
using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// 单个卷的 USN 变更检测结果。
/// </summary>
public sealed class VolumeChangeSet
{
    public char Letter;
    /// <summary>本次应记录的最新 USN 状态（扫描完成后入库，供下次增量使用）。</summary>
    public ulong JournalId;
    public long NextUsn;

    /// <summary>日志可读但本次必须全量扫描（日志回卷 / 变更过多 / 枚举失败等）。</summary>
    public bool FullRescan;
    public string? FullRescanReason;

    /// <summary>自上次扫描以来无任何文件系统变更（可整根复用快照）。</summary>
    public bool NoChanges;

    /// <summary>发生变更的记录条数（含重命名的新旧两条记录）。</summary>
    public long ChangedRecords;

    /// <summary>
    /// 脏路径集合（OrdinalIgnoreCase）：包含每条变更路径自身及其全部祖先前缀。
    /// 目录路径不在此集合内 ⇒ 其整个子树自上次扫描以来没有任何变更，可整段复用快照。
    /// </summary>
    public HashSet<string> DirtyPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// NTFS USN 变更日志增量检测服务。
///
/// 原理：NTFS 把每次文件/目录变更写入 USN 变更日志。上次扫描时记录各卷的
/// (JournalId, NextUsn)，本次只读取「上次 → 现在」的日志段，把变更条目映射为
/// 具体路径并展开祖先，得到脏路径集合。扫描器据此剪枝：脏子树照常重扫，
/// 干净子树直接从上一轮快照复制，实现秒级增量对比。
///
/// 回退策略（返回 null 或 FullRescan=true，调用方自动全量扫描）：
/// - 需要管理员权限读取卷设备；- 仅 NTFS 卷；- 日志被删除/重建（JournalId 变化）；
/// - 日志回卷（上次位置早于最早有效记录）；- 单轮变更过多（全扫更划算）。
/// </summary>
public static class UsnJournalService
{
    /// <summary>变更记录数超过该阈值时放弃增量（建脏集合的开销已超过全扫收益）。</summary>
    private const int MaxChangedRecords = 200_000;

    /// <summary>USN 原因位：文件关闭。单独出现时表示无实质变更。</summary>
    private const ulong ReasonCloseOnly = FileSystemNative.USN_REASON_CLOSE;

    /// <summary>
    /// 收集指定卷自上次扫描以来的变更。
    /// </summary>
    /// <param name="letter">盘符。</param>
    /// <param name="prevJournalId">上次保存的 USN 日志标识（0 表示无历史状态）。</param>
    /// <param name="prevNextUsn">上次保存的日志写入位置。</param>
    /// <param name="log">回退/异常原因输出。</param>
    /// <returns>null = 卷不可读（无管理员权限 / 非 NTFS / 查询失败），且无法保存新状态。</returns>
    public static VolumeChangeSet? Collect(char letter, ulong prevJournalId, long prevNextUsn, Action<string> log)
    {
        var vol = FileSystemNative.TryOpenVolume(letter, out int openErr);
        if (vol == null)
        {
            log(Lang.F("无法打开卷 {0}:（Win32 错误 {1}），本次全量扫描。读取 USN 变更日志通常需要以管理员身份运行。",
                       "Could not open volume {0}: (Win32 error {1}); falling back to a full scan. "
                       + "Reading the USN change journal usually requires administrator rights.", letter, openErr));
            return null;
        }

        using (vol)
        {
            if (!FileSystemNative.TryQueryJournal(vol, out var journal, out int qErr) || journal == null)
            {
                log(Lang.F("查询 USN 日志失败（Win32 错误 {0}），本次全量扫描（该卷可能不是 NTFS 或日志未启用）。",
                           "USN journal query failed (Win32 error {0}); falling back to a full scan "
                           + "(the volume may not be NTFS, or the journal is not enabled).", qErr));
                return null;
            }

            var result = new VolumeChangeSet
            {
                Letter = letter,
                JournalId = journal.JournalId,
                NextUsn = journal.NextUsn
            };

            // ---- 必须回退全扫的情形（新状态仍保存，下次可增量） ----
            if (prevJournalId == 0)
            {
                result.FullRescan = true;
                result.FullRescanReason = Lang.T("该卷尚无 USN 历史状态（首次记录）",
                                                 "no USN history for this volume yet (first record)");
                return result;
            }
            if (journal.JournalId != prevJournalId)
            {
                result.FullRescan = true;
                result.FullRescanReason = Lang.T("USN 日志标识已变化（日志可能被删除重建）",
                                                 "the USN journal ID changed (the journal may have been recreated)");
                return result;
            }
            if (journal.NextUsn == prevNextUsn)
            {
                result.NoChanges = true;
                return result;
            }
            if (prevNextUsn < journal.FirstUsn)
            {
                result.FullRescan = true;
                result.FullRescanReason = Lang.T("USN 日志已回卷，部分历史变更不可用",
                                                 "the USN journal wrapped around; some past changes are unavailable");
                return result;
            }

            // ---- 1) 读取「上次 → 现在」的全部变更记录 ----
            // 防御性设计：解析异常或读不到进展时回退全扫，绝不因增量路径崩溃整个程序
            var raw = new List<FileSystemNative.UsnRecord>(4096);
            var changes = new List<(ulong Frn, ulong Parent, string Name)>(1024);
            var buffer = new byte[64 * 1024];
            long usn = prevNextUsn;
            long parsedTotal = 0;
            try
            {
                while (usn < journal.NextUsn)
                {
                    if (!FileSystemNative.TryReadUsnRecords(vol, journal.JournalId, ref usn, buffer, raw,
                            out int rErr, out var batchInfo))
                    {
                        log(Lang.F("读取 USN 记录失败（Win32 错误 {0}），本次全量扫描。",
                                   "Reading USN records failed (Win32 error {0}); falling back to a full scan.", rErr));
                        result.FullRescan = true;
                        result.FullRescanReason = Lang.T("读取变更记录失败", "reading change records failed");
                        return result;
                    }

                    parsedTotal += raw.Count;

                    // 本批存在「结构完整但不是 V2」的记录（如 ReFS / 高版本的 128 位记录）：
                    // 它们会被静默跳过 ⇒ 变更集不完整 ⇒ 继续走下去会得出「无变更」的**错误**
                    // 结论并复用上一轮过期快照。必须显式回退全扫，宁可慢也不能错。
                    if (batchInfo.HasForeignRecords)
                    {
                        log(Lang.F("USN 记录版本无法识别（本批 {0} 条为 USN_RECORD_V{1}，本工具仅解析 V2），本次全量扫描。",
                                   "Unrecognized USN record version ({0} records in this batch are USN_RECORD_V{1}; "
                                   + "this tool only parses V2); falling back to a full scan.",
                                   batchInfo.ForeignCount, batchInfo.ForeignMajor));
                        result.FullRescan = true;
                        result.FullRescanReason = Lang.T("USN 记录版本无法识别", "unrecognized USN record version");
                        return result;
                    }

                    // 无进展保护：一批记录都没解析出来且位置未推进，避免死循环
                    if (raw.Count == 0 && usn < journal.NextUsn)
                    {
                        log(Lang.T("读取批次未解析出任何记录且进度未推进，视为日志异常，本次全量扫描。",
                                   "A batch yielded no records and made no progress; treating the journal as "
                                   + "anomalous and falling back to a full scan."));
                        result.FullRescan = true;
                        result.FullRescanReason = Lang.T("USN 记录读取无进展", "no progress reading USN records");
                        return result;
                    }

                    foreach (var rec in raw)
                    {
                        // 纯 CLOSE 记录 = 文件关闭但无内容/结构变化，忽略
                        if (rec.Reason == 0 || rec.Reason == ReasonCloseOnly) continue;
                        changes.Add((rec.FileReferenceNumber, rec.ParentReferenceNumber, rec.Name));
                    }
                }
            }
            catch (Exception ex)
            {
                log(Lang.F("解析 USN 记录时出现未预期异常（{0}：{1}），本次全量扫描。",
                           "Unexpected exception while parsing USN records ({0}: {1}); falling back to a full scan.",
                           ex.GetType().Name, ex.Message));
                result.FullRescan = true;
                result.FullRescanReason = Lang.T("USN 记录解析异常", "exception while parsing USN records");
                return result;
            }
            raw.Clear();

            result.ChangedRecords = changes.Count;
            if (changes.Count > MaxChangedRecords)
            {
                result.FullRescan = true;
                result.FullRescanReason = Lang.F("变更记录过多（{0} 条），全量扫描更高效",
                                                 "too many change records ({0}); a full scan is more efficient",
                                                 FormatCount(changes.Count));
                return result;
            }
            if (changes.Count == 0)
            {
                // 诊断信息：区间读毕却无实质变更，输出区间端点与解析统计，便于排查误判
                log(Lang.F("USN 区间 [{0} → {1}] 读毕：共解析 {2} 条记录，过滤 CLOSE 后无实质变更，判定本轮无变更。",
                           "USN range [{0} -> {1}] read to the end: {2} records parsed; no substantive change "
                           + "after filtering CLOSE records, so this run is treated as unchanged.",
                           prevNextUsn, journal.NextUsn, parsedTotal));
                result.NoChanges = true;
                return result;
            }

            // ---- 2) 枚举 MFT 建立「目录 FRN → (父FRN, 名称)」映射 ----
            // 只保留目录记录：变更条目自身的名称/父目录已在 USN 记录里，
            // 向上回溯祖先链只需要目录映射（约几万条，远小于全量文件数）。
            var dirs = new Dictionary<ulong, (ulong Parent, string Name)>();
            if (!FileSystemNative.TryEnumMftRecords(vol, (frn, parent, attrs, name) =>
                {
                    if ((attrs & FileSystemNative.FILE_ATTRIBUTE_DIRECTORY) != 0)
                        dirs[frn] = (parent, name);
                    return true;
                }, out long enumerated, out int eErr, out var mftInfo))
            {
                log(Lang.F("枚举 MFT 失败（Win32 错误 {0}），本次全量扫描。",
                           "MFT enumeration failed (Win32 error {0}); falling back to a full scan.", eErr));
                result.FullRescan = true;
                result.FullRescanReason = Lang.T("枚举 MFT 失败", "MFT enumeration failed");
                return result;
            }

            // 「设备层没故障」≠「内容可用」：记录版本不认识时一条目录都建不出来，
            // 于是每个变更对象的父目录都解析失败，最终被误判为「无变更」而复用过期快照。
            // 这是最危险的一类错误（不报错、报告照旧），必须显式回退。
            if (mftInfo.Outcome == FileSystemNative.MftEnumOutcome.UnrecognizedRecord)
            {
                log(Lang.F("枚举 MFT 的记录版本无法识别（{0}），本次全量扫描。",
                           "MFT enumeration returned an unrecognized record version ({0}); falling back to a full scan.",
                           mftInfo.Describe()));
                result.FullRescan = true;
                result.FullRescanReason = Lang.T("MFT 记录版本无法识别", "unrecognized MFT record version");
                return result;
            }

            // 空枚举防御：有变更却一枚录都枚举不到，几乎必然是过滤参数/权限异常，
            // 若按「全部无法定位」误判为无变更会复用过期快照，必须回退全扫
            if (enumerated == 0 && changes.Count > 0)
            {
                log(Lang.F("枚举 MFT 得到 0 条记录（待定位变更 {0} 条），判定枚举异常，本次全量扫描。",
                           "MFT enumeration returned 0 records ({0} changes needed locating); treating the "
                           + "enumeration as anomalous and falling back to a full scan.", changes.Count));
                result.FullRescan = true;
                result.FullRescanReason = Lang.T("MFT 枚举结果为空", "MFT enumeration returned nothing");
                return result;
            }

            // ---- 3) 解析变更路径并展开全部祖先 ----
            string rootPrefix = $"{letter}:\\";
            // 卷根 FRN：优先问 API（权威、不依赖硬编码记录号）。这里取不到**不**算失败 ——
            // 卷根只是回溯终点，取不到时退回「记录 5」不变量即可；而且路径是否可信仍要
            // 经过 dirs 校验（不存在的目录一律返回 null），不会产出假路径。
            ulong volumeRootFileId = FileSystemNative.TryGetRootFileId(letter, out ulong apiRootFileId)
                ? apiRootFileId : 0;
            var pathCache = new Dictionary<ulong, string>();
            foreach (var (frn, parent, name) in changes)
            {
                string? parentPath = ResolveDir(parent, dirs, volumeRootFileId, rootPrefix, pathCache);
                if (parentPath == null) continue; // 对象位于已不存在的目录（创建后又删除），磁盘现状无变化

                string path = parentPath[parentPath.Length - 1] == '\\' ? parentPath + name : parentPath + "\\" + name;
                MarkDirty(result, path);
            }

            // 变更对象全部无法定位（均在已删除目录内）⇒ 磁盘现状与上次一致
            if (result.DirtyPaths.Count == 0)
            {
                log(Lang.F("检测到 {0} 条变更记录但全部无法定位路径（对象均位于已删除目录内），按无变更处理。",
                           "Detected {0} change records but none could be located (their objects live in deleted "
                           + "directories); treating as unchanged.", changes.Count));
                result.NoChanges = true;
            }

            return result;
        }
    }

    /// <summary>回溯目录 FRN 链得到目录完整路径；目录已不存在（或链断裂）返回 null。</summary>
    private static string? ResolveDir(
        ulong frn,
        Dictionary<ulong, (ulong Parent, string Name)> dirs,
        ulong volumeRootFileId,
        string rootPrefix,
        Dictionary<ulong, string> cache)
    {
        // 卷根是递归终点。判定顺序：先用 API 取得的卷根 FRN **精确比较**，再查枚举结果，
        // 最后才退回「卷根 = 记录 5」这一 NTFS 格式不变量。
        //
        // 为什么必须如此：FSCTL_ENUM_USN_DATA 不返回 MFT 记录 0..26（$MFT/$Root 等元数据文件），
        // 所以卷根自己永远不在 dirs 里；而 FRN 是「序列号 << 48 | 记录号」，
        // 本机实测卷根 = 0x0005000000000005，不是裸的 5。
        // 旧代码写 `frn == 5`：既不匹配真实 FRN，又因 dirs 里没有根记录而返回 null
        // ⇒ 所有变更路径都解析失败 ⇒ 被判定为「无变更」而静默复用上一轮快照（错报）。
        if (volumeRootFileId != 0 && frn == volumeRootFileId) return rootPrefix;
        if (cache.TryGetValue(frn, out var cached)) return cached;
        if (!dirs.TryGetValue(frn, out var dir))
            return FileSystemNative.IsVolumeRootFileId(frn) ? rootPrefix : null;
        if (dir.Parent == frn) return null; // 防御环

        string? parentPath = ResolveDir(dir.Parent, dirs, volumeRootFileId, rootPrefix, cache);
        if (parentPath == null) return null;

        string path = parentPath[parentPath.Length - 1] == '\\' ? parentPath + dir.Name : parentPath + "\\" + dir.Name;
        cache[frn] = path;
        return path;
    }

    /// <summary>把路径及其全部祖先前缀加入脏集合。</summary>
    private static void MarkDirty(VolumeChangeSet result, string path)
    {
        var set = result.DirtyPaths;
        int i = 0;
        while (i < path.Length)
        {
            int idx = path.IndexOf('\\', i);
            if (idx < 0) idx = path.Length;
            set.Add(path.Substring(0, idx)); // 依次产生 "C:"、"C:\Users"、"C:\Users\henry" …
            i = idx + 1;
        }
        set.Add(path);
    }

    private static string FormatCount(long n) => n.ToString("N0");
}
