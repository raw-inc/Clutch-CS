namespace ReArtWord.Contents.BackgroundService.Contracts.Targets;

/// <summary>低負荷PCの選択に使用するリソースです。</summary>
public enum EPcResource
{
    /// <summary>CPUの使用率です。</summary>
    Cpu,

    /// <summary>物理メモリの使用率または使用量です。</summary>
    Ram,

    /// <summary>GPUの使用率です。</summary>
    Gpu,

    /// <summary>専用VRAMの使用率または使用量です。</summary>
    Vram,
}
