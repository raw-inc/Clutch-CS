using Xunit;

namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>Windowsの条件と、ホステッドCIでの画面・音声の利用可否を確認します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class HostedDesktopTheoryAttribute : TheoryAttribute
{
    /// <summary>OS条件を確認し、ホステッドCIでは不足する機能も調べます。</summary>
    public HostedDesktopTheoryAttribute(int minimumWindows10Build = 0)
    {
        MinimumWindows10Build = minimumWindows10Build;
        Skip = HostedDesktopFactAttribute.GetSkipReason(minimumWindows10Build: minimumWindows10Build);
    }

    /// <summary>試験に必要なWindows 10以降のビルド番号です。</summary>
    public int MinimumWindows10Build
    {
        get;
    }
}
