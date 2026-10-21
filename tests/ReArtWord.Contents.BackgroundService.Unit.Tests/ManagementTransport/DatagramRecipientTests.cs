using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>他の受信者への不完全な配送が、自分宛ての管理要求を妨げないことを確認します。</summary>
public sealed class DatagramRecipientTests
{
    /// <summary>発見用の通知は監視へ渡し、同名クラスターの参加処理には渡しません。</summary>
    [Fact]
    public void DiscoveryAnnouncementsNeverEnterClusterMembership()
    {
        Guid receiver = Guid.NewGuid();
        WireMessage message = Message(receiver) with { Kind = DiscoveryProtocol.AnnouncementKind, TargetId = null, TargetInstanceId = null };
        FragmentAssembler peer = new(4194304, TimeProvider.System, new("receiver", receiver, "cluster"));
        FragmentAssembler observer = new(4194304, TimeProvider.System, new("observer", receiver, null));
        Assert.Null(Assemble(peer, message));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(message), Assemble(observer, message));
    }

    /// <summary>宛先外の断片は本文の全体復号前に除き、既存の通信形式を維持します。</summary>
    [Fact]
    public void ForeignFragmentsAreDiscardedBeforePayloadAllocation()
    {
        Guid receiver = Guid.NewGuid();
        FragmentAssembler assembler = new(4194304, TimeProvider.System, new("receiver", receiver, "cluster"));
        WireMessage foreign = Message(Guid.NewGuid());
        byte[][] packets = DatagramEncoding.Encode(foreign, 4194304);
        Assert.All(packets, packet => Assert.Null(assembler.Decode("source", packet)));
        WireMessage wanted = Message(receiver);
        Assert.All(DatagramEncoding.Encode(wanted, 4194304), packet => Assert.NotNull(assembler.Decode("source", packet)));
        DatagramFragment old = new(Guid.NewGuid(), 0, 1, 1, "legacy", [1]);
        Assert.NotNull(assembler.Decode("source", JsonSerializer.SerializeToUtf8Bytes(old)));
        Assert.All(packets, packet => Assert.InRange(packet.Length, 1, 1472));
    }

    /// <summary>先頭を失った他宛先の断片が上限まで残っても、先頭から届いた自分宛てを復元します。</summary>
    [Fact]
    public void AddressedHeaderCanDisplaceAnUnidentifiedTailAtCapacity()
    {
        Guid receiver = Guid.NewGuid();
        FragmentAssembler assembler = new(4194304, TimeProvider.System, new("receiver", receiver, "cluster"));
        for (int index = 0; index < 32; index++)
        {
            DatagramFragment[] foreign = Fragments(Message(receiver) with { TargetId = "another-peer" });
            Assert.Null(assembler.Add("sender", foreign[1]));
        }
        WireMessage expected = Message(receiver);
        byte[]? completed = null;
        foreach (DatagramFragment fragment in Fragments(expected))
        {
            completed = assembler.Add("sender", fragment) ?? completed;
        }
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected), completed);
    }

    /// <summary>完了後に遅着した末尾が蓄積しても、保持件数を増やさず次の配送を受け付けます。</summary>
    [Fact]
    public void LateDuplicateTailsDoNotStarveSubsequentDeliveries()
    {
        Guid receiver = Guid.NewGuid();
        FragmentAssembler assembler = new(4194304, TimeProvider.System, new("receiver", receiver, "cluster"));
        for (int index = 0; index < 64; index++)
        {
            WireMessage expected = Message(receiver);
            DatagramFragment[] fragments = Fragments(expected);
            byte[]? completed = null;
            foreach (DatagramFragment fragment in fragments)
            {
                completed = assembler.Add("sender", fragment) ?? completed;
            }
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected), completed);
            Assert.Null(assembler.Add("sender", fragments[^1]));
        }
    }

    /// <summary>先頭を保持する32配送は追い出さず、完了して空いた枠から次の配送を受け付けます。</summary>
    [Fact]
    public void CapacityStillProtectsMessagesWhoseHeadersHaveArrived()
    {
        Guid receiver = Guid.NewGuid();
        FragmentAssembler assembler = new(4194304, TimeProvider.System, new("receiver", receiver, "cluster"));
        WireMessage[] admitted = Enumerable.Range(0, 32).Select(_ => Message(receiver)).ToArray();
        foreach (WireMessage message in admitted)
        {
            Assert.Null(assembler.Add("sender", Fragments(message)[0]));
        }
        WireMessage waiting = Message(receiver);
        foreach (DatagramFragment fragment in Fragments(waiting))
        {
            Assert.Null(assembler.Add("sender", fragment));
        }
        byte[]? completed = null;
        foreach (DatagramFragment fragment in Fragments(admitted[0]).Skip(1))
        {
            completed = assembler.Add("sender", fragment) ?? completed;
        }
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(admitted[0]), completed);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(waiting), Assemble(assembler, waiting));
    }

    /// <summary>通知の末尾欠落が上限まで残っても、保存要求や応答を期限前に組み立てます。</summary>
    [Theory]
    [InlineData("prepare", false)]
    [InlineData("reply", true)]
    public void IncompleteNotificationsDoNotStarveAddressedMessages(string kind, bool delayedHeader)
    {
        Guid receiver = Guid.NewGuid();
        FragmentAssembler assembler = new(4194304, TimeProvider.System, new("receiver", receiver, "cluster"));
        for (int index = 0; index < 32; index++)
        {
            WireMessage notification = Message(receiver) with { Kind = "transient", TargetId = null, TargetInstanceId = null };
            DatagramFragment[] fragments = Fragments(notification);
            if (delayedHeader)
            {
                Assert.Null(assembler.Add("sender", fragments[1]));
            }
            Assert.Null(assembler.Add("sender", fragments[0]));
        }
        WireMessage expected = Message(receiver) with { Kind = kind };
        byte[]? completed = null;
        foreach (DatagramFragment fragment in Fragments(expected))
        {
            completed = assembler.Add("sender", fragment) ?? completed;
        }
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected), completed);
    }

    /// <summary>新しい通知のために、既に受け付けた保存要求の断片を破棄しません。</summary>
    [Fact]
    public void NotificationsDoNotEvictIncompleteRequests()
    {
        Guid receiver = Guid.NewGuid();
        FragmentAssembler assembler = new(4194304, TimeProvider.System, new("receiver", receiver, "cluster"));
        WireMessage[] admitted = Enumerable.Range(0, 32).Select(_ => Message(receiver) with { Kind = "prepare" }).ToArray();
        foreach (WireMessage message in admitted)
        {
            Assert.Null(assembler.Add("sender", Fragments(message)[0]));
        }
        WireMessage notification = Message(receiver) with { Kind = "transient", TargetId = null, TargetInstanceId = null };
        foreach (DatagramFragment fragment in Fragments(notification))
        {
            Assert.Null(assembler.Add("sender", fragment));
        }
        Assert.All(admitted, message => Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(message), Assemble(assembler, message)));
    }

    /// <summary>別宛先・別クラスタ・自分の送信が連続しても、必要な本文を復元できます。</summary>
    [Theory]
    [InlineData("target")]
    [InlineData("cluster")]
    [InlineData("self")]
    [InlineData("instance")]
    public void UnrelatedPartialMessagesDoNotFillAssemblyCapacity(string difference)
    {
        Guid receiver = Guid.NewGuid();
        FragmentAssembler assembler = new(4194304, TimeProvider.System, new("receiver", receiver, "cluster"));
        for (int index = 0; index < 64; index++)
        {
            WireMessage foreign = Message(receiver);
            foreign = difference switch
            {
                "target" => foreign with { TargetId = "another-peer" },
                "cluster" => foreign with { ClusterId = "another-cluster" },
                "instance" => foreign with { TargetInstanceId = Guid.NewGuid() },
                _ => foreign with { InstanceId = receiver },
            };
            DatagramFragment[] fragments = Fragments(foreign);
            Assert.Null(assembler.Add("sender", fragments[1]));
            Assert.Null(assembler.Add("sender", fragments[0]));
            Assert.Null(assembler.Add("sender", fragments[^1]));
        }
        WireMessage expected = Message(receiver);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected), Assemble(assembler, expected));
    }

    /// <summary>順序が逆転した自分宛ての配送と、全員宛ての通知を引き続き復元できます。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddressedAndBroadcastMessagesAcceptReorderedFragments(bool broadcast)
    {
        Guid receiver = Guid.NewGuid();
        FragmentAssembler assembler = new(4194304, TimeProvider.System, new("receiver", receiver, "cluster"));
        WireMessage expected = Message(receiver);
        if (broadcast)
        {
            expected = expected with { TargetId = null, TargetInstanceId = null };
        }
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected), Assemble(assembler, expected));
    }

    /// <summary>クラスタ未指定の監視クライアントは、任意クラスタの自分宛て応答を受け取れます。</summary>
    [Fact]
    public void ObserverAcceptsItsReplyFromAnyCluster()
    {
        Guid receiver = Guid.NewGuid();
        FragmentAssembler assembler = new(4194304, TimeProvider.System, new("receiver", receiver, null));
        WireMessage expected = Message(receiver) with { ClusterId = "discovered-cluster" };
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected), Assemble(assembler, expected));
    }

    /// <summary>宛先が先頭断片に収まらない場合も、従来どおり全体を組み立てられます。</summary>
    [Fact]
    public void IncompleteHeaderDoesNotRejectAnAddressedMessage()
    {
        Guid receiver = Guid.NewGuid();
        FragmentAssembler assembler = new(4194304, TimeProvider.System, new("receiver", receiver, "cluster"));
        WireMessage expected = Message(receiver) with { SenderId = new string('s', 2000) };
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected), Assemble(assembler, expected));
    }

    /// <summary>型付き本文を複数断片にする試験用の配送です。</summary>
    /// <returns>指定実体への新しいメッセージです。</returns>
    private static WireMessage Message(Guid receiver) => new(Guid.NewGuid(), "cluster", "sender", Guid.NewGuid(), "reply", "receiver", null, JsonSerializer.SerializeToElement(new string('x', 4096)), receiver);

    /// <summary>製品と同じ送信形式を受信側の断片へ変換します。</summary>
    /// <returns>送信順に並んだ断片です。</returns>
    private static DatagramFragment[] Fragments(WireMessage message) => DatagramEncoding.Encode(message, 4194304).Select(packet => JsonSerializer.Deserialize<DatagramFragment>(packet)!).ToArray();

    /// <summary>断片の到着順序を逆転させ、完成した本文を取り出します。</summary>
    /// <returns>復元された本文、未完了ならnullです。</returns>
    private static byte[]? Assemble(FragmentAssembler assembler, WireMessage message)
    {
        byte[]? complete = null;
        foreach (DatagramFragment fragment in Fragments(message).Reverse())
        {
            complete = assembler.Add("sender", fragment) ?? complete;
        }
        return complete;
    }
}
