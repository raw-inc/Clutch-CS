namespace ReArtWord.Contents.BackgroundService.Contracts.Targets;

/// <summary>比較するリソース、単位、GPUを指定します。</summary>
/// <param name="Resource">比較対象のリソース。</param>
/// <param name="UsedBytes">RAM・VRAMを使用率ではなく使用バイト数で比較するか。</param>
/// <param name="GpuId">対象GPUの識別子。省略時はPC内の最大値を使います。</param>
public sealed record PcResourceChoice(EPcResource Resource, bool UsedBytes = false, string? GpuId = null);
