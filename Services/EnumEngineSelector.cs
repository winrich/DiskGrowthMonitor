using DiskGrowthMonitor.Models;
using DiskGrowthMonitor.Native;
using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// 枚举引擎选择器（阶段 B）：把「这台机器的这个卷能用哪个信息类」从文档推断
/// 变成**运行时实测 + 按卷缓存**。
///
/// 为什么必须实测而不能只按 OS 版本判：FileIdExtdDirectoryInformation(60) 的可用门槛
/// 文档口径互相矛盾（MSDN 称 Server 2012 起，NtDoc 称 Win10 起），而 WS2012 恰好在争议区间内。
/// 猜错的代价不对称 —— 猜「可用」会在老系统上直接报错，猜「不可用」会让新系统白白退回逐文件句柄。
/// 所以：实测一次（一次枚举调用，只取第一条即停），结果按卷缓存，之后零开销。
///
/// 降级链 60 → 37 → Win32：<c>class 37</c> 自 Vista / Server 2008 起就可用，
/// 能拿到 64 位 FileId（NTFS 上即 MFT 记录号）⇒ 即便 60 在旧系统不可用，
/// 也不会退到「逐文件开句柄」那条慢路径。
/// </summary>
internal sealed class EnumEngineSelector
{
    /// <summary>
    /// 目录枚举缓冲字节数。固定 64 KB：PoC 实测 NTFS 每轮 NtQueryDirectoryFile 约 64 KB 封顶
    /// （64 / 256 / 1024 KB 三者的轮次完全相同），而 1 MB 反而慢 2.9~3.1 倍。
    /// </summary>
    public const int DefaultBufferSize = 64 * 1024;

    private readonly EnumEngineKind _requested;
    private readonly int _bufferSize;

    /// <summary>卷键 → 已解析的引擎档位。</summary>
    private readonly Dictionary<string, EnumEngineKind> _byVolume = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>卷键 → 该卷的解析说明（降级原因等，供输出展示）。</summary>
    private readonly Dictionary<string, string> _notes = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();

    public EnumEngineSelector(EnumEngineKind requested, int bufferSize)
    {
        _requested = requested;
        _bufferSize = bufferSize > 0 ? bufferSize : DefaultBufferSize;
    }

    public EnumEngineKind Requested => _requested;

    public int BufferSize => _bufferSize;

    /// <summary>
    /// 解析某个目录应使用的枚举引擎。按**卷**缓存（同一卷的所有目录共用一次探测结果），
    /// 多线程并发调用安全（探测本身在锁内串行，但正常只发生一次）。
    /// </summary>
    /// <param name="dir">目录路径（用于定位所属卷）。</param>
    /// <param name="note">该卷的解析说明（降级原因）；首次解析时回传，命中缓存时为 null。</param>
    public EnumEngineKind Resolve(string dir, out string? note)
    {
        string volumeKey = VolumeKeyOf(dir);

        lock (_sync)
        {
            if (_byVolume.TryGetValue(volumeKey, out var cached))
            {
                note = null;
                return cached;
            }

            string? drive = DriveOf(dir);
            // 探测目标优先用卷根（如 "C:\"）：它最稳定、几乎总能枚举；给出的具体目录可能是
            // 权限受限的子目录，用它探测会把「读不了」误当成「信息类不支持」。
            string probeTarget = drive != null ? drive + ":\\" : dir;

            EnumEngineKind resolved = _requested;
            string detail;

            if (_requested == EnumEngineKind.Win32)
            {
                detail = Lang.T("按 --enum-engine=win32 强制 Win32 枚举器",
                                "forced to the Win32 enumerator by --enum-engine=win32");
            }
            else
            {
                resolved = Probe(probeTarget, _requested, out detail);
            }

            _byVolume[volumeKey] = resolved;
            _notes[volumeKey] = detail;
            note = detail;
            return resolved;
        }
    }

    /// <summary>各卷的解析结果摘要（扫描结束后打印，便于核对实际用了哪一档）。</summary>
    public string DescribeAll()
    {
        lock (_sync)
        {
            if (_byVolume.Count == 0) return Lang.T("（未解析）", "(not resolved)");

            var parts = new List<string>();
            foreach (var kv in _byVolume) parts.Add(kv.Key + " " + Describe(kv.Value));
            return string.Join(Lang.T("　", "  "), parts);
        }
    }

    /// <summary>各卷降级/说明明细（只回传有降级或不寻常情形的）。</summary>
    public List<string> Notes()
    {
        lock (_sync)
        {
            var list = new List<string>();
            foreach (var kv in _notes)
            {
                if (kv.Value.Length == 0) continue;
                // 卷键本身以 ':' 结尾（"C:"）⇒ 不再叠一个全角冒号，否则会出现 "C:："
                string sep = kv.Key.EndsWith(":", StringComparison.Ordinal) ? " " : Lang.T("：", ": ");
                list.Add(kv.Key + sep + kv.Value);
            }
            return list;
        }
    }

    /// <summary>执行一次降级探测：按 requested 决定的候选顺序逐个试，返回第一个可用的档位。</summary>
    private EnumEngineKind Probe(string dir, EnumEngineKind requested, out string detail)
    {
        // 候选顺序：auto 走完整降级链；强制档只试它自己，失败即降到 Win32（并明确告知）
        int[] candidates;
        switch (requested)
        {
            case EnumEngineKind.Extd: candidates = new[] { NtDirectoryEnumerator.FileIdExtdDirectoryInformation }; break;
            case EnumEngineKind.Both: candidates = new[] { NtDirectoryEnumerator.FileIdBothDirectoryInformation }; break;
            default: candidates = new[]
            {
                NtDirectoryEnumerator.FileIdExtdDirectoryInformation,
                NtDirectoryEnumerator.FileIdBothDirectoryInformation
            }; break;
        }

        foreach (int infoClass in candidates)
        {
            if (!TryProbe(dir, infoClass)) continue;

            detail = requested == EnumEngineKind.Auto
                ? (infoClass == NtDirectoryEnumerator.FileIdExtdDirectoryInformation
                    ? Lang.T("自动探测：FileIdExtdDirectoryInformation(60) 可用",
                             "auto probe: FileIdExtdDirectoryInformation(60) available")
                    : Lang.T("自动探测：60 不可用，已降级到 FileIdBothDirectoryInformation(37)",
                             "auto probe: 60 unavailable, degraded to FileIdBothDirectoryInformation(37)"))
                : Lang.T("已实测确认可用", "measured as available");
            return infoClass == NtDirectoryEnumerator.FileIdExtdDirectoryInformation
                ? EnumEngineKind.Extd : EnumEngineKind.Both;
        }

        detail = requested == EnumEngineKind.Auto
            ? Lang.T("自动探测：60 与 37 均不可用，已降级到 Win32（逐文件开句柄）",
                     "auto probe: neither 60 nor 37 is available, degraded to Win32 (per-file handles)")
            : Lang.F("强制 {0} 但实测失败，已降级到 Win32（逐文件开句柄）",
                     "forced {0} but the probe failed, degraded to Win32 (per-file handles)",
                     Describe(requested));
        return EnumEngineKind.Win32;
    }

    /// <summary>
    /// 试枚举一次（只取第一条）。不抛异常：返回 false 专指「该信息类不被接受」。
    /// 空目录返回 false 的 MoveNext 也算成功 —— 说明信息类被接受了，只是没有子项。
    /// </summary>
    private bool TryProbe(string dir, int infoClass)
    {
        try
        {
            using var e = NtDirectoryEnumerator.Enumerate(dir, false, infoClass, DefaultBufferSize).GetEnumerator();
            e.MoveNext();
            return true;
        }
        catch (NotSupportedException)
        {
            return false;   // 信息类不被这个卷接受 ⇒ 继续降级
        }
        catch
        {
            // 目录打不开（权限 / 不存在 / 其他 IO）：**无法据此判定信息类支持性**。
            // 保守地按「支持」处理，把真实错误留给扫描阶段按原有语义记录；
            // 否则一次权限问题就会把整卷永久降级到最慢的 Win32 路径。
            return true;
        }
    }

    /// <summary>档位的可读描述（按当前输出语言，见 <see cref="Lang"/>）。</summary>
    public static string Describe(EnumEngineKind kind)
    {
        switch (kind)
        {
            case EnumEngineKind.Extd:
                return Lang.T("FileIdExtd(60) 目录项直出 128 位 FileId",
                              "FileIdExtd(60) - 128-bit FileId straight from directory entries");
            case EnumEngineKind.Both:
                return Lang.T("FileIdBoth(37) 目录项直出 64 位 FileId",
                              "FileIdBoth(37) - 64-bit FileId straight from directory entries");
            case EnumEngineKind.Win32:
                return Lang.T("Win32 FindFirstFileExW + 逐文件句柄",
                              "Win32 FindFirstFileExW + per-file handles");
            default:
                return Lang.T("自动（按卷探测 60 → 37 → Win32）",
                              "auto (per-volume probing 60 -> 37 -> Win32)");
        }
    }

    /// <summary>命令行取值解析。</summary>
    public static bool TryParse(string? text, out EnumEngineKind kind)
    {
        kind = EnumEngineKind.Auto;
        if (string.IsNullOrWhiteSpace(text)) return false;
        switch (text.Trim().ToLowerInvariant())
        {
            case "auto": kind = EnumEngineKind.Auto; return true;
            case "extd": kind = EnumEngineKind.Extd; return true;
            case "both": kind = EnumEngineKind.Both; return true;
            case "win32": kind = EnumEngineKind.Win32; return true;
            default: return false;
        }
    }

    /// <summary>该档位是否需要「目录项里的 FileId」（false = 走逐文件句柄的旧路径）。</summary>
    public static bool UsesEntryFileId(EnumEngineKind kind)
        => kind == EnumEngineKind.Extd || kind == EnumEngineKind.Both;

    /// <summary>从 "C:\dir" 取出 "C"；非盘符路径返回 null。</summary>
    private static string? DriveOf(string path)
        => path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0])
            ? char.ToUpperInvariant(path[0]).ToString()
            : null;

    /// <summary>
    /// 缓存键 = 所属卷的标识。盘符路径用盘符；非盘符路径（UNC 等）用前三级路径，
    /// 避免不同服务器/共享被误当成同一个卷而共享探测结论。
    /// </summary>
    private static string VolumeKeyOf(string path)
    {
        string? drive = DriveOf(path);
        if (drive != null) return drive + ":";

        int cuts = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] != '\\') continue;
            if (++cuts == 3) return path.Substring(0, i);
        }
        return path;
    }
}
