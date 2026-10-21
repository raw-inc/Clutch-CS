using ReArtWord.Contents.BackgroundService.ManagementTransport.SignalScope;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Streams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Terminal;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>フレーム境界の終了だけを許可し、認証・本文・ヘッダーの欠落を隠さないことを確認します。</summary>
public sealed class SessionFrameEndTests
{
    /// <summary>新しい受付だけがEOFを返し、従来の完全受信APIは失敗を維持します。</summary>
    /// <returns>本文のない正常終了の判別です。</returns>
    [Fact]
    public async Task EmptyConnectionIsAnEndOnlyForOptionalFrameReadersAsync()
    {
        using MemoryStream stream = new();
        Assert.False(await FrameReader.ReadHeaderAsync(stream, new byte[24]));
        Assert.Equal(0, await TerminalWire.ReadOrEndAsync(stream, new byte[32]));
        Assert.Equal(0, await SignalScopeWire.ReadOrEndAsync(stream, new byte[32]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => TerminalWire.ReadAsync(stream, new byte[32]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => SignalScopeWire.ReadAsync(stream, new byte[32]));
    }

    /// <summary>部分ヘッダーや長さ分の本文欠落は通常終了にしません。</summary>
    /// <returns>端末とキャプチャ双方の切断境界です。</returns>
    [Theory]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 4, 1, 2 })]
    public async Task PartialFramesRemainFailuresAsync(byte[] bytes)
    {
        using MemoryStream terminal = new(bytes), capture = new(bytes);
        await Assert.ThrowsAsync<EndOfStreamException>(() => TerminalWire.ReadOrEndAsync(terminal, new byte[32]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => SignalScopeWire.ReadOrEndAsync(capture, new byte[32]));
    }

    /// <summary>0長を受信した場合はEOFと混同せず、従来どおり不正フレームにします。</summary>
    /// <returns>正常終了用の戻り値が入力検証を弱めないことです。</returns>
    [Fact]
    public async Task ZeroLengthFrameIsNotAnEndAsync()
    {
        using MemoryStream terminal = new(new byte[4]), capture = new(new byte[4]);
        await Assert.ThrowsAsync<InvalidDataException>(() => TerminalWire.ReadOrEndAsync(terminal, new byte[32]));
        await Assert.ThrowsAsync<InvalidDataException>(() => SignalScopeWire.ReadOrEndAsync(capture, new byte[32]));
    }
}
