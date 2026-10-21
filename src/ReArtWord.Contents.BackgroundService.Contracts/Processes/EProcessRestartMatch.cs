namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>自律再起動した後継プロセスの照合条件です。</summary>
public enum EProcessRestartMatch
{
    /// <summary>同じ実行ファイルと同じ引数の並びを要求します。</summary>
    ExecutableAndArguments,

    /// <summary>引数変更を許容し、同じ実行ファイルを要求します。</summary>
    ExecutableOnly,
}
