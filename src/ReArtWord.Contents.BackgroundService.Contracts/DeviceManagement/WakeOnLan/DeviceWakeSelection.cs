namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;

/// <summary>物理NICの自動選択と、空も含む明示選択を区別します。</summary>
/// <param name="UsePhysicalDefaults">新しく認識した物理NICも自動で対象に含めます。</param>
/// <param name="InterfaceIds">明示選択した安定識別子です。</param>
public sealed record DeviceWakeSelection(bool UsePhysicalDefaults, IReadOnlyList<string> InterfaceIds)
{
    /// <summary>保存後の選択を、列挙順とGUIDの大文字小文字に依存せず確認します。</summary>
    /// <returns>自動選択と明示選択の両方が一致するかです。</returns>
    public bool Matches(DeviceWakeSelection actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        return UsePhysicalDefaults == actual.UsePhysicalDefaults && InterfaceIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(actual.InterfaceIds);
    }

    /// <summary>不正・重複・過大な選択を保存する前に拒否します。</summary>
    public void Validate()
    {
        if (InterfaceIds is null || 128 < InterfaceIds.Count || (UsePhysicalDefaults && InterfaceIds.Count != 0)
            || InterfaceIds.Any(id => !Guid.TryParse(id, out _)) || InterfaceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != InterfaceIds.Count)
        {
            throw new ArgumentException("Device.InvalidWakeSelection");
        }
    }
}
