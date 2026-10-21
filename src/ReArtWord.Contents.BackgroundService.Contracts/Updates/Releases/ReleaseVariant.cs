namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;

/// <summary>同一リリースの特定RID向けマニフェストとbundle内ペイロードです。</summary>
/// <param name="Manifest">RID固有ファイル一覧。リリース IDと互換集合は共通です。</param>
/// <param name="RelativePayloadPath">要求のPayloadRootからの相対ディレクトリです。</param>
public sealed record ReleaseVariant(ReleaseManifest Manifest, string RelativePayloadPath);
