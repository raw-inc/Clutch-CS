namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Gpu;

/// <summary>DXGIで確認した現在のhardware 接続実装です。</summary>
internal sealed record GpuAdapterIdentity(string Luid, string Name, uint VendorId, ulong? DedicatedBytes = null, ulong? SharedLimitBytes = null);
