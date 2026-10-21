namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration;

/// <summary>人が確認した候補版と有効版を固定する影響情報です。</summary>
/// <param name="TargetId">対象設定。</param>
/// <param name="SavedRevision">保存候補の版。</param>
/// <param name="EffectiveRevision">現有効版。nullは有効版なし。</param>
/// <param name="Token">比較した状態を反映要求で照合する識別子。</param>
/// <param name="Differences">実際の変更項目。</param>
/// <param name="KnownImpacts">確認できる適用範囲。</param>
/// <param name="UnknownImpacts">確認できない外部影響。</param>
/// <param name="Truncated">差分の取得上限に達したか。</param>
/// <param name="UnsavedCandidate">候補の保存・共有は未成立か。</param>
public sealed record ConfigurationImpactPreview(string TargetId, string SavedRevision, string? EffectiveRevision, string Token,
    IReadOnlyList<ConfigurationDifference> Differences, IReadOnlyList<string> KnownImpacts, IReadOnlyList<string> UnknownImpacts, bool Truncated, bool UnsavedCandidate = false);
