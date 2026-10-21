using Microsoft.AspNetCore.Components;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>ブラウザーを開かず、描画試験に現在のURLと移動先を提供します。</summary>
internal sealed class ClientTestNavigation : NavigationManager
{
    /// <summary>試験用URLを初期化します。</summary>
    internal ClientTestNavigation(string uri = "http://localhost/") => Initialize("http://localhost/", uri);

    /// <summary>要求先だけを記録し、実際のネットワークや画面を操作しません。</summary>
    protected override void NavigateToCore(string uri, bool forceLoad)
    {
        Uri = ToAbsoluteUri(uri).ToString();
        NotifyLocationChanged(false);
    }
}
