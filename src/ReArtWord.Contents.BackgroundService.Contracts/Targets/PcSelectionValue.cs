using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Targets;

/// <summary>PC選択の専用値を読み取り、不正な条件や過大な入れ子を拒否します。</summary>
public static class PcSelectionValue
{
    /// <summary>同時に確定している設定入力は先頭だけを採用し、候補を混合しません。</summary>
    /// <returns>一つの入力が持つ選択条件の独立した複製です。</returns>
    public static NodeValue First(IEnumerable<NodeValue> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        NodeValue first = inputs.FirstOrDefault() ?? throw new FormatException("PC候補の入力がありません。");
        _ = Read(first);
        return first.Copy();
    }

    /// <summary>一つのポートから渡された選択条件を読み取ります。</summary>
    /// <returns>検証済みの選択条件。</returns>
    public static IReadOnlyList<PcSelection> Read(NodeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind != EValueKind.Json || value.Json.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("PC選択の配列が必要です。");
        }
        PcSelection[] selections = value.Json.Deserialize<PcSelection[]>(ContractJson.Options)
            ?? throw new FormatException("PC選択の値がありません。");
        int remaining = 128;
        foreach (PcSelection selection in selections)
        {
            Validate(selection, 0, ref remaining);
        }
        return selections;
    }

    /// <summary>型と意味が一致する選択条件だけを許可します。</summary>
    private static void Validate(PcSelection selection, int depth, ref int remaining)
    {
        if (selection is null || 8 < depth || --remaining < 0)
        {
            throw new FormatException("PC選択の件数または入れ子が上限を超えています。");
        }
        if (selection.Lowest is { } resource)
        {
            if (!Enum.IsDefined(resource.Resource) || selection.AllHosts || selection.HostIds is not null
                || (resource.UsedBytes && resource.Resource is not (EPcResource.Ram or EPcResource.Vram))
                || (resource.GpuId is not null && (string.IsNullOrWhiteSpace(resource.GpuId) || resource.Resource is not (EPcResource.Gpu or EPcResource.Vram))))
            {
                throw new FormatException("リソースの比較条件が一致しません。");
            }
            foreach (PcSelection candidate in selection.Within ?? [])
            {
                Validate(candidate, depth + 1, ref remaining);
            }
            return;
        }
        if (selection.Within is not null || (selection.AllHosts && selection.HostIds is not null)
            || (!selection.AllHosts && selection.HostIds is null)
            || selection.HostIds?.Any(string.IsNullOrWhiteSpace) == true || 128 < selection.HostIds?.Count)
        {
            throw new FormatException("PC一覧または全参加PCのどちらかを指定してください。");
        }
    }
}
