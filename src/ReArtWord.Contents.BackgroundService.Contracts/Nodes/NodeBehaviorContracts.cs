using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>設定が期待する動作と、実装が明示的に対応する契約を照合します。</summary>
public static class NodeBehaviorContracts
{
    /// <summary>版未指定の既存設定は、初回に定めた契約1として扱います。</summary>
    /// <returns>設定が期待する動作契約。</returns>
    public static int Expected(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.ContractVersion ?? 1;
    }

    /// <summary>比較時は、意味が同じ未指定と契約1を同じ表現へ揃えます。</summary>
    /// <returns>初回契約ならnull、それ以外は明示した版。</returns>
    public static int? CanonicalVersion(NodeDefinition node)
    {
        if (Expected(node) == 1)
        {
            return null;
        }
        return node.ContractVersion;
    }

    /// <summary>宣言された互換版だけを受け付けます。設定形式の一致からは推測しません。</summary>
    /// <returns>実装がその動作契約に対応すると宣言しているか。</returns>
    public static bool Supports(NodeDescriptor descriptor, int expected)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return 0 < expected && (descriptor.ContractVersion == expected || descriptor.CompatibleContractVersions?.Contains(expected) == true);
    }

    /// <summary>開始・反映・復元前に表示する、不適合の理由を返します。</summary>
    /// <returns>不適合ならノードと契約欄を示す指摘、対応済みならnull。</returns>
    public static ValidationIssue? Validate(NodeDefinition node, NodeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(descriptor);
        int expected = Expected(node);
        if (expected < 1)
        {
            return new("node.contractVersion.invalid", "動作契約の版は1以上を指定してください。", node.Id, "contractVersion");
        }
        if (!Supports(descriptor, expected))
        {
            string message = string.Create(CultureInfo.InvariantCulture, $"この設定は動作契約 {expected} を要求しています。現在の実装は契約 {descriptor.ContractVersion} で、要求された版との互換性が宣言されていません。");
            if (!string.IsNullOrWhiteSpace(descriptor.ContractNotes))
            {
                message += " " + descriptor.ContractNotes;
            }
            return new("node.contractVersion.unsupported", message, node.Id, "contractVersion");
        }
        return null;
    }

    /// <summary>実装の契約宣言に、無効な版や重複が含まれていないことを確認します。</summary>
    /// <returns>現在版と互換版の宣言が有効か。</returns>
    public static bool ValidDeclaration(NodeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        IReadOnlyList<int> versions = descriptor.CompatibleContractVersions ?? [];
        return 0 < descriptor.ContractVersion && versions.All(version => 0 < version && version <= descriptor.ContractVersion)
            && versions.Distinct().Count() == versions.Count;
    }
}
