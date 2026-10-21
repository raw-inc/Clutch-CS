using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;

namespace ReArtWord.Contents.BackgroundService.CoreService.Workflows;

/// <summary>旧版でも実行の識別子と成否を読める、有限診断リングの保存形式です。</summary>
/// <param name="Sequence">循環スロットの保存世代です。</param>
/// <param name="View">通常結果、または圧縮時の互換要約です。</param>
/// <param name="Payload">別のローカル用途へ同時保存する圧縮詳細。旧readerのJSONへ追加しません。</param>
internal sealed record WorkflowHistoryRecord(long Sequence, ExecutionResultView View,
    [property: JsonIgnore] WorkflowHistoryPayload? Payload = null);
