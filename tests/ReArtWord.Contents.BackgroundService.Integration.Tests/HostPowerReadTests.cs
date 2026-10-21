using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>複数Coreから同一PCの電源設定を読み取り、OSは変更しません。</summary>
public sealed class HostPowerReadTests
{
    /// <summary>入力照会専用の補助経路で、コンソールの実入力を取得します。</summary>
    /// <returns>サービス側から使う固定pipe契約の確認です。</returns>
    [Fact]
    public async Task ReadsConsoleInputThroughTheOneShotWorkerAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string name = "contents-input-test-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string executable = Path.GetFullPath($"../../../../../src/ReArtWord.Contents.BackgroundService.CoreService/bin/{configuration}/net10.0/Clutch-CS.CoreService.exe", AppContext.BaseDirectory);
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--device-display-worker");
        start.ArgumentList.Add(name);
        using Process process = Process.Start(start) ?? throw new IOException("Console helper did not start.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(true);
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { read = new DeviceReadRequest(EDeviceArea.Power), readInput = true }, ContractJson.Options);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
            await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(true);
            await pipe.WriteAsync(body, timeout.Token).ConfigureAwait(true);
            await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(true);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            Assert.InRange(length, 1, 2097152);
            byte[] reply = new byte[length];
            await pipe.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(true);
            using JsonDocument document = JsonDocument.Parse(reply);
            JsonElement input = document.RootElement.GetProperty("activity");
            Assert.True(0 < input.GetProperty("sessionId").GetInt32());
            Assert.StartsWith("console:", input.GetProperty("identity").GetString(), StringComparison.Ordinal);
            Assert.True(input.GetProperty("lastActivity").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>クラスタごとの仮の設定でなく、PC共通の値と版を読みます。</summary>
    /// <returns>二つの実プロセスと管理通信の確認です。</returns>
    [Fact]
    public async Task ReadsTheSamePowerSettingsFromIndependentCoreProcessesAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        CorePeer first = new(Guid.NewGuid().ToString("N"), "power-a", CorePeer.AvailablePort(), true);
        CorePeer second = new(Guid.NewGuid().ToString("N"), "power-b", CorePeer.AvailablePort(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        DeviceSnapshot one = await first.GetAsync<DeviceSnapshot>("host-manager.read", new DeviceReadRequest(EDeviceArea.Power)).ConfigureAwait(true);
        DeviceSnapshot two = await second.GetAsync<DeviceSnapshot>("host-manager.read", new DeviceReadRequest(EDeviceArea.Power)).ConfigureAwait(true);
        Assert.Equal(one.Revision, two.Revision);
        DevicePowerState state = DeviceSnapshots.Read<DevicePowerState>(one.Data);
        Assert.NotEqual(Guid.Empty, state.SchemeId);
        Assert.NotEmpty(state.SchemeName);
        Assert.NotNull(state.Ac.DisplaySeconds);
    }
}
