namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

/// <summary>バックエンド内で識別できるキャプチャ対象です。</summary>
internal sealed record CaptureAdapter(string Id, string Name, string Details)
{
    /// <summary>一覧に表示する名称を返します。</summary>
    /// <returns>識別子付き名称。</returns>
    public override string ToString() => Name;
}
