namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>候補値と実行先の観測結果です。</summary>
internal sealed record SettingNetworkChoices(string? PeerId, IReadOnlyList<SettingNetworkInterface> Interfaces, string? Problem);
