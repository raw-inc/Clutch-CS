using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;

namespace ReArtWord.Contents.BackgroundService.Runtime.Configuration;

/// <summary>未来の一件へ適用する共通運用版を取得します。</summary>
public interface IClusterOperationalProvider
{
    /// <summary>現在の版を所有コピーし、未初期化をTOML既定へ置換しません。</summary>
    /// <returns>不変として保持する一件の版、または利用不能のnullです。</returns>
    public ClusterOperationalSnapshot? Capture();
}
