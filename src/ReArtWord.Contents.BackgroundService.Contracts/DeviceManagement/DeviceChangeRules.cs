using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.EnvironmentVariables;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Display;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Hosts;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>全入口で同じ固定対象・変更種別・有限入力を検証します。</summary>
public static class DeviceChangeRules
{
    private static readonly string[] scopes = ["Machine", "User"];
    private static string UserScope => "User";
    private static string UserSidPrefix => "S-1-";
    private static string VirtualNicPrefix => "vnic/";
    private static string FirewallBulkResource => "bulk";
    private static string RefreshWakeResource => "refresh";

    /// <summary>設定領域と限定した参照条件を確認します。</summary>
    public static void ValidateRead(DeviceReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        int filterLimit = 128;
        if (request.Area == EDeviceArea.Firewall && string.Equals(request.ResourceId, FirewallBulkResource, StringComparison.Ordinal))
        {
            filterLimit = 8192;
        }
        if (!Enum.IsDefined(request.Area) || request.ResourceId?.Length is > 512 || request.Offset is < 0 or > 100000
            || filterLimit < request.Filter?.Length || !scopes.Contains(request.Scope, StringComparer.Ordinal) || request.UserSid?.Length is > 184)
        {
            throw new ArgumentException("Device.InvalidRequest", nameof(request));
        }
        if (request.Area == EDeviceArea.Environment && string.Equals(request.Scope, UserScope, StringComparison.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.UserSid);
            if (!request.UserSid.StartsWith(UserSidPrefix, StringComparison.Ordinal) || request.UserSid.Any(character => !char.IsAsciiDigit(character) && character is not ('S' or '-')))
            {
                throw new ArgumentException("Device.SelectUser", nameof(request));
            }
        }
        if (request.Area == EDeviceArea.WakeOnLan && request.ResourceId is not null && !string.Equals(request.ResourceId, RefreshWakeResource, StringComparison.Ordinal))
        {
            throw new ArgumentException("Device.InvalidRequest", nameof(request));
        }
    }

    /// <summary>未指定項目を変更として補わず、領域と操作の組合せを確定します。</summary>
    public static void Validate(DeviceChangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRead(request.Read);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HostId);
        if (request.OperationId == Guid.Empty || request.InstanceId == Guid.Empty || 120 < request.HostId.Length
            || request.ExpectedRevision.Length != 64 || !request.ExpectedRevision.All(Uri.IsHexDigit) || !Enum.IsDefined(request.Action)
            || request.Change.ValueKind != System.Text.Json.JsonValueKind.Object || 262144 < request.Change.GetRawText().Length)
        {
            throw new ArgumentException("Device.InvalidRequest", nameof(request));
        }
        EDeviceArea area = request.Action switch
        {
            EDeviceAction.Network or EDeviceAction.NetworkProtocols or EDeviceAction.VirtualNicCreate or EDeviceAction.VirtualNicDelete or EDeviceAction.VirtualNicVlan
                or EDeviceAction.VirtualSwitchCreate or EDeviceAction.VirtualSwitchDelete => EDeviceArea.Network,
            EDeviceAction.Hosts => EDeviceArea.Hosts,
            EDeviceAction.Environment => EDeviceArea.Environment,
            EDeviceAction.Display => EDeviceArea.Display,
            EDeviceAction.Power => EDeviceArea.Power,
            EDeviceAction.WindowsUpdate => EDeviceArea.WindowsUpdate,
            EDeviceAction.Login => EDeviceArea.Login,
            EDeviceAction.WakeOnLan => EDeviceArea.WakeOnLan,
            _ => EDeviceArea.Firewall,
        };
        if (request.Read.Area != area || (area is EDeviceArea.Network or EDeviceArea.Firewall or EDeviceArea.Display or EDeviceArea.Power or EDeviceArea.WindowsUpdate or EDeviceArea.Login && !request.AcknowledgeDisconnect))
        {
            throw new ArgumentException("Device.ConfirmNetworkImpact", nameof(request));
        }
        if (request.Read.Offset != 0 || request.Read.Filter is not null
            || (request.Action is EDeviceAction.Hosts or EDeviceAction.VirtualNicCreate && request.Read.ResourceId is not null))
        {
            throw new ArgumentException("Device.InvalidRequest", nameof(request));
        }
        switch (request.Action)
        {
            case EDeviceAction.WakeOnLan:
                if (request.Read.ResourceId is not null)
                {
                    throw new ArgumentException("Device.InvalidRequest", nameof(request));
                }
                DeviceSnapshots.Read<WakeOnLan.DeviceWakeSelection>(request.Change).Validate();
                break;
            case EDeviceAction.Login:
                if (request.Read.ResourceId is not null)
                {
                    throw new ArgumentException("Device.InvalidRequest", nameof(request));
                }
                DeviceSnapshots.Read<Login.DeviceLoginChange>(request.Change).Validate();
                break;
            case EDeviceAction.WindowsUpdate:
                if (request.Read.ResourceId is not null)
                {
                    throw new ArgumentException("Device.InvalidRequest", nameof(request));
                }
                _ = DeviceSnapshots.Read<WindowsUpdate.DeviceWindowsUpdateChange>(request.Change);
                break;
            case EDeviceAction.Power:
                if (request.Read.ResourceId is not null)
                {
                    throw new ArgumentException("Device.InvalidRequest", nameof(request));
                }
                DeviceSnapshots.Read<Power.DevicePowerChange>(request.Change).Validate();
                break;
            case EDeviceAction.Display:
                if (request.Read.ResourceId?.Length != 64 || !request.Read.ResourceId.All(Uri.IsHexDigit))
                {
                    throw new ArgumentException("Device.SelectDisplay", nameof(request));
                }
                DeviceSnapshots.Read<DeviceDisplayChange>(request.Change).Validate();
                break;
            case EDeviceAction.VirtualSwitchCreate:
            case EDeviceAction.VirtualSwitchDelete:
                DeviceVirtualSwitchRules.Validate(request);
                break;
            case EDeviceAction.Network:
            case EDeviceAction.NetworkProtocols:
                if (!Guid.TryParse(request.Read.ResourceId, out _))
                {
                    throw new ArgumentException("Device.SelectInterface", nameof(request));
                }
                if (request.Action == EDeviceAction.NetworkProtocols)
                {
                    DeviceSnapshots.Read<DeviceNetworkProtocols>(request.Change).Validate();
                }
                else
                {
                    DeviceNetworks.Validate(DeviceSnapshots.Read<DeviceNetworkChange>(request.Change));
                }
                break;
            case EDeviceAction.Hosts:
                DeviceHostsRules.Validate(DeviceSnapshots.Read<DeviceHostsChange>(request.Change).Content);
                break;
            case EDeviceAction.Environment:
                ValidateEnvironment(request.Read, DeviceSnapshots.Read<DeviceEnvironmentChange>(request.Change));
                break;
            case EDeviceAction.VirtualNicCreate:
            case EDeviceAction.VirtualNicVlan:
            case EDeviceAction.VirtualNicDelete:
                ValidateVirtualNic(request);
                break;
            default:
                DeviceFirewallRules.Validate(request.Action, request.Read.ResourceId, DeviceSnapshots.Read<DeviceFirewallChange>(request.Change));
                if (request.Action == EDeviceAction.FirewallRuleAdd)
                {
                    string id = "Contents.HostManager." + request.OperationId.ToString("N");
                    string resource = "rule/" + id;
                    DeviceFirewallRule rule = DeviceSnapshots.Read<DeviceFirewallChange>(request.Change).Rule!;
                    if (!string.Equals(request.Read.ResourceId, resource, StringComparison.Ordinal) || !string.Equals(rule.Id, id, StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Device.InvalidFirewallRule", nameof(request));
                    }
                }
                break;
        }
    }

    /// <summary>サービスのHKCUを暗黙に対象とせず、変数名と値をそのまま保持します。</summary>
    private static void ValidateEnvironment(DeviceReadRequest read, DeviceEnvironmentChange change)
    {
        string? name = read.ResourceId;
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (255 < name.Length || name.Any(character => char.IsControl(character) || character == '=')
            || (!change.Delete && change.Value is null) || (change.Delete && change.Value is not null) || change.Value?.Length is > 32767
            || change.Value?.Contains('\0', StringComparison.Ordinal) == true)
        {
            throw new ArgumentException("Device.InvalidEnvironmentVariable", nameof(change));
        }
    }

    /// <summary>管理OS用vNICのみに使う名前・スイッチ・VLANを確認します。</summary>
    private static void ValidateVirtualNic(DeviceChangeRequest request)
    {
        DeviceVirtualNicChange change = DeviceSnapshots.Read<DeviceVirtualNicChange>(request.Change);
        if (change.VlanId is < 1 or > 4094 || (request.Action != EDeviceAction.VirtualNicCreate
            && (request.Read.ResourceId is null || !request.Read.ResourceId.StartsWith(VirtualNicPrefix, StringComparison.Ordinal))))
        {
            throw new ArgumentException("Device.InvalidVirtualNic", nameof(request));
        }
        if (request.Action == EDeviceAction.VirtualNicCreate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(change.Name);
            if (64 < change.Name.Length || change.Name.Any(character => char.IsControl(character) || character is '*' or '?' or '[' or ']') || !Guid.TryParse(change.SwitchId, out _))
            {
                throw new ArgumentException("Device.InvalidVirtualNic", nameof(request));
            }
        }
    }
}
