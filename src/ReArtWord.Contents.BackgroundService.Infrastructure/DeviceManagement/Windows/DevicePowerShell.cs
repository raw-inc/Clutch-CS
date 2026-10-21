using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Windows;

/// <summary>同梱した固定処理へJSONを標準入力で渡し、入力をPowerShellコードへ展開しません。</summary>
[SupportedOSPlatform("windows")]
internal static class DevicePowerShell
{
    /// <summary>有限のOS照会・変更を一回だけ実行します。</summary>
    /// <returns>コマンドの型付き結果です。</returns>
    internal static async Task<DevicePowerShellReply> RunAsync(string area, object request, CancellationToken cancellationToken = default)
    {
        string network = "Network";
        string switches = string.Empty;
        if (string.Equals(area, network, StringComparison.Ordinal))
        {
            switches = Resource("VirtualSwitch.Read") + "\n" + Resource("VirtualSwitch.Change");
        }
        string script = Resource("Common") + "\n" + Resource(area + ".Read") + "\n" + Resource(area + ".Change") + "\n" + switches + "\n" + Resource("Dispatch");
        string executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        string bootstrap = "[Console]::InputEncoding=New-Object System.Text.UTF8Encoding($false); [Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false); $frame=[Console]::In.ReadToEnd() | ConvertFrom-Json; $request=$frame.request; & ([ScriptBlock]::Create($frame.code))";
        string command = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        string output = await BoundedProcessRunner.RunAsync(executable, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", command],
            new ProcessRunLimits(2 * 1024 * 1024, true, StandardInput: JsonSerializer.Serialize(new { code = script, request }, ContractJson.Options), OutputEncoding: Encoding.UTF8), timeout.Token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<DevicePowerShellReply>(output, ContractJson.Options) ?? throw new InvalidDataException("Device.InvalidCommandOutput");
    }

    /// <summary>製品に組み込んだ領域別の処理だけを読みます。</summary>
    /// <returns>入力データを含まない固定スクリプトです。</returns>
    private static string Resource(string name)
    {
        using Stream stream = typeof(DevicePowerShell).Assembly.GetManifestResourceStream("Contents.DeviceManagement." + name + ".ps1")
            ?? throw new InvalidOperationException("Device.ScriptMissing");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
