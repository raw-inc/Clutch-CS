using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.State;

/// <summary>状態の比較更新の実行結果です。</summary>
/// <param name="Applied">比較が成立して保存したか。</param>
/// <param name="Current">現在の状態。</param>
/// <param name="Outcome">保存の確認結果。</param>
/// <param name="Reason">失敗または不明の理由。</param>
/// <param name="LocalOnly">共有保存ではなく離脱側のローカル保存です。</param>
public sealed record StateWriteResult(
    bool Applied,
    VersionedState? Current,
    EOutcome Outcome,
    string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LocalOnly = false);
