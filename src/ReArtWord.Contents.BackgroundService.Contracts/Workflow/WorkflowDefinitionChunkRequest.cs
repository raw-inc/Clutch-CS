namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>版を固定した完全定義を、有限バイトの断片として取得します。</summary>
/// <param name="Id">ワークフロー識別子。</param>
/// <param name="Revision">必ず一致させる保存版または実効版。</param>
/// <param name="Effective">実効定義を選ぶか。</param>
/// <param name="Offset">UTF8byteの開始位置。</param>
/// <param name="Length">要求バイト数。最大65536。</param>
public sealed record WorkflowDefinitionChunkRequest(string Id, string Revision, bool Effective = false, int Offset = 0, int Length = 65536);
