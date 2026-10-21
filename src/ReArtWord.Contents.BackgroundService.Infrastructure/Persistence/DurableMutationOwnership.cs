using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>保存要求の可変部分だけを所有し、大きな不変JSON本文を直列化し直しません。</summary>
internal static class DurableMutationOwnership
{
    /// <summary>配列・解消候補・バイト列・型定義と、借用JSON文書の寿命を呼出元から分離します。</summary>
    /// <returns>元の要求指紋を保つ独立した保存候補です。</returns>
    internal static DurableMutation[] Copy(IReadOnlyList<DurableMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        DurableMutation[] result = new DurableMutation[mutations.Count];
        for (int index = 0; index < mutations.Count; index++)
        {
            DurableMutation mutation = mutations[index];
            ArgumentNullException.ThrowIfNull(mutation);
            NodeValue? value = mutation.Value;
            if (value is not null)
            {
                Contracts.Values.ValueTypeDefinition? type = value.DataType;
                if (type is not null)
                {
                    type = ContractJson.Copy(type);
                }
                value = value with { Json = value.Json.Clone(), Binary = value.Binary.ToArray(), DataType = type };
            }
            result[index] = mutation with { Value = value, ResolveVersions = mutation.ResolveVersions?.ToArray() };
        }
        return result;
    }
}
