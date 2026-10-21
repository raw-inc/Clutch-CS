using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>検出開始時の照合方針と期限を固定し、引数そのものを重複保存しません。</summary>
internal sealed record ProcessReplacementWindow(DateTimeOffset Deadline, DateTimeOffset NotBefore, string ExecutablePath, string ArgumentsHash, EProcessRestartMatch Match,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? SessionId = null);
