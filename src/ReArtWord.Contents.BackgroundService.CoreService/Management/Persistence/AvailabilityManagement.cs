using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Persistence;

/// <summary>保存問題を値や秘密の一覧として公開せずに照会します。</summary>
internal static class AvailabilityManagement
{
    /// <summary>候補本文を含まない参照専用操作を登録します。</summary>
    public static void Register(ManagementRegistry registry, ServiceRuntime runtime)
    {
        registry.ReadValue("availability.list", value =>
        {
            AvailabilityListQuery query = new();
            if (value is not null)
            {
                query = ManagementRegistry.Body<AvailabilityListQuery>(value);
            }
            if (query.Limit is < 1 or > 100 || 100 < query.Cursor?.Length)
            {
                throw new ManagementInputException();
            }
            AvailabilityState[] states = runtime.Persistence.Availability.GetStates().OrderBy(item => item.Purpose).ThenBy(item => item.DatasetId, StringComparer.Ordinal).ToArray();
            string owner = ContractJson.Hash(states);
            int start = ManagementPageCursor.Read(query.Cursor, owner, states.Length);
            AvailabilityState[] page = states.Skip(start).Take(query.Limit).ToArray();
            return new AvailabilityListPage(runtime.Configuration.Availability, page, ManagementPageCursor.Next(owner, start + page.Length, states.Length));
        });
    }
}
