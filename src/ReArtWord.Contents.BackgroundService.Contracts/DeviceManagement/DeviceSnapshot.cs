namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>実取得した設定と内容版を保持します。</summary>
/// <param name="Revision">比較更新に使用する内容識別子。</param>
/// <param name="Data">領域ごとの型付き設定のJSON表現。</param>
public sealed record DeviceSnapshot(string Revision, System.Text.Json.JsonElement Data);
