namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>このPCのWebクライアントを新規導入するか、既存の導入を利用するかです。</summary>
public sealed record WebClientSettings(bool Install, int Port, string? ExistingRoot = null, bool RunAsService = false)
{
    /// <summary>ClientHostとセットアップで共有する既定のHTTP portです。</summary>
    public static int DefaultPort => 5081;
}
