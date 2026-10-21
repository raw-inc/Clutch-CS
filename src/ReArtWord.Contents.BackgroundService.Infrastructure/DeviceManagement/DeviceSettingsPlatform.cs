using System.Security.Principal;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.EnvironmentVariables;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.EnvironmentVariables;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Hosts;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Windows;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;

/// <summary>共通hosts・WoL処理と、Windows専用の限定したOS操作を選びます。</summary>
internal sealed class DeviceSettingsPlatform(string dataDirectory, Display.IDeviceDisplayAccess? displayAccess = null) : IDeviceSettingsPlatform, IDisposable
{
    private static string FirewallBulkResource => "bulk";
    private static string RefreshWakeResource => "refresh";
    private static bool SupportsWake => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
    private readonly DeviceHostsFile hosts = new(DeviceHostsFile.SystemPath, Path.Combine(dataDirectory, "host-manager-hosts-backups"));
    private readonly Display.IDeviceDisplayAccess? display = displayAccess ?? (OperatingSystem.IsWindows() ? new Display.WindowsDisplaySettings()
                                                                                                           : null);
    private readonly WakeOnLan.WakeSettings wake = new();

    /// <summary>未対応OSへWindows操作を公開しません。</summary>
    /// <returns>このCoreの実行能力です。</returns>
    public DeviceCapabilities Capabilities()
    {
        if (OperatingSystem.IsWindows())
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            return new("Windows", Environment.MachineName, elevated, [EDeviceArea.Network, EDeviceArea.Hosts, EDeviceArea.Firewall, EDeviceArea.Environment, EDeviceArea.Display, EDeviceArea.Power, EDeviceArea.WindowsUpdate, EDeviceArea.Login, EDeviceArea.WakeOnLan], WindowsEnvironmentVariables.Users());
        }
        string root = "root";
        string os = OperatingSystem.IsMacOS() ? "macOS"
                                              : "Linux";
        bool supported = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
        return new(os, Environment.MachineName, string.Equals(Environment.UserName, root, StringComparison.Ordinal), supported ? [EDeviceArea.Hosts, EDeviceArea.WakeOnLan]
                                                                                                                               : [], []);
    }

    /// <summary>設定領域の実読取りを行い、一覧の各編集対象にも内容版を付けます。</summary>
    /// <returns>取得した内容と版です。</returns>
    public async Task<DeviceSnapshot> ReadAsync(DeviceReadRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Area == EDeviceArea.Hosts)
        {
            return await hosts.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        if (request.Area == EDeviceArea.WakeOnLan && SupportsWake)
        {
            if (string.Equals(request.ResourceId, RefreshWakeResource, StringComparison.Ordinal))
            {
                wake.Invalidate();
            }
            return await wake.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Device.UnsupportedPlatform");
        }
        if (request.Area == EDeviceArea.Environment)
        {
            return WindowsEnvironmentVariables.Read(request);
        }
        if (request.Area == EDeviceArea.Power)
        {
            return new Power.WindowsPowerSettings().Read();
        }
        if (request.Area == EDeviceArea.WindowsUpdate)
        {
            return new WindowsUpdate.WindowsUpdateSettings().Read();
        }
        if (request.Area == EDeviceArea.Login)
        {
            return new Login.WindowsLoginSettings().Read();
        }
        if (request.Area == EDeviceArea.Display)
        {
            return await display!.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        }
        string area = request.Area == EDeviceArea.Network ? "Network"
                                                          : "Firewall";
        DevicePowerShellReply reply = await DevicePowerShell.RunAsync(area, new { mode = "read", query = request }, cancellationToken).ConfigureAwait(false);
        if (!reply.Success)
        {
            throw new InvalidOperationException(reply.Reason ?? "Device.ReadFailed");
        }
        if (request.ResourceId is not null)
        {
            if (request.Area == EDeviceArea.Firewall && string.Equals(request.ResourceId, FirewallBulkResource, StringComparison.Ordinal))
            {
                DeviceFirewallTemplates templates = DeviceSnapshots.Read<DeviceFirewallTemplates>(reply.Data);
                return DeviceSnapshots.Create(templates with { Rules = templates.Rules.Select(item => item with
                {
                    Rule = item.Rule with { Revision = ContractJson.Hash(item.Rule with { Revision = null }) },
                    Cloneable = item.Cloneable && CanCloneFirewallRule(item.Rule),
                }).ToArray() });
            }
            return DeviceSnapshots.Create(reply.Data);
        }
        if (request.Area == EDeviceArea.Network)
        {
            DeviceNetworkState state = DeviceSnapshots.Read<DeviceNetworkState>(reply.Data);
            return DeviceSnapshots.Create(state with
            {
                Interfaces = state.Interfaces.Select(item => item with { Revision = ContractJson.Hash(item with { Revision = null }) }).ToArray(),
                VirtualNics = state.VirtualNics.Select(item => item with { Revision = ContractJson.Hash(item with { Revision = null }) }).ToArray(),
            });
        }
        DeviceFirewallState firewall = DeviceSnapshots.Read<DeviceFirewallState>(reply.Data);
        return DeviceSnapshots.Create(firewall with
        {
            Profiles = firewall.Profiles.Select(item => item with { Revision = ContractJson.Hash(item with { Revision = null }) }).ToArray(),
            Rules = firewall.Rules.Select(item => item with { Revision = ContractJson.Hash(item with { Revision = null }) }).ToArray(),
        });
    }

    /// <summary>OSの既定条件に加え、公開作成APIで全フィルターを再現できるかを確認します。</summary>
    /// <returns>未対応の動的ポート・アドレス等を複製候補から除いた結果です。</returns>
    private static bool CanCloneFirewallRule(DeviceFirewallRule rule)
    {
        try
        {
            DeviceFirewallRules.Validate(EDeviceAction.FirewallRuleAdd, "rule/template", new(Rule: rule));
            return rule.Editable;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>OSへの変更を一回だけ呼び出し、確定した開始前拒否を区別します。</summary>
    /// <returns>実変更の確認結果です。</returns>
    public async Task<DeviceChangeResult> ApplyAsync(DeviceChangeRequest request, DeviceSnapshot before, CancellationToken cancellationToken = default)
    {
        if (request.Action == EDeviceAction.Hosts)
        {
            return await hosts.ApplyAsync(request, before, cancellationToken).ConfigureAwait(false);
        }
        if (request.Action == EDeviceAction.WakeOnLan && SupportsWake)
        {
            if (!Capabilities().Elevated)
            {
                return new(request.OperationId, EOutcome.NotExecuted, "Device.PermissionDenied");
            }
            return wake.Apply(request, before);
        }
        if (!OperatingSystem.IsWindows())
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.UnsupportedPlatform");
        }
        if (request.Action == EDeviceAction.Environment)
        {
            bool notified = WindowsEnvironmentVariables.Apply(request.Read, DeviceSnapshots.Read<DeviceEnvironmentChange>(request.Change), before.Revision);
            string notification = notified ? "environment.notified"
                                           : "environment.notification.unconfirmed";
            return new(request.OperationId, EOutcome.Succeeded, CompletedSteps: ["environment.persisted", notification]);
        }
        if (!Capabilities().Elevated)
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.PermissionDenied");
        }
        if (request.Action == EDeviceAction.Display)
        {
            return await display!.ApplyAsync(request, before, cancellationToken).ConfigureAwait(false);
        }
        if (request.Action == EDeviceAction.Power)
        {
            return new Power.WindowsPowerSettings().Apply(request, before);
        }
        if (request.Action == EDeviceAction.WindowsUpdate)
        {
            return await new WindowsUpdate.WindowsUpdateSettings().ApplyAsync(request, before, cancellationToken).ConfigureAwait(false);
        }
        if (request.Action == EDeviceAction.Login)
        {
            return new Login.WindowsLoginSettings().Apply(request, before);
        }
        string area = request.Read.Area == EDeviceArea.Network ? "Network"
                                                               : "Firewall";
        DevicePowerShellReply reply = await DevicePowerShell.RunAsync(area, new { mode = "apply", query = request.Read, request.Action, request.OperationId, request.Change, before = before.Data }, cancellationToken).ConfigureAwait(false);
        if (request.Read.Area == EDeviceArea.Network)
        {
            wake.Invalidate();
        }
        EOutcome outcome = reply.Success ? EOutcome.Succeeded
                                         : reply.Started ? EOutcome.Failed
                                                         : EOutcome.NotExecuted;
        return new(request.OperationId, outcome, reply.Reason, CompletedSteps: reply.Steps);
    }

    /// <summary>PC設定サービスが所有するNICキャッシュの排他を解放します。</summary>
    public void Dispose() => wake.Dispose();
}
