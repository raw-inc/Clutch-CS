using Xunit;

namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>Windows専用のデータ駆動試験を、他のOSでは理由付きで除外します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class WindowsTheoryAttribute : TheoryAttribute
{
    /// <summary>試験を実行できるOSか確認します。</summary>
    public WindowsTheoryAttribute()
    {
        Skip = WindowsFactAttribute.GetSkipReason();
    }
}
