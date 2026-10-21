using System.Buffers.Binary;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>切断の一次原因を保持し、例外本文や設定値を配信しないことを検証します。</summary>
public sealed class DesktopFaultTests
{
    /// <summary>Win32入力障害の具体的な番号を共通HRESULTで失わないことを確認します。</summary>
    /// <returns>秘密を含まない数値の診断です。</returns>
    [Fact]
    public async Task NativeErrorCodeIsPreservedAsync()
    {
        DesktopFault fault = new();
        fault.Record("input", new System.ComponentModel.Win32Exception(5));
        using MemoryStream stream = new();
        await fault.SendAsync(stream).ConfigureAwait(true);
        using JsonDocument document = JsonDocument.Parse(stream.ToArray().AsMemory(DesktopWire.HeaderSize));
        Assert.Equal("00000005", document.RootElement.GetProperty("code").GetString());
    }

    /// <summary>取消や後続障害が音声の元障害を上書きせず、有限のwire messageだけを返します。</summary>
    /// <returns>同じ中継protocolで読める診断です。</returns>
    [Fact]
    public async Task FirstFailureIsPreservedWithoutExposingExceptionTextAsync()
    {
        DesktopFault fault = new();
        fault.Record("audio", new InvalidDataException("private file path and user settings"));
        fault.Record("video", new IOException("secondary failure"));
        fault.Record("session", new OperationCanceledException());
        using MemoryStream stream = new();
        await fault.SendAsync(stream).ConfigureAwait(true);
        byte[] bytes = stream.ToArray();
        Assert.Equal(8, BinaryPrimitives.ReadInt32BigEndian(bytes));
        Assert.Equal(bytes.Length - DesktopWire.HeaderSize, DesktopWire.PayloadLength(bytes.AsSpan(0, DesktopWire.HeaderSize)));
        using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(DesktopWire.HeaderSize));
        Assert.Equal("audio", document.RootElement.GetProperty("stage").GetString());
        Assert.Equal("InvalidDataException", document.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain("private", document.RootElement.ToString(), StringComparison.Ordinal);
    }
}
