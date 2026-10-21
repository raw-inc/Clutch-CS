using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>復旧応答の省略・破損を、コミットや取消の根拠に使用しないことを検証します。</summary>
public sealed class ReplicationRecoveryProbeTests
{
    /// <summary>enum既定値や空リストの補完による偽の成功を防ぎます。</summary>
    [Theory]
    [InlineData("Outcome")]
    [InlineData("OperationId")]
    [InlineData("Fingerprint")]
    [InlineData("Entries")]
    public void MissingReceiptFieldIsRejected(string field)
    {
        JsonObject body = JsonSerializer.SerializeToNode(Create(EOutcome.Succeeded, null))!.AsObject();
        body[nameof(ReplicationRecoveryProbe.Operation)]!.AsObject().Remove(field);
        Assert.Throws<InvalidDataException>(() => ReplicationRecoveryProbe.Read(JsonSerializer.SerializeToElement(body)));
    }

    /// <summary>必須の照会フィールドの欠落も、実行中ではないという判断へ落としません。</summary>
    [Theory]
    [InlineData("Protocol")]
    [InlineData("Operation")]
    [InlineData("Stored")]
    [InlineData("Members")]
    [InlineData("Busy")]
    public void MissingProbeFieldIsRejected(string field)
    {
        JsonObject body = JsonSerializer.SerializeToNode(Create(EOutcome.Succeeded, null))!.AsObject();
        body.Remove(field);
        Exception error = Assert.ThrowsAny<Exception>(() => ReplicationRecoveryProbe.Read(JsonSerializer.SerializeToElement(body)));
        Assert.True(error is JsonException or InvalidDataException);
    }

    /// <summary>再起動した同じHostだけが、同じ内容の永続判断を利用できます。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecisionRequiresOriginalHostAndContent(bool commit)
    {
        string phase = commit ? ReplicationJournal.CommitDecided
                              : ReplicationJournal.AbortDecided;
        ReplicationRecoveryProbe probe = Create(EOutcome.Unknown, phase);
        DurableReceipt operation = probe.Operation!;
        PeerInfo local = probe.Members[0] with { InstanceId = Guid.NewGuid() };
        ReplicationRecoveryProbe decoded = ReplicationRecoveryProbe.Read(JsonSerializer.SerializeToElement(probe));
        Assert.Equal(commit, decoded.Decision(operation.OperationId, operation.Fingerprint, local));
        Assert.Null(decoded.Decision(operation.OperationId, operation.Fingerprint, local with { HostId = "replacement" }));
        Assert.Null(decoded.Decision(operation.OperationId, "different", local));
        Assert.Null((decoded with { Busy = true }).Decision(operation.OperationId, operation.Fingerprint, local));
    }

    /// <summary>壊れた参加者を復旧経路へ通さず、通常受付の障害へ波及させません。</summary>
    [Fact]
    public void InvalidMembershipIsRejected()
    {
        ReplicationRecoveryProbe probe = Create(EOutcome.Unknown, ReplicationJournal.Preparing);
        Assert.Throws<InvalidDataException>(() => ReplicationRecoveryProbe.Read(JsonSerializer.SerializeToElement(probe with { Members = null! })));
        Assert.Throws<InvalidDataException>(() => ReplicationRecoveryProbe.Read(JsonSerializer.SerializeToElement(probe with { Members = [null!] })));
        Assert.Throws<InvalidDataException>(() => ReplicationRecoveryProbe.Read(JsonSerializer.SerializeToElement(probe with { Members = [probe.Members[0], probe.Members[0]] })));
        Assert.Null(probe.Decision(probe.Operation!.OperationId, probe.Operation.Fingerprint, probe.Members[0]));
    }

    /// <summary>独立した操作と元参加者を持つ応答です。</summary>
    /// <returns>本文を含めない検証用応答です。</returns>
    private static ReplicationRecoveryProbe Create(EOutcome outcome, string? reason)
        => new(ReplicationRecoveryProbe.ProtocolName, new(Guid.NewGuid(), "same-content", outcome, [], reason),
            [new("peer", Guid.NewGuid(), "host", "127.0.0.1", 1000, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow)], null, false);
}
