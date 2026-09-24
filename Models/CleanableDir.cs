namespace DiskGrowthMonitor.Models;

/// <summary>
/// 报告「可安全清理的目录」清单中的一行：本轮扫描到的某个可安全清理目录及其子树占用。
///
/// 清单一律只收录<b>最外层</b>目录：若某条命中目录是另一条的子孙，其占用已包含在祖先条目内，
/// 重复计入会使合计虚高（例如 <c>*\AppData\Local\Temp</c> 与 <c>*\AppData\Local\Temp\Low</c>
/// 都会命中 <c>…\Temp\Low</c> 这一个目录）。
/// </summary>
/// <param name="Path">目录完整路径。</param>
/// <param name="Bytes">该目录子树占用（字节，已硬链接去重、已剔除被排除的子目录）。</param>
/// <param name="Entry">命中的知识库条目，提供用途短名、说明与可清理性。</param>
public sealed record CleanableDir(string Path, long Bytes, DirKnowledge Entry);
