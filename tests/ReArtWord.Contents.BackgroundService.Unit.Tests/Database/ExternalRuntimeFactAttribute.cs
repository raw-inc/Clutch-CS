using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Database;

/// <summary>外部試験用接続先がない環境では、実接続済みと数えず明示skipにします。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ExternalRuntimeFactAttribute : FactAttribute
{
    /// <summary>試験環境変数の指定を確認します。</summary>
    public ExternalRuntimeFactAttribute(string environmentVariable)
    {
        EnvironmentVariable = environmentVariable;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
        {
            Skip = environmentVariable + " must identify an isolated test runtime.";
        }
    }

    /// <summary>実相手の接続情報を指定する環境変数です。</summary>
    public string EnvironmentVariable
    {
        get;
    }
}
