namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>エディター上のノードの位置です。</summary>
/// <param name="X">横位置。</param>
/// <param name="Y">縦位置。</param>
public sealed record NodePosition(
    double X,
    double Y);

