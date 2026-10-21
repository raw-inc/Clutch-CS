namespace ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

/// <summary>有限コマンドの出力量とメモリー観測上限です。</summary>
/// <param name="OutputCharacters">保持する標準出力の最大文字数です。</param>
/// <param name="FailOnOutputLimit">超過を途中結果として返さず停止します。</param>
/// <param name="WorkingSetBytes">子プロセスの観測メモリー上限。OSの隔離境界ではありません。</param>
/// <param name="WorkingDirectory">今回の一回が使用する作業ディレクトリーです。</param>
/// <param name="StandardInput">引数へ展開せずUTF-8で渡す有限入力。nullは標準入力を変更しません。</param>
/// <param name="OutputEncoding">子が使用する標準出力・標準エラーの文字コード。未指定は既存の既定です。</param>
/// <param name="EnvironmentOverrides">この子プロセスだけへ適用する値です。nullの値は継承変数を除去します。</param>
public sealed record ProcessRunLimits(int OutputCharacters = 1048576, bool FailOnOutputLimit = false, long? WorkingSetBytes = null, string? WorkingDirectory = null, string? StandardInput = null,
    System.Text.Encoding? OutputEncoding = null, IReadOnlyDictionary<string, string?>? EnvironmentOverrides = null);
