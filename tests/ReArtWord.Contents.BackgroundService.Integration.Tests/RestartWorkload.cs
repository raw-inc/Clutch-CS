using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>停止順序と独立して実信号・設定保存・State書込み・手動実行を継続します。</summary>
internal sealed class RestartWorkload(UdpObserver observer, string cluster, int[] online, int inputPort, UdpClient receiver, int seed)
{
    private readonly ConcurrentDictionary<string, int> received = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Attempt> attempts = new();
    private string? writingPeer;
    private int sent;
    private string? configurationRevision;
    private ManagementCommand? pendingSave;

    /// <summary>停止注入が更新途中に重なったかを記録するための元Peerです。</summary>
    internal string? WritingPeer => Volatile.Read(ref writingPeer);

    /// <summary>ループバックへ実送信した信号数です。</summary>
    internal int Sent => Volatile.Read(ref sent);

    /// <summary>外側要求と元結果を、停止後の照合まで維持します。</summary>
    internal IReadOnlyList<Attempt> Attempts => attempts.ToArray();

    /// <summary>受信値の重複を数え、表示履歴の上限に依存せず検出します。</summary>
    internal IReadOnlyDictionary<string, int> Received => new Dictionary<string, int>(received, StringComparer.Ordinal);

    /// <summary>約3Hzで異なるOSCを流します。停止中のUDP未達を成功に読み替えません。</summary>
    /// <returns>この試験だけの送信寿命です。</returns>
    internal async Task SendAsync(CancellationToken cancellationToken = default)
    {
        using UdpClient sender = new(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string token = "source-" + Interlocked.Increment(ref sent).ToString("D8", CultureInfo.InvariantCulture);
                await sender.SendAsync(Packet(token), new IPEndPoint(IPAddress.Loopback, inputPort), cancellationToken).ConfigureAwait(false);
                await Task.Delay(333, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 制御側の取消しで送信を終了し、再送しません。
        }
    }

    /// <summary>試験所有の宛先へ実到着したバイト列だけを成功として観測します。</summary>
    /// <returns>取消しまでの受信です。</returns>
    internal async Task ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult packet = await receiver.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                string text = Encoding.UTF8.GetString(packet.Buffer);
                string token = text.Split('\0', StringSplitOptions.RemoveEmptyEntries)[^1];
                received.AddOrUpdate(token, 1, static (_, count) => count + 1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 所有タスクを待ち終えてからソケットを閉じます。
        }
    }

    /// <summary>一度に一件の更新へ制限しつつ、対象Peerを乱数で変えます。</summary>
    /// <returns>開始した要求と、その場で確認できた成否です。</returns>
    // 抑制理由: 送信先の試験順序を再現するための固定seedです。認証情報の生成には使用しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394", Justification = "隔離負荷試験の送信先をseedから再現するため。")]
    internal async Task WriteAsync(CancellationToken cancellationToken = default)
    {
        Random random = new(seed);
        int sequence = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PeerInfo[] available = observer.GetClusters().Where(item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal)).SelectMany(item => item.Peers)
                    .Where(peer => PeerStatusRules.CanParticipate(peer.Status) && int.TryParse(peer.PeerId.AsSpan(1), out int index)
                        && index < online.Length && Volatile.Read(ref online[index]) != 0).OrderBy(peer => peer.PeerId, StringComparer.Ordinal).ToArray();
                if (available.Length != 0)
                {
                    PeerInfo peer = available[random.Next(available.Length)];
                    Guid id = Guid.NewGuid();
                    int kind = sequence++ % 3;
                    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(4));
                    string operation;
                    object body;
                    if (kind == 0)
                    {
                        operation = "runs.start";
                        body = new WorkflowRunRequest(id, "chaos-manual", EntryNodeId: "delay",
                            Inputs: new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromBytes(Encoding.UTF8.GetBytes("manual-" + id.ToString("N"))) });
                    }
                    else
                    {
                        if (kind == 1)
                        {
                            operation = "internal-data.write";
                            body = new InternalDataWriteRequest(id, new(string.Empty, EInternalDataScope.Cluster, "chaos-" + id.ToString("N")), null, NodeValue.FromObject(sequence));
                        }
                        else
                        {
                            operation = "workflows.save";
                            if (pendingSave is { } pending)
                            {
                                ManagementQueryResult query = await observer.QueryAsync(pending, deadline.Token).ConfigureAwait(false);
                                ObserveSave(pending, query.Reply);
                                if (pendingSave is not null)
                                {
                                    continue;
                                }
                            }
                            if (configurationRevision is null)
                            {
                                ManagementReply read = await observer.InspectAsync(Command(peer, "workflows.read", "chaos-edit", Guid.NewGuid()), deadline.Token).ConfigureAwait(false);
                                if (read.Outcome != EOutcome.Succeeded || read.Value is null)
                                {
                                    continue;
                                }
                                configurationRevision = read.Value.Json.GetProperty("saved").GetProperty("revision").GetString();
                            }
                            body = new WorkflowSaveRequest(id, RestartFixtureData.Editable(sequence), configurationRevision);
                        }
                    }
                    ManagementCommand command = Command(peer, operation, body, Guid.NewGuid());
                    Volatile.Write(ref writingPeer, peer.PeerId);
                    ManagementReply reply;
                    try
                    {
                        reply = await observer.ExecuteAsync(command, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        reply = new(EOutcome.Unknown, Reason: "fixture.callerDeadline");
                    }
                    attempts.Enqueue(new(command, reply));
                    if (kind == 2)
                    {
                        ObserveSave(command, reply);
                    }
                    Volatile.Write(ref writingPeer, null);
                }
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // 読取り中の停止は次の新規要求と区別します。送信した要求は上で元IDを保持します。
            }
        }
    }

    /// <summary>UIと同様に確認済みの編集元を保持し、未確認の保存がある間は別編集を開始しません。</summary>
    private void ObserveSave(ManagementCommand command, ManagementReply? reply)
    {
        WorkflowSaveResult? saved = reply?.Value?.Json.Deserialize<WorkflowSaveResult>(ContractJson.Options);
        if (reply?.Outcome == EOutcome.Succeeded && saved?.Shared.Outcome == EOutcome.Succeeded)
        {
            configurationRevision = saved.Revision;
            pendingSave = null;
            return;
        }
        if (reply?.Outcome == EOutcome.NotExecuted || (saved?.Saved.Outcome is EOutcome.Failed or EOutcome.NotExecuted
            && saved.Shared.Outcome is EOutcome.Failed or EOutcome.NotExecuted))
        {
            pendingSave = null;
            return;
        }
        pendingSave = command;
    }

    /// <summary>全員復帰後に新しいOSCを送り、待受が実際に復帰したことを確認します。</summary>
    /// <returns>固有のプローブ一件の実到着です。</returns>
    internal async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        using UdpClient sender = new(new IPEndPoint(IPAddress.Loopback, 0));
        string token = "probe-" + Guid.NewGuid().ToString("N");
        await sender.SendAsync(Packet(token), new IPEndPoint(IPAddress.Loopback, inputPort), cancellationToken).ConfigureAwait(false);
        await CorePeer.WaitAsync(_ => Task.FromResult(received.ContainsKey(token)), cancellationToken).ConfigureAwait(false);
        Assert.Equal(1, received[token]);
    }

    /// <summary>元要求の照会と既受理の手動実行再照会によって、旧処理が再発火しないことを確認します。</summary>
    /// <returns>復帰後に判明した外側結果です。</returns>
    internal async Task<IReadOnlyList<object>> VerifyRequestsAsync(CancellationToken cancellationToken = default)
    {
        List<object> results = [];
        string runOperation = "runs.start";
        foreach (Attempt attempt in attempts)
        {
            ManagementQueryResult query = await observer.QueryAsync(attempt.Command, cancellationToken).ConfigureAwait(false);
            Assert.False(query.Running);
            if (attempt.Reply.Outcome == EOutcome.Succeeded)
            {
                Assert.NotNull(query.Reply);
                Assert.Equal(EOutcome.Succeeded, query.Reply.Outcome);
                Assert.Equal(ContractJson.Hash(attempt.Reply.Value), ContractJson.Hash(query.Reply.Value));
            }
            results.Add(new { attempt.Command.RequestId, attempt.Command.Operation, original = attempt.Reply, recovered = query });
            ManagementReply? known = query.Reply ?? attempt.Reply;
            if (known?.Outcome == EOutcome.Succeeded && known.Value is not null && string.Equals(attempt.Command.Operation, runOperation, StringComparison.Ordinal))
            {
                WorkflowRunStatus accepted = known.Value.Json.Deserialize<WorkflowRunStatus>(ContractJson.Options)!;
                if (accepted.Running || accepted.Outcome == EOutcome.Succeeded)
                {
                    PeerInfo current = observer.GetClusters().Single(item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal)).Peers
                        .Single(peer => string.Equals(peer.PeerId, attempt.Command.TargetPeerId, StringComparison.Ordinal));
                    ManagementReply repeated = await observer.ExecuteAsync(attempt.Command with { RequestId = Guid.NewGuid(), TargetInstanceId = current.InstanceId, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(15) }, cancellationToken).ConfigureAwait(false);
                    Assert.Equal(EOutcome.Succeeded, repeated.Outcome);
                    WorkflowRunStatus status = repeated.Value!.Json.Deserialize<WorkflowRunStatus>(ContractJson.Options)!;
                    Assert.False(status.Running);
                    Assert.Equal(accepted.OperationId, status.OperationId);
                    if (status.Outcome == EOutcome.Succeeded)
                    {
                        Assert.Equal(1, received.GetValueOrDefault("manual-" + status.OperationId.ToString("N")));
                    }
                }
            }
        }
        return results;
    }

    /// <summary>表示候補ではなく、その時点で観測した元実体へ固定します。</summary>
    /// <returns>有限期限と一回の操作IDを持つ要求です。</returns>
    private ManagementCommand Command(PeerInfo peer, string operation, object body, Guid id)
        => new(id, cluster, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(15), operation, ContractJson.ToValue(body));

    /// <summary>試験の識別文字列だけを一引数のOSCへ符号化します。</summary>
    /// <returns>4バイト境界に揃った一パケットです。</returns>
    private static byte[] Packet(string token)
    {
        byte[] value = Encoding.UTF8.GetBytes(token);
        byte[] bytes = new byte[12 + (value.Length + 4) / 4 * 4];
        Encoding.UTF8.GetBytes("/chaos\0\0,s\0\0").CopyTo(bytes, 0);
        value.CopyTo(bytes, 12);
        return bytes;
    }

    /// <summary>中断しても元IDと要求実体を失わない試験記録です。</summary>
    internal sealed record Attempt(ManagementCommand Command, ManagementReply Reply);
}
