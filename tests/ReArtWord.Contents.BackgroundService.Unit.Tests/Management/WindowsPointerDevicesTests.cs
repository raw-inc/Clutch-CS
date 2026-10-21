using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Input;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>マウス接続の三状態と、Windows実APIによる読み取り専用の列挙を検証します。</summary>
public sealed class WindowsPointerDevicesTests(ITestOutputHelper output)
{
    /// <summary>通知しない旧Coreを未接続と断定せず、新Coreの三状態を保持します。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void MonitorMetadataPreservesKnownAndUnknownPresence(bool? present)
    {
        DesktopMonitor monitor = new(0, "display", 0, 0, 1920, 1080, true) { MousePresent = present };
        Assert.Equal(present, ContractJson.Copy(monitor).MousePresent);
        DesktopMonitor? legacy = JsonSerializer.Deserialize<DesktopMonitor>("""{"id":0,"name":"display","left":0,"top":0,"width":1920,"height":1080,"primary":true}""", ContractJson.Options);
        Assert.Null(legacy!.MousePresent);
    }

    /// <summary>実機の一覧を読むだけで、入力・設定・デバイス登録を変更しません。</summary>
    [Fact]
    public void WindowsCanReadHostMousePresence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        bool? present = WindowsPointerDevices.Read();
        output.WriteLine("Host mouse presence: " + present);
        Assert.NotNull(present);
    }
}
