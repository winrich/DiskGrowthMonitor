// ---------------------------------------------------------------------------
// .NET Framework 4.5 缺失的编译器必需类型（polyfill）
//
// C# 9 的 init 访问器 / record，C# 11 的 required 修饰符，都需要若干
// "编译器要求的成员"存在于目标框架里。这些类型只在 .NET 5+ / netstandard2.1+
// 的 BCL 中提供，.NET Framework 4.5 没有，因此必须在项目内自行声明。
//
// ⚠ 本文件与 BCL 是「同名类型」关系：一旦项目重新引入任何 .NET 5+ / netstandard2.1+
//   目标，这里就会与框架自带的 IsExternalInit / RequiredMemberAttribute 冲突（CS0101）。
//   届时必须重新用 #if NETFRAMEWORK 包起来，或改为 InternalsVisibleTo 之外的等价方案。
//   注意这条约束会传导到「引用本项目源码」的验证工程（_optdb/cleantest 就是直接
//   Compile Include 主工程源码），它们的 TFM 也必须同步收敛。
// ---------------------------------------------------------------------------
namespace System.Runtime.CompilerServices
{
    /// <summary>标记 init-only 属性 setter 的 modreq 目标类型。空实现即可。</summary>
    internal static class IsExternalInit
    {
    }

    /// <summary>required 成员的编译期标记（C# 11）。</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field
                    | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    /// <summary>标记"需要编译器特性支持"的成员（C# 11）。</summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName)
        {
            FeatureName = featureName;
        }

        public string FeatureName { get; }

        public bool IsOptional { get; init; }

        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>表示构造函数已为所有 required 成员赋值（C# 11）。</summary>
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}
