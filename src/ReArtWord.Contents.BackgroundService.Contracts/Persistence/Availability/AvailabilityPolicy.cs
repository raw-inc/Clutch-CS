namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

/// <summary>保存不能時に確認済み状態を維持するか、新しい未保存状態を使うかです。</summary>
public enum EAvailabilitySaveFailure
{
    /// <summary>最後の確認済み状態を維持します。</summary>
    KeepConfirmed,

    /// <summary>明示した未保存候補を現在有効にできます。</summary>
    UseUnsaved,
}

/// <summary>内部データのオフライン時の読取り・変更方針です。</summary>
public enum EAvailabilityOffline
{
    /// <summary>確認不能な用途を利用しません。</summary>
    Unavailable,

    /// <summary>最後に確認した値を古い可能性のある読取り専用として使います。</summary>
    LastConfirmedReadOnly,

    /// <summary>内部データの端末内未確定変更を許可します。</summary>
    LocalUnconfirmed,
}

/// <summary>用途別に事前指定する保存不能・オフライン方針です。</summary>
/// <param name="SaveFailure">保存不能時の既定は旧確認済み維持です。</param>
/// <param name="Offline">オフライン時の既定は確認済み値の読取り専用です。</param>
/// <param name="FallbackBackend">明示時だけ使用する独立候補保存先です。</param>
public sealed record AvailabilityPolicy(EAvailabilitySaveFailure SaveFailure = EAvailabilitySaveFailure.KeepConfirmed, EAvailabilityOffline Offline = EAvailabilityOffline.LastConfirmedReadOnly, EStorageBackend? FallbackBackend = null);
