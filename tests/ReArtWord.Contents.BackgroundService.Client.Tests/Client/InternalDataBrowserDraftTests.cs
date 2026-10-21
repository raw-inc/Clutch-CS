using ReArtWord.Contents.BackgroundService.Client.Operations.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>ブラウザーの確認済み基準と入力草稿を、Core未保存現在値から分離します。</summary>
public sealed class InternalDataBrowserDraftTests
{
    /// <summary>Core現在43・強い保存42ではオフライン基準は42だけです。</summary>
    [Fact]
    public void UnsavedEffectiveValueCannotBecomeBrowserConfirmedBaseline()
    {
        AvailabilityState state = new(EStoragePurpose.Runtime, "state:key", "peer", "saved", "volatile", "saved", IsUnsaved: true);
        AvailabilityValue current = new(NodeValue.FromObject(43), "volatile", state);
        AvailabilityValue confirmed = new(NodeValue.FromObject(42), "saved", state with { EffectiveRevision = "saved", IsUnsaved = false });
        InternalDataClientView view = new("cluster", "peer", Guid.NewGuid(), new("workflow", EInternalDataScope.Shared, "key"), DateTimeOffset.UtcNow, true, current, new(), Confirmed: confirmed);
        InternalDataBrowserDraft draft = InternalDataBrowserDraft.Capture(view, null, "43", false, null, false);
        Assert.Equal(42, draft.Baseline.Current.Value!.Json.GetInt32());
        Assert.Equal("saved", draft.Baseline.Current.Revision);
        Assert.False(draft.Baseline.Current.State.IsUnsaved);
        InternalDataBrowserDraft unconfirmed = InternalDataBrowserDraft.Capture(view with { Confirmed = null }, null, "43", false, null, false);
        Assert.False(unconfirmed.Baseline.Current.State.CanRead);
        Assert.Null(unconfirmed.Baseline.Current.Value);
    }

    /// <summary>未送信入力がある場合は、再読取りした新保存版へ比較元を進めません。</summary>
    [Fact]
    public void BrowserInputRetainsItsOriginalSavedRevision()
    {
        Guid instance = Guid.NewGuid();
        AvailabilityState state = new(EStoragePurpose.Runtime, "state:key", "peer", "one", "one", "one");
        AvailabilityValue old = new(NodeValue.FromObject(1), "one", state);
        InternalDataClientView first = new("cluster", "peer", instance, new("workflow", EInternalDataScope.Shared, "key"), DateTimeOffset.UtcNow, true, old, new(Offline: EAvailabilityOffline.LocalUnconfirmed), Confirmed: old);
        InternalDataBrowserDraft retained = InternalDataBrowserDraft.Capture(first, null, "7", true, null, false);
        AvailabilityValue later = new(NodeValue.FromObject(2), "two", state with { SavedRevision = "two", EffectiveRevision = "two" });
        InternalDataBrowserDraft result = InternalDataBrowserDraft.Capture(first with { Current = later, Confirmed = later }, retained, "7", true, null, false);
        Assert.Equal("one", result.Baseline.Current.Revision);
        Assert.Equal("7", result.Raw);
        Assert.Equal(instance, result.Baseline.InstanceId);
    }
}
