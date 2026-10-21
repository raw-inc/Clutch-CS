using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>配置操作の最終確認結果です。受理だけでは成功を返しません。</summary>
public sealed record SetupResult(Guid OperationId, EOutcome Outcome, string Stage, string Detail, string Root, bool Finished = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WebClientInstallation? WebClient = null);
