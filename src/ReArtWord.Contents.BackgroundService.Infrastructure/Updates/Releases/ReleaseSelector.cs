using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

/// <summary>共通リリース方針を維持して実RIDに一致するペイロードだけを選びます。</summary>
public static class ReleaseSelector
{
    /// <summary>全variantの識別子、互換集合、RID重複と相対パスを確認します。</summary>
    public static void Validate(ClusterUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReleaseVerifier.Validate(request.Manifest);
        if (!Path.IsPathFullyQualified(request.PayloadRoot))
        {
            throw new InvalidDataException("payload rootは絶対pathで指定してください。");
        }
        if (request.Variants is null)
        {
            return;
        }
        if (request.Variants.Count == 0)
        {
            throw new InvalidDataException("RID別成果物が空です。");
        }
        HashSet<string> platforms = new(StringComparer.Ordinal);
        HashSet<string> compatibility = new(request.Manifest.CompatibleReleaseIds, StringComparer.Ordinal);
        foreach (ReleaseVariant variant in request.Variants)
        {
            ReleaseVerifier.Validate(variant.Manifest);
            if (!platforms.Add(variant.Manifest.Platform) || !string.Equals(variant.Manifest.ReleaseId, request.Manifest.ReleaseId, StringComparison.Ordinal) || !compatibility.SetEquals(variant.Manifest.CompatibleReleaseIds) || !ReleaseVerifier.IsRelativePath(variant.RelativePayloadPath))
            {
                throw new InvalidDataException("RID別成果物には固有RID、共通release/互換集合、安全な相対pathが必要です。");
            }
        }
    }

    /// <summary>対象のRIDを黙って近いOSや既定版へ置換しません。</summary>
    /// <returns>単一RIDの既存更新入力へ固定した選択です。</returns>
    public static ClusterUpdateRequest Select(ClusterUpdateRequest request, string platform)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        Validate(request);
        if (request.Variants is null)
        {
            if (!string.Equals(request.Manifest.Platform, platform, StringComparison.Ordinal))
            {
                throw new InvalidDataException("対象RIDの成果物がありません。");
            }
            return request;
        }
        ReleaseVariant variant = request.Variants.SingleOrDefault(item => string.Equals(item.Manifest.Platform, platform, StringComparison.Ordinal)) ?? throw new InvalidDataException("対象RIDの成果物がありません。");
        string path = Path.GetFullPath(Path.Combine(request.PayloadRoot, variant.RelativePayloadPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!UpdateLayout.Contains(Path.GetFullPath(request.PayloadRoot), path))
        {
            throw new InvalidDataException("RID別payloadがbundle外へ出ています。");
        }
        return request with { Manifest = variant.Manifest, PayloadRoot = path, Variants = null };
    }
}
