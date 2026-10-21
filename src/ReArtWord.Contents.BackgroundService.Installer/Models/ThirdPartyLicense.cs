namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>配布物に含まれる依存ライブラリとライセンス原文です。</summary>
/// <param name="Name">ライブラリ名です。</param>
/// <param name="Version">配布するバージョンです。</param>
/// <param name="License">配布元のライセンス表記です。</param>
/// <param name="Text">著作権表記を含むライセンス原文です。</param>
/// <param name="SelectedLicense">選択式のライセンスから採用した条件です。</param>
/// <param name="LicenseSelections">ライブラリに含まれるコードの採用条件です。</param>
internal sealed record ThirdPartyLicense(string Name, string Version, string License, string Text, string? SelectedLicense = null, ThirdPartyLicenseSelection[]? LicenseSelections = null);
