using System.Reflection;
using Xunit.Sdk;

namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>同じTheory内でも、媒体ツールを使うケースだけを除外します。</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class MediaInlineDataAttribute : DataAttribute
{
    private readonly object[] values;

    /// <summary>テキスト処理など、ツールに依存しないInlineDataはそのまま実行できます。</summary>
    public MediaInlineDataAttribute(params object[] values)
    {
        this.values = values;
        Skip = MediaFactAttribute.UnavailableReason;
    }

    /// <summary>このケースの引数です。</summary>
    public IReadOnlyList<object> Values => values;

    /// <summary>元の引数を一ケースとして返します。</summary>
    /// <returns>媒体ツールを必要とするケースです。</returns>
    public override IEnumerable<object[]> GetData(MethodInfo testMethod) => [values];
}
