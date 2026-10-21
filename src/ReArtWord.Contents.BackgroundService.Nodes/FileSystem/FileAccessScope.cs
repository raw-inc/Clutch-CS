namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem;

/// <summary>ファイル操作が排他制御するパスとアクセス方向です。</summary>
/// <param name="Path">正規化済みの対象パス。</param>
/// <param name="Write">変更を伴うか。</param>
public sealed record FileAccessScope(string Path, bool Write);
