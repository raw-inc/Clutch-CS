using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>このノードに適用する状況別停止方針です。指定しないノードは継続します。</summary>
/// <param name="StopWhenSingleton">単体時停止。未指定は共通方針に従います。</param>
/// <param name="StopWhenDegraded">複数残存時停止。未指定は共通方針に従います。</param>
/// <param name="Inputs">安全停止中の入力方針。</param>
/// <param name="Group">共通の操作範囲名。</param>
/// <param name="Confirmation">一時操作前の確認方針。未指定は共通設定に従います。</param>
public sealed record NodeSafetyPolicy(
    bool? StopWhenSingleton = null,
    bool? StopWhenDegraded = null,
    EStoppedInputPolicy Inputs = EStoppedInputPolicy.Drop,
    string? Group = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ESafetyConfirmation? Confirmation = null);
