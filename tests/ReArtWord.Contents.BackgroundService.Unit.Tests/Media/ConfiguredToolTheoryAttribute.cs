using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Media;

/// <summary>実行ファイルが未指定の試験を、理由付きで除外します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ConfiguredToolTheoryAttribute : TheoryAttribute
{
    /// <summary>実行ファイルの指定を確認します。指定されたパスの不備は試験で検出します。</summary>
    public ConfiguredToolTheoryAttribute(string environmentVariable)
    {
        EnvironmentVariable = environmentVariable;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(environmentVariable)))
        {
            Skip = environmentVariable + " に試験用の実行ファイルを指定してください。";
        }
    }

    /// <summary>試験用の実行ファイルを指定する環境変数です。</summary>
    public string EnvironmentVariable
    {
        get;
    }
}
