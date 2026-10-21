using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>明示したWindows配布物があるときだけ実配置試験を実施します。</summary>
internal sealed class RealInstallationFactAttribute : FactAttribute
{
    /// <summary>外部依存なしの通常単体試験と実配置試験を分けます。</summary>
    public RealInstallationFactAttribute(bool interactive = false)
    {
        Interactive = interactive;
        string? package = Environment.GetEnvironmentVariable("CLUTCH_TEST_PACKAGE");
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(package))
        {
            Skip = "Set CLUTCH_TEST_PACKAGE to a Windows install package to run real per-user installation, UDP update, repair and uninstall.";
        }
        if (OperatingSystem.IsWindows() && interactive)
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
            if (process.SessionId == 0)
            {
                Skip = "The user-startup test requires a signed-in Windows session.";
            }
        }
    }

    /// <summary>サインイン中のWindowsセッションが必要な試験です。</summary>
    public bool Interactive
    {
        get;
    }
}
