using Xunit;

namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>Windows専用の試験を、他のOSでは理由付きで除外します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class WindowsFactAttribute : FactAttribute
{
    /// <summary>試験を実行できるOSか確認します。</summary>
    public WindowsFactAttribute()
    {
        Skip = GetSkipReason();
    }

    /// <summary>Windows以外では、実行できない理由を返します。</summary>
    /// <returns>除外理由、または実行可能を表すnullです。</returns>
    internal static string? GetSkipReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Windows専用の試験です。";
        }
        return null;
    }
}
