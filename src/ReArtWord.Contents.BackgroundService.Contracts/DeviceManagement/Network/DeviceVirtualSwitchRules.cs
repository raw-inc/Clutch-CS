namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>仮想スイッチの種別、固定対象、作成時だけ許す条件を検証します。</summary>
internal static class DeviceVirtualSwitchRules
{
    private static readonly string[] kinds = ["External", "Internal", "Private"];
    private static string SwitchPrefix => "switch/";
    private static string SwitchInventory => "switches";

    /// <summary>不正な組合せをOS操作の開始前に拒否します。</summary>
    internal static void Validate(DeviceChangeRequest request)
    {
        DeviceVirtualSwitchChange change = DeviceSnapshots.Read<DeviceVirtualSwitchChange>(request.Change);
        if (request.Action == EDeviceAction.VirtualSwitchDelete)
        {
            string? resource = request.Read.ResourceId;
            if (resource is null || !resource.StartsWith(SwitchPrefix, StringComparison.Ordinal)
                || !Guid.TryParse(resource[SwitchPrefix.Length..], out Guid id) || id == Guid.Empty
                || change.Name is not null || change.Kind is not null || change.PhysicalInterfaceId is not null || change.AllowManagementOS is not null)
            {
                throw new ArgumentException("Device.InvalidVirtualSwitch", nameof(request));
            }
            return;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(change.Name);
        if (!string.Equals(request.Read.ResourceId, SwitchInventory, StringComparison.Ordinal)
            || 64 < change.Name.Length || !string.Equals(change.Name, change.Name.Trim(), StringComparison.Ordinal)
            || change.Name.Any(character => char.IsControl(character) || character is '*' or '?' or '[' or ']')
            || !kinds.Contains(change.Kind, StringComparer.Ordinal))
        {
            throw new ArgumentException("Device.InvalidVirtualSwitch", nameof(request));
        }
        if (string.Equals(change.Kind, kinds[0], StringComparison.Ordinal))
        {
            if (!Guid.TryParse(change.PhysicalInterfaceId, out Guid id) || id == Guid.Empty || change.AllowManagementOS is null)
            {
                throw new ArgumentException("Device.SelectInterface", nameof(request));
            }
            return;
        }
        if (change.PhysicalInterfaceId is not null || change.AllowManagementOS is not null)
        {
            throw new ArgumentException("Device.InvalidVirtualSwitch", nameof(request));
        }
    }
}
