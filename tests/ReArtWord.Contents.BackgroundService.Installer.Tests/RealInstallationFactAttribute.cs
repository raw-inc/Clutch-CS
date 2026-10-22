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
            // ユーザーのログオンタスクを実際に登録・起動する試験はSession 0では検証できません。
            // 配布物生成後にも対象に含め、未実施の理由をTRXとCIサマリーに残します。
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
            if (process.SessionId == 0)
            {
                Skip = "Not verified: user logon-task startup requires a signed-in Windows session; the test process is in Session 0.";
            }
        }
    }

    /// <summary>サインイン中のWindowsセッションが必要な試験です。</summary>
    public bool Interactive
    {
        get;
    }
}
