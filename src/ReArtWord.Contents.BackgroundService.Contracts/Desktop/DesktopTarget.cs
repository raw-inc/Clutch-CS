namespace ReArtWord.Contents.BackgroundService.Contracts.Desktop;

/// <summary>クラスタの任意指定と、必須の接続先ホストです。</summary>
/// <param name="HostId">利用者が選択した物理ホスト。</param>
/// <param name="ClusterId">指定時はこのクラスタだけを使用します。</param>
public sealed record DesktopTarget(string HostId, string? ClusterId = null);
