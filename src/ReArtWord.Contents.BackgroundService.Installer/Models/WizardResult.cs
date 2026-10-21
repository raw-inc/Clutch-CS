using ReArtWord.Contents.BackgroundService.Contracts.Installation;

namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>完了画面の文言と、確定したWebクライアントの情報です。</summary>
internal sealed record WizardResult(string Summary, string Details, WebClientInstallation? WebClient = null);
