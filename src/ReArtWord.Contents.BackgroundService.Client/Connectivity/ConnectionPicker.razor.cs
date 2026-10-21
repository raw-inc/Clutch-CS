using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

namespace ReArtWord.Contents.BackgroundService.Client.Connectivity;

/// <summary>監視ネットワークと管理対象のクラスタ・ピアを選択します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class ConnectionPicker : IAsyncDisposable
{
    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    [Inject]
    private ConnectionState Connection
    {
        get;
        set;
    } = null!;

    [Inject]
    private EditorTargetSelection Selection
    {
        get;
        set;
    } = null!;

    /// <summary>管理対象の変更を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback TargetChanged
    {
        get;
        set;
    }

    /// <summary>現在選択しているクラスタ識別子です。</summary>
    [Parameter]
    public string? ClusterId
    {
        get;
        set;
    }

    /// <summary>現在選択しているピア識別子です。</summary>
    [Parameter]
    public string? PeerId
    {
        get;
        set;
    }

    /// <summary>接続を確認できているかどうかです。</summary>
    [Parameter]
    public bool Connected
    {
        get;
        set;
    }

    private ClientNetworkState? network;

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyList<DiscoveredCluster> clusters = [];
    private string interfaceId = string.Empty;
    private string bindAddress = string.Empty;
    private string broadcast = string.Empty;
    private string portText = string.Empty;
    private string message = string.Empty;
    private bool open;
    private bool busy;
    private readonly CancellationTokenSource lifetime = new();
    private Task polling = Task.CompletedTask;
    private IReadOnlyList<ClientNetworkAddress> Addresses => network?.Interfaces.FirstOrDefault(item => string.Equals(item.InterfaceId, interfaceId, StringComparison.Ordinal))?.Addresses ?? [];
    private IReadOnlyList<ClientNetworkStatus> Bindings => network?.Bindings ?? (network?.Selected is { } selected ? [new(selected, network.Listening, network.Problem)]
                                                                                                                   : []);

    /// <summary>選択したネットワーク割当を編集欄へ読み込みます。</summary>
    private void EditBinding(ClientNetworkBinding binding)
    {
        interfaceId = binding.InterfaceId;
        bindAddress = binding.BindAddress;
        broadcast = binding.BroadcastAddress;
        portText = binding.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>指定した監視割当を保存済みネットワーク集合から除きます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task RemoveBindingAsync(ClientNetworkBinding binding)
    {
        busy = true;
        try
        {
            await Api.PutAsync<JsonElement>("/client/networks", Bindings.Where(item => item.Binding != binding).Select(item => item.Binding).ToArray(), lifetime.Token).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            message = "Could not update monitored networks. Refresh to confirm the current configuration.";
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>初回表示で必要な状態と継続監視を準備します。</summary>
    // 抑制理由: 開始した監視Taskはフィールドで所有し、画面終了時に完了を確認します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "継続監視Taskはpollingで保持し、DisposeAsyncで取消後にawaitします。")]
    protected override void OnInitialized()
    {
        polling = PollAsync();
    }

    /// <summary>対象の現在値を取得して編集または選択画面を開きます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task OpenAsync()
    {
        open = true;
        await RefreshAsync().ConfigureAwait(true);
        if (network?.Selected is { } selected)
        {
            interfaceId = selected.InterfaceId;
            bindAddress = selected.BindAddress;
            broadcast = selected.BroadcastAddress;
            portText = selected.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>NICの選択変更に合わせて利用可能なアドレス候補を更新します。</summary>
    private void InterfaceChanged(ChangeEventArgs args)
    {
        interfaceId = args.Value?.ToString() ?? string.Empty;
        bindAddress = string.Empty;
        broadcast = string.Empty;
    }

    /// <summary>選択したIPv4アドレスに対応する送信先を更新します。</summary>
    private void AddressChanged(ChangeEventArgs args)
    {
        bindAddress = args.Value?.ToString() ?? string.Empty;
        broadcast = Addresses.FirstOrDefault(item => string.Equals(item.Address, bindAddress, StringComparison.Ordinal))?.BroadcastAddress ?? string.Empty;
    }

    /// <summary>明示したNICとIPv4の監視割当を保存します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task BindAsync()
    {
        message = interfaceId.Length == 0 ? "Select a network interface."
                                          : !Ipv4(bindAddress) ? "Select an IPv4 address on this interface."
                                                               : !Ipv4(broadcast) ? "Enter a valid IPv4 broadcast address."
                                                                                  : !int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsedPort) || parsedPort is < 1 or > 65535 ? "Enter a UDP port from 1 to 65535."
                                                                                                                                                                                                                                                           : string.Empty;
        if (message.Length != 0)
        {
            return;
        }

        busy = true;
        try
        {
            ClientNetworkBinding? request = new ClientNetworkBinding(interfaceId, bindAddress, broadcast, int.Parse(portText, System.Globalization.CultureInfo.InvariantCulture));
            ClientNetworkBinding[]? bindings = new[]
            {
                request
            }.Concat(Bindings.Select(item => item.Binding).Where(item => !string.Equals(item.InterfaceId, request.InterfaceId, StringComparison.Ordinal) || item.Port != request.Port)).ToArray();
            await Api.PutAsync<JsonElement>("/client/networks", bindings, lifetime.Token).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            message = network?.Listening == true && network.Selected == request ? "Listening with the selected binding. Choose a cluster and peer."
                                                                                : "The selected binding is not confirmed. Check the bridge binding and reason. No alternate interface or IP was selected.";
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            message = "Listening on this interface is not confirmed. Check the selected binding.";
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>指定した対象を選択し、関連する表示を更新します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task SelectAsync(string clusterId, string peerId, Guid instanceId)
    {
        busy = true;
        try
        {
            await Selection.SelectAsync(clusterId, peerId, instanceId, lifetime.Token).ConfigureAwait(true);
            message = Connection.Connected ? "The selected cluster and peer responded."
                                           : "The selected peer did not respond. No other peer was substituted.";
            await TargetChanged.InvokeAsync().ConfigureAwait(true);
            if (Connection.Connected)
            {
                open = false;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JSException or InvalidOperationException or OperationCanceledException)
        {
            message = "Draft retention or connection could not be confirmed. Check the current selection.";
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>表示対象の最新状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task RefreshAsync()
    {
        try
        {
            network = await Api.GetAsync<ClientNetworkState>("/client/network", lifetime.Token).ConfigureAwait(true);
            clusters = await Api.GetAsync<List<DiscoveredCluster>>("/client/clusters", lifetime.Token).ConfigureAwait(true) ?? [];
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            message = "Could not read ClientHost network and discovery state.";
        }
    }

    /// <summary>画面の存続期間だけ状態を定期確認し、取消要求で終了します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task PollAsync()
    {
        using PeriodicTimer? timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(true))
            {
                if (open && !busy)
                {
                    await RefreshAsync().ConfigureAwait(true);
                    await InvokeAsync(StateHasChanged).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>入力をIPv4アドレスとして検証します。</summary>
    /// <returns>指定した条件を満たすかどうかです。</returns>
    private static bool Ipv4(string value) => IPAddress.TryParse(value, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>画面が所有する継続処理を終了し、保持する参照と資源を解放します。</summary>
    /// <returns>所有する処理と参照の解放完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(true);
            await polling.ConfigureAwait(true);
        }
        finally
        {
            lifetime.Dispose();
            await base.DisposeAsync().ConfigureAwait(true);
            GC.SuppressFinalize(this);
        }
    }
}
