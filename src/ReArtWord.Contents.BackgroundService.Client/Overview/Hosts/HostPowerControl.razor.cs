using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;

/// <summary>PCの状態表示と、対象実体に結び付けた電源操作の確認を管理します。</summary>
// 抑制理由: Razorが生成する公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言に合わせます。")]
public sealed partial class HostPowerControl
{
    /// <summary>表示対象PCです。</summary>
    [Parameter] public string HostId
    {
        get;
        set;
    } = string.Empty;

    /// <summary>親が選んだ実体の識別子です。再起動や接続先変更時に確認を失効させます。</summary>
    [Parameter] public string? TargetIdentity
    {
        get;
        set;
    }

    /// <summary>現在観測した参加状態です。</summary>
    [Parameter] public EPeerStatus Status
    {
        get;
        set;
    }

    /// <summary>前回値であることを表示します。</summary>
    [Parameter] public bool Stale
    {
        get;
        set;
    }

    /// <summary>全参加Peerとの接続が途絶えた保存PCです。</summary>
    [Parameter] public bool Offline
    {
        get;
        set;
    }

    /// <summary>親が操作の結果を確認している間は新しい要求を抑止します。</summary>
    [Parameter] public bool Busy
    {
        get;
        set;
    }

    /// <summary>確認されたrestart、shutdown、wake、removeだけを親へ渡します。</summary>
    [Parameter] public EventCallback<string> ActionConfirmed
    {
        get;
        set;
    }

    /// <summary>親が確認した操作上の問題です。</summary>
    [Parameter] public string? Problem
    {
        get;
        set;
    }

    [Inject] private IJSRuntime Script
    {
        get;
        set;
    } = null!;

    private readonly string panelId = "host-power-" + Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim rendering = new(1, 1);
    private ElementReference root;
    private IJSObjectReference? module, control;
    private DotNetObjectReference<HostPowerControl>? reference;
    private bool dispatching;
    private static readonly string[] powerActions = ["restart", "shutdown"];
    private static readonly string[] offlineActions = ["remove", "wake"];
    private bool CanAct => Offline || Status is EPeerStatus.Ready or EPeerStatus.Syncing;
    private string HealthClass => Offline ? "warn offline"
                                          : !Stale && Status == EPeerStatus.Ready ? "good"
                                                                                  : "warn";
    private string StatusLabel => Offline ? Ui["Offline"]
                                          : Stale ? Ui["Previous / unconfirmed"]
                                                  : Ui[Status.ToString()];

    /// <summary>実体の変更をブラウザーの確認状態へ反映し、共通時計へ発光を同期します。</summary>
    /// <returns>今回の表示との同期です。</returns>
    // 抑制理由: Blazorの固定署名で画面寿命のトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名で画面寿命のトークンを渡します。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await rendering.WaitAsync(UiCancellation).ConfigureAwait(true);
        try
        {
            if (ComponentDisposed)
            {
                return;
            }
            module ??= await Script.InvokeAsync<IJSObjectReference>("import", UiCancellation, "./host-power-control.js").ConfigureAwait(true);
            reference ??= DotNetObjectReference.Create(this);
            control ??= await module.InvokeAsync<IJSObjectReference>("attachHostPowerControl", UiCancellation, root, reference).ConfigureAwait(true);
            await control.InvokeVoidAsync("update", UiCancellation, new { HostId, TargetIdentity, Status = Status.ToString(), Stale, Offline, Busy = Busy || dispatching }).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ComponentDisposed)
        {
        }
        finally
        {
            rendering.Release();
        }
    }

    /// <summary>表示後に対象や可用性が変わった確認を拒否し、一度だけ親へ通知します。</summary>
    /// <returns>親の操作受付の完了です。</returns>
    // 抑制理由: JSInvokableの署名で、ネットワーク処理自体は親が所有します。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "JSInvokableの固定境界で、親所有のEventCallbackだけを通知します。")]
    public async Task ConfirmAsync(string action, string hostId, string? targetIdentity)
    {
        if (ComponentDisposed || dispatching || Busy || !CanAct || !string.Equals(hostId, HostId, StringComparison.Ordinal)
            || !string.Equals(targetIdentity, TargetIdentity, StringComparison.Ordinal)
            || !((Offline && offlineActions.Contains(action, StringComparer.Ordinal)) || (!Offline && powerActions.Contains(action, StringComparer.Ordinal))))
        {
            return;
        }
        dispatching = true;
        try
        {
            await ActionConfirmed.InvokeAsync(action).ConfigureAwait(true);
        }
        finally
        {
            dispatching = false;
        }
    }

    /// <summary>時限・イベント・アニメーションと相互参照を終了します。</summary>
    /// <returns>所有するブラウザー資源の解放です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(true);
        await rendering.WaitAsync().ConfigureAwait(true);
        try
        {
            if (control is not null)
            {
                await control.InvokeVoidAsync("dispose").ConfigureAwait(true);
                await control.DisposeAsync().ConfigureAwait(true);
            }
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (JSDisconnectedException)
        {
        }
        finally
        {
            reference?.Dispose();
            rendering.Release();
            rendering.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
