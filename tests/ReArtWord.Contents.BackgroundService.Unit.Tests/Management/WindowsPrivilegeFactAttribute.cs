using ReArtWord.Contents.BackgroundService.CoreService.Windows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>Windowsの実効権限が試験条件と異なる場合、理由を記録してスキップします。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class WindowsPrivilegeFactAttribute : FactAttribute
{
    /// <summary>実行中の権限と、必要な場合は実機試験の有効化設定を確認します。</summary>
    public WindowsPrivilegeFactAttribute(bool administrator, string? environmentVariable = null)
    {
        Administrator = administrator;
        EnvironmentVariable = environmentVariable;
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows専用の試験です。";
        }
        else if (WindowsInteractiveIdentity.IsAdministrator() != administrator)
        {
            Skip = administrator ? "管理者として起動したテストプロセスが必要です。"
                                 : "管理者へ昇格していないテストプロセスが必要です。";
        }
        else if (environmentVariable is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
        {
            Skip = environmentVariable + " を設定して実機試験を有効にしてください。";
        }
    }

    /// <summary>試験が要求する管理者権限の有無です。</summary>
    public bool Administrator
    {
        get;
    }

    /// <summary>実機試験を明示的に有効にする場合の環境変数です。</summary>
    public string? EnvironmentVariable
    {
        get;
    }
}
