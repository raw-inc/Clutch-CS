using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>HTTPを利用せず実Core プロセスの起動と所有資源の終了を管理します。</summary>
internal sealed class ObserverCoreProcess : IAsyncDisposable
{
    private readonly Process process;

    /// <summary>試験専用設定と保存先でCoreを起動します。</summary>
    public ObserverCoreProcess(string root, string clusterId, string peerId, int udpPort, string interfaceId, bool initialize)
    {
        string directory = Path.Combine(root, clusterId, peerId);
        Directory.CreateDirectory(directory);
        string configuration = Path.Combine(directory, "contents.toml");
        File.WriteAllText(configuration, $$"""
            [cluster]
            id = '{{clusterId}}'
            peerId = '{{peerId}}'
            hostId = 'observer-test-host'
            address = '127.0.0.1'
            interfaceId = '{{interfaceId}}'
            broadcast = '127.255.255.255'
            port = {{udpPort}}
            initialize = {{System.Text.Json.JsonSerializer.Serialize(initialize)}}
            [storage]
            directory = '{{directory.Replace(Path.DirectorySeparatorChar, '/')}}/data'
            [logs]
            interfaceId = '{{interfaceId}}'
            listenPort = 0
            """);
        ProcessStartInfo start = new("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(CoreAssembly());
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configuration);
        process = Process.Start(start) ?? throw new IOException("試験用Coreを開始できません。");
    }

    /// <summary>発見待ちの途中でも起動失敗を検出します。</summary>
    public void EnsureRunning()
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException("試験用Coreが終了しました。exit=" + process.ExitCode);
        }
    }

    /// <summary>現在のbuild構成と同じCore実行物を使用します。</summary>
    /// <returns>実Core assemblyの場所です。</returns>
    private static string CoreAssembly()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "ReArtWord.Contents.BackgroundService.CoreService", "bin", configuration, "net10.0", "Clutch-CS.CoreService.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("通常build済みのCoreServiceが必要です。");
    }

    /// <summary>この試験用の構成が起動したプロセスだけを終了します。</summary>
    /// <returns>終了確認です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        using (process)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
    }
}
