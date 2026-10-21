using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Catalog;

/// <summary>折り畳みの安定キーと表示対象を持つカタログのグループです。</summary>
internal sealed record CatalogGroup(string Key, string Label, string Category, IReadOnlyList<NodeDescriptor> Items);
