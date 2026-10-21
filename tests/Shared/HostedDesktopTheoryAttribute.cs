using Xunit;

namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>画面・音声機能がないホステッドCIで、実デスクトップ試験を明示的に除外します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class HostedDesktopTheoryAttribute : TheoryAttribute
{
    /// <summary>ローカルや実機ランナーでは通常どおり実行します。</summary>
    public HostedDesktopTheoryAttribute()
    {
        Skip = HostedDesktopFactAttribute.GetSkipReason();
    }
}
