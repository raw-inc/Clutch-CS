using System.Runtime.InteropServices;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>公式ZIP導入に対応するOS・CPUと、任意の実接続許可を確認します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RuntimeInstallFactAttribute : FactAttribute
{
    /// <summary>未対応OSを成功扱いにせず、実行条件を明記してskipします。</summary>
    public RuntimeInstallFactAttribute(string? environmentVariable = null)
    {
        EnvironmentVariable = environmentVariable;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            Skip = "Runtime ZIP installation requires Windows x64 or ARM64.";
        }
        if (environmentVariable is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
        {
            Skip = environmentVariable + " must enable the isolated runtime test.";
        }
    }

    /// <summary>外部接続や実ランタイムの検証を有効化する変数です。</summary>
    public string? EnvironmentVariable
    {
        get;
    }
}
