using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>応答の再送で欠けた断片を補い、別の内容や実体を混ぜないことを検証します。</summary>
public sealed class DatagramReplyTests
{
    /// <summary>一回目と二回目で異なる断片が失われても、同じ応答を復元できます。</summary>
    [Fact]
    public void RetransmittedReplyRecoversComplementaryFragmentLoss()
    {
        WireMessage request = Request();
        Guid responder = Guid.NewGuid();
        JsonElement body = JsonSerializer.SerializeToElement(new string('x', 65536));
        WireMessage first = WireMessage.Reply(request, "source", responder, body);
        WireMessage repeated = WireMessage.Reply(request, "source", responder, body);
        Assert.Equal(first.Id, repeated.Id);
        FragmentAssembler assembler = new(4194304, TimeProvider.System, new(request.SenderId, request.InstanceId, request.ClusterId));
        Assert.Null(Deliver(assembler, first, 0));
        byte[] complete = Assert.IsType<byte[]>(Deliver(assembler, repeated, 1));
        WireMessage actual = JsonSerializer.Deserialize<WireMessage>(complete)!;
        Assert.Equal(body.GetString(), actual.Body.GetString());
        Assert.Equal(request.Id, actual.ReplyTo);
        Assert.Equal(request.InstanceId, actual.TargetInstanceId);
    }

    /// <summary>同じ照会でも応答内容が変わった場合、古い断片を混ぜて完成させません。</summary>
    [Fact]
    public void ChangedReplyBodyNeverCombinesDifferentSnapshots()
    {
        WireMessage request = Request();
        Guid responder = Guid.NewGuid();
        WireMessage previous = WireMessage.Reply(request, "source", responder, JsonSerializer.SerializeToElement(new string('a', 4096)));
        WireMessage current = WireMessage.Reply(request, "source", responder, JsonSerializer.SerializeToElement(new string('b', 4096)));
        Assert.NotEqual(previous.Id, current.Id);
        FragmentAssembler assembler = new(4194304, TimeProvider.System);
        Assert.Null(Deliver(assembler, previous, 0));
        Assert.Null(Deliver(assembler, current, 1));
        byte[] complete = Assert.IsType<byte[]>(Deliver(assembler, current, 0));
        Assert.Equal(current.Body.GetString(), JsonSerializer.Deserialize<WireMessage>(complete)!.Body.GetString());
    }

    /// <summary>別の要求や再起動後の実体からの応答は、同じ配送として扱いません。</summary>
    [Fact]
    public void ReplyIdentitySeparatesRequestsAndResponderInstances()
    {
        WireMessage request = Request();
        Guid responder = Guid.NewGuid();
        JsonElement body = JsonSerializer.SerializeToElement(true);
        WireMessage first = WireMessage.Reply(request, "source", responder, body);
        Assert.NotEqual(first.Id, WireMessage.Reply(request with { Id = Guid.NewGuid() }, "source", responder, body).Id);
        Assert.NotEqual(first.Id, WireMessage.Reply(request, "source", Guid.NewGuid(), body).Id);
        Assert.NotEqual(request.Id, first.Id);
    }

    /// <summary>実体が固定された読取要求を作ります。</summary>
    /// <returns>試験ごとに独立した要求です。</returns>
    private static WireMessage Request() => new(Guid.NewGuid(), "cluster", "destination", Guid.NewGuid(), "sync.read", "source", null, JsonSerializer.SerializeToElement(true));

    /// <summary>製品の分割形式で、偶数番または奇数番の断片だけを届けます。</summary>
    /// <returns>復元できた本文、まだ欠けている場合はnullです。</returns>
    private static byte[]? Deliver(FragmentAssembler assembler, WireMessage message, int parity)
    {
        byte[]? complete = null;
        byte[][] packets = DatagramEncoding.Encode(message, 4194304);
        for (int index = parity; index < packets.Length; index += 2)
        {
            complete = assembler.Add("source-address", JsonSerializer.Deserialize<DatagramFragment>(packets[index])!) ?? complete;
        }
        return complete;
    }
}
