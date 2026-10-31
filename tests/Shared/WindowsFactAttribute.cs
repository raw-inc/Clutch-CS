using Xunit;

namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>Windows専用の試験を、他のOSでは理由付きで除外します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class WindowsFactAttribute : FactAttribute
{
    /// <summary>試験に必要なWindowsのバージョンを確認します。</summary>
    /// <param name="minimumWindows10Build">Windows 10以降で必要なビルド番号です。省略時はWindows全般を対象にします。</param>
    public WindowsFactAttribute(int minimumWindows10Build = 0)
    {
        MinimumWindows10Build = minimumWindows10Build;
        Skip = GetSkipReason(minimumWindows10Build);
    }

    /// <summary>試験に必要なWindows 10以降のビルド番号です。</summary>
    public int MinimumWindows10Build
    {
        get;
    }

    /// <summary>必要なWindowsの条件を満たさない場合は理由を返します。</summary>
    /// <returns>除外理由、または実行可能を表すnullです。</returns>
    internal static string? GetSkipReason(int minimumWindows10Build = 0)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Windows専用の試験です。";
        }
        if (minimumWindows10Build != 0 && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, minimumWindows10Build))
        {
            return $"Windows 10のビルド{minimumWindows10Build}以降が必要です。";
        }
        return null;
    }
}
