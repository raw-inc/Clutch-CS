using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

/// <summary>スクリプトの導入操作と実行環境の一つのライフサイクルを所有します。</summary>
internal sealed class ScriptServices : IAsyncDisposable
{
    /// <summary>同じPeer領域の環境設定をパッケージ操作と共有します。</summary>
    internal ScriptServices(string dataDirectory)
    {
        Packages = new(dataDirectory);
        Runtimes = new(dataDirectory, Windows.ScriptRuntimeUserProfile.Read);
        Management = new(Packages, runtimes: Runtimes);
        Installer = new(dataDirectory, Runtimes);
    }

    /// <summary>保存済みスクリプトが固定するパッケージ構成です。</summary>
    internal ScriptPackageStore Packages
    {
        get;
    }

    /// <summary>受付済みのパッケージ操作を完了まで所有します。</summary>
    internal ScriptPackageManager Management
    {
        get;
    }

    /// <summary>明示・既定・検出した実行先を選びます。</summary>
    internal ScriptRuntimeRegistry Runtimes
    {
        get;
    }

    /// <summary>公式Python・Node.jsの追加導入を所有します。</summary>
    internal ScriptRuntimeInstaller Installer
    {
        get;
    }

    /// <summary>実行環境の不足も現在コードに対応する解析診断として返します。</summary>
    /// <returns>元の要求指紋を維持した型解析です。</returns>
    internal async Task<ScriptAnalysis> AnalyzeAsync(ScriptAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        string prefix = "script.runtime.";
        try
        {
            ScriptPackageEnvironment environment = await Runtimes.ForAnalysisAsync(request, Packages, cancellationToken).ConfigureAwait(false);
            return await Nodes.Scripting.ScriptAnalysisService.AnalyzeAsync(request, environment, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith(prefix, StringComparison.Ordinal))
        {
            return new(ContractJson.Hash(request), null, [exception.Message]);
        }
    }

    /// <summary>実行環境を破棄する前に所有している操作を停止します。</summary>
    /// <returns>バックグラウンド操作の終了確認です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名であり、所有する処理の取消を管理側へ委譲します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public async ValueTask DisposeAsync()
    {
        await Management.DisposeAsync().ConfigureAwait(false);
        await Installer.DisposeAsync().ConfigureAwait(false);
        Runtimes.Dispose();
    }
}
