using DiskGrowthMonitor.Util;

namespace DiskGrowthMonitor.Models;

/// <summary>
/// 目录用途知识库条目（dir_knowledge 表的一行）。
///
/// 用途：给报告中的已知目录附上「这是什么 / 能不能删」的说明，让读者不必再猜
/// <c>C:\Users\x\AppData\Local\Google\Chrome\User Data\Default\Cache</c> 到底是什么。
///
/// 内置条目（<see cref="IsBuiltin"/>=true）由程序启动时按需补齐：<b>仅在 pattern 不存在时插入，
/// 绝不覆盖用户对已有条目的修改</b>。因此可以直接用任意 SQLite 工具增删改，升级程序不会冲掉编辑。
/// 本表属配置数据，<c>--reset-db</c> 不会清空。
/// </summary>
public sealed class DirKnowledge
{
    /// <summary>自增主键（新增时无需填写）。</summary>
    public long Id { get; set; }

    /// <summary>
    /// 路径模式，<b>后缀对齐</b>匹配（从路径尾部逐段比较）。支持：
    /// <c>*</c>=任意单段；<c>**</c>=任意多段（含 0 段）；<c>%VAR%</c>=按当前环境变量展开。
    /// 例：<c>*\AppData\Local\Temp</c> 同时命中 <c>C:\Users\henry\AppData\Local\Temp</c>
    /// 与 <c>D:\Users\bob\AppData\Local\Temp</c>。
    /// </summary>
    public required string Pattern { get; init; }

    /// <summary>分类：浏览器 / 系统缓存 / 日志 / 系统组件 / 用户数据 / 应用数据 / 开发工具。</summary>
    public required string Category { get; init; }

    /// <summary>用途短名，例如「Chrome 网页缓存」。显示在报告路径单元格内。</summary>
    public required string Title { get; init; }

    /// <summary>一句话说明（报告中鼠标悬停时显示完整内容）。</summary>
    public string? Note { get; init; }

    // ---------------------------------------------------------------- 英文列（第 57 轮新增）
    // 知识库描述属于**数据**，不随界面语言自动翻译 —— 它们有自己的英文列（dir_knowledge 的
    // category_en / title_en / note_en）。用户自定义条目可以只填中文，此时英文档回退显示中文原文
    // （宁可显示中文，也不要一个空白的用途列）。

    /// <summary>英文分类（可空；空则回退 <see cref="Category"/>）。</summary>
    public string? CategoryEn { get; init; }

    /// <summary>英文用途短名（可空；空则回退 <see cref="Title"/>）。</summary>
    public string? TitleEn { get; init; }

    /// <summary>英文说明（可空；空则回退 <see cref="Note"/>）。</summary>
    public string? NoteEn { get; init; }

    /// <summary>按当前输出语言取分类。</summary>
    public string CategoryText => Pick(CategoryEn, Category);

    /// <summary>按当前输出语言取用途短名。</summary>
    public string TitleText => Pick(TitleEn, Title);

    /// <summary>按当前输出语言取说明。</summary>
    public string NoteText => Pick(NoteEn, Note);

    /// <summary>英文档取英文列（缺失回退原文），中文档恒取原文。</summary>
    private static string Pick(string? english, string? chinese)
    {
        if (!Lang.IsEnglish) return chinese ?? string.Empty;
        return string.IsNullOrEmpty(english) ? (chinese ?? string.Empty) : english!;
    }

    /// <summary>可清理性：安全 / 谨慎 / 禁止。为空表示未评估。</summary>
    public string? Cleanable { get; init; }

    /// <summary>命中多条时取数值大者（数值越大越优先，同值时取模式更具体的一条）。</summary>
    public int Priority { get; init; } = 50;

    /// <summary>是否为内置条目（true=内置，false=用户自定义）。仅影响展示，不影响匹配。</summary>
    public bool IsBuiltin { get; init; } = true;

    /// <summary>是否参与匹配（false 时条目保留在库中但不生效）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>参考来源，便于溯源核对。</summary>
    public string? Source { get; init; }

    /// <summary>
    /// 可清理性的展示文本（报告中显示在用途短名之后）。
    ///
    /// 注意 <c>Cleanable</c> 本身是**数据**（存在库里、被 <c>CleanupAdvisor</c> 比较），
    /// 取值保持稳定不变（安全/谨慎/禁止）；这里只做展示翻译，不动数据。
    /// </summary>
    public string CleanableText => Cleanable switch
    {
        "安全" => Lang.T("可安全清理", "safe to clean"),
        "谨慎" => Lang.T("谨慎清理", "clean with care"),
        "禁止" => Lang.T("禁止删除", "do not delete"),
        _ => string.Empty
    };

    /// <summary>可清理性对应的样式类名（无评估时为空串）。</summary>
    public string CleanableCss => Cleanable switch
    {
        "安全" => "clean-safe",
        "谨慎" => "clean-cautious",
        "禁止" => "clean-forbidden",
        _ => string.Empty
    };
}

/// <summary>一次目录用途匹配的结果。</summary>
/// <param name="Entry">命中的知识条目。</param>
/// <param name="MatchedSegments">参与匹配的模式段数（越多越具体，用于同优先级仲裁）。</param>
public sealed record KnowledgeMatch(DirKnowledge Entry, int MatchedSegments);
