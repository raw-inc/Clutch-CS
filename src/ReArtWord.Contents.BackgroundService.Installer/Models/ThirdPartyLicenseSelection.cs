namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>ライブラリに含まれるコードのライセンス選択です。</summary>
/// <param name="Name">元のライブラリ名です。</param>
/// <param name="License">配布元が提示する選択肢です。</param>
/// <param name="SelectedLicense">Clutch-CS が採用した条件です。</param>
internal sealed record ThirdPartyLicenseSelection(string Name, string License, string SelectedLicense);
