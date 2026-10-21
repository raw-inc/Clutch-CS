using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Persistence;

/// <summary>取得元と比較基準を変えず、ブラウザー入力を外部操作キューとは別に保持します。</summary>
internal sealed record InternalDataBrowserDraft(InternalDataClientView Baseline, string? Raw, Guid? PendingCommandId = null, bool WriteUnknown = false)
{
    /// <summary>ブラウザーのオフライン基準へ強い保存確認値だけを保持します。</summary>
    /// <returns>元の基準版と未送信入力です。</returns>
    internal static InternalDataBrowserDraft Capture(InternalDataClientView view, InternalDataBrowserDraft? retained, string raw, bool dirty, Guid? command, bool unknown)
    {
        AvailabilityValue confirmed = view.Confirmed ?? new(null, null, view.Current.State with { CanRead = false, CanWrite = false, Reason = "InternalData.ConfirmedUnknown" });
        InternalDataClientView baseline = view with { Current = confirmed, Remote = null, PendingCommandId = null };
        if (dirty && retained is not null && retained.Baseline.InstanceId == view.InstanceId && retained.Baseline.Target == view.Target
            && string.Equals(retained.Baseline.ClusterId, view.ClusterId, StringComparison.Ordinal) && string.Equals(retained.Baseline.PeerId, view.PeerId, StringComparison.Ordinal)
            && !retained.Baseline.Current.State.IsUnsaved && !retained.Baseline.Current.State.IsLocalUnconfirmed)
        {
            baseline = retained.Baseline;
        }
        string? draft = null;
        if (dirty)
        {
            draft = raw;
        }
        return new(baseline, draft, command, unknown);
    }
}
