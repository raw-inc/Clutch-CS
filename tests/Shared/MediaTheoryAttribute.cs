using Xunit;

namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>全ケースで媒体ツールを使う試験の前提条件を確認します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class MediaTheoryAttribute : TheoryAttribute
{
    /// <summary>デスクトップの実動作試験では、対話環境の条件も維持します。</summary>
    public MediaTheoryAttribute(bool requiresDesktop = false)
    {
        RequiresDesktop = requiresDesktop;
        Skip = MediaFactAttribute.UnavailableReason;
        if (Skip is null && requiresDesktop)
        {
            Skip = HostedDesktopFactAttribute.GetSkipReason();
        }
    }

    /// <summary>画面・音声出力を必要とする試験かどうかです。</summary>
    public bool RequiresDesktop
    {
        get;
    }
}
