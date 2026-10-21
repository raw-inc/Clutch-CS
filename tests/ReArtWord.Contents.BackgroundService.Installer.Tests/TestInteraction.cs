using ReArtWord.Contents.BackgroundService.Installer.Services;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>OS対話が起動しないUI試験境界です。</summary>
internal sealed class TestInteraction : IInstallerInteraction
{
    /// <summary>OSを再起動せず、成功・失敗・応答待ちを再現します。</summary>
    public Func<CancellationToken, Task> RestartBehavior
    {
        get; init;
    } = _ => Task.CompletedTask;

    /// <summary>明示された再起動要求の回数です。</summary>
    public int RestartRequests
    {
        get; private set;
    }

    /// <summary>対話終了要求の回数です。</summary>
    public int CloseRequests
    {
        get; private set;
    }

    /// <summary>管理画面を開くよう要求された導入先です。</summary>
    public string? OpenedClientRoot
    {
        get; private set;
    }

    /// <summary>folder選択の取消を返します。</summary>
    /// <returns>未選択です。</returns>
    public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    /// <summary>画像選択の取消を返します。</summary>
    /// <returns>未選択です。</returns>
    public Task<string?> PickImageAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    /// <summary>OSの再起動は実施しません。</summary>
    /// <returns>試験境界の完了です。</returns>
    public Task RestartAsync(CancellationToken cancellationToken = default)
    {
        RestartRequests++;
        return RestartBehavior(cancellationToken);
    }

    /// <summary>外部のclientは開始しません。</summary>
    public void OpenClient(string root) => OpenedClientRoot = root;

    /// <summary>テストsessionがwindowを所有します。</summary>
    public void Close()
    {
        CloseRequests++;
    }
}
