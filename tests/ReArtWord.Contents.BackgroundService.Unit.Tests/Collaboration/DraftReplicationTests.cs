using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;

/// <summary>編集の順序、通知の逆転と競合解消をPeerの草稿間で検証します。</summary>
public sealed class DraftReplicationTests
{
    private const int Capacity = 256;
    private const int MaximumBytes = 1048576;
    private static readonly WorkflowDefinition Original = new("flow", "initial", [], []);

    /// <summary>一人の連続編集へ遅れた複製を戻しても、古い値との競合を作りません。</summary>
    [Fact]
    public void DelayedReplicaDoesNotConflictWithLaterEdits()
    {
        DraftSession first = Session(), second = Session();
        Apply(first, Original, Original with { Name = "first" });
        Receive(second, first);
        DraftAnnouncement stale = Announcement(second);
        Apply(first, first.View.Definition, Original with { Name = "second" });
        first.MergeAnnouncement(stale, DateTimeOffset.UtcNow, Capacity, MaximumBytes);
        Receive(second, first);
        for (int repeat = 0; repeat < 5; repeat++)
        {
            Receive(first, second);
            Receive(second, first);
        }
        Assert.Equal("second", first.View.Definition.Name);
        Assert.Equal(first.View.SessionVersion, second.View.SessionVersion);
        Assert.Empty(first.View.Conflicts);
        Assert.Empty(second.View.Conflicts);
    }

    /// <summary>真の競合は残し、解消前の通知が遅着しても選択を復活させません。</summary>
    [Fact]
    public void ResolvedConflictDoesNotReturnFromAnIdlePeer()
    {
        DraftSession first = Session(), second = Session();
        Apply(first, Original, Original with { Name = "first" });
        Apply(second, Original, Original with { Name = "second" });
        Receive(first, second);
        Assert.NotEmpty(first.View.Conflicts);
        Receive(second, first);
        DraftAnnouncement stale = Announcement(second);
        DraftView view = first.View;
        first.Apply(Change(view.Definition, view.Definition with { Name = "chosen" }) with
        {
            Resolutions = new Dictionary<string, EEditResolution> { ["/name"] = EEditResolution.UseEdited },
        }, DateTimeOffset.UtcNow, Capacity, MaximumBytes);
        first.MergeAnnouncement(stale, DateTimeOffset.UtcNow, Capacity, MaximumBytes);
        Receive(second, first);
        Receive(first, second);
        Assert.Equal("chosen", first.View.Definition.Name);
        Assert.Empty(first.View.Conflicts);
        Assert.Empty(second.View.Conflicts);
    }

    /// <summary>強制保存の確認と競合解消の通知順が入れ替わっても、解消済み候補を復活させません。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForcedSaveAndResolutionConvergeInEitherArrivalOrder(bool remoteRebasesFirst)
    {
        string revision = ContractJson.Hash(Original);
        DraftSession first = new(Original, revision, DateTimeOffset.UtcNow), second = new(Original, revision, DateTimeOffset.UtcNow);
        Apply(first, Original, Original with { Name = "one" });
        Receive(second, first);
        Apply(second, Original, Original with { Name = "two" });
        Receive(first, second);
        DraftView view = first.View;
        Assert.NotEmpty(view.Conflicts);
        WorkflowDefinition chosen = view.Definition with { Name = "chosen" };
        StoredWorkflow saved = new(chosen, ContractJson.Hash(chosen), DateTimeOffset.UtcNow, EActivationPolicy.Auto);
        first.Rebase(saved, DateTimeOffset.UtcNow);
        first.Apply(Change(view.Definition, chosen) with
        {
            Resolutions = new Dictionary<string, EEditResolution> { ["/name"] = EEditResolution.UseEdited },
        }, DateTimeOffset.UtcNow, Capacity, MaximumBytes);
        if (remoteRebasesFirst)
        {
            second.Rebase(saved, DateTimeOffset.UtcNow);
        }
        Receive(second, first);
        second.Rebase(saved, DateTimeOffset.UtcNow);
        for (int repeat = 0; repeat < 5; repeat++)
        {
            Receive(first, second);
            Receive(second, first);
        }
        Assert.Equal(saved.Revision, first.View.SessionVersion);
        Assert.Equal(first.View.SessionVersion, second.View.SessionVersion);
        Assert.Empty(first.View.Conflicts);
        Assert.Empty(second.View.Conflicts);
    }

    /// <summary>保存後の共通編集を基点にするため、元の値へ戻す編集も失いません。</summary>
    [Fact]
    public void ConcurrentEditsUseTheSharedUnsavedAncestor()
    {
        DraftSession first = Session(), second = Session();
        Apply(first, Original, Original with { Name = "shared" });
        Receive(second, first);
        Apply(first, first.View.Definition, first.View.Definition with { Name = "initial" });
        Apply(second, second.View.Definition, second.View.Definition with { Enabled = true });
        Receive(first, second);
        Receive(second, first);
        Assert.Equal("initial", first.View.Definition.Name);
        Assert.True(first.View.Definition.Enabled);
        Assert.Empty(first.View.Conflicts);
        Assert.Equal(first.View.SessionVersion, second.View.SessionVersion);
    }

    /// <summary>解消と無関係な並行編集の合流で、共通時点の競合を再追加しません。</summary>
    [Fact]
    public void ConcurrentUnrelatedEditDoesNotReviveResolvedConflict()
    {
        DraftSession first = Session(), second = Session();
        Apply(first, Original, Original with { Name = "first" });
        Apply(first, Original, Original with { Name = "second" });
        Receive(second, first);
        DraftChangeRequest resolution = Change(first.View.Definition, first.View.Definition) with
        {
            Resolutions = new Dictionary<string, EEditResolution> { ["/name"] = EEditResolution.KeepCurrent },
        };
        first.Apply(resolution, DateTimeOffset.UtcNow, Capacity, MaximumBytes);
        Apply(second, second.View.Definition, second.View.Definition with { Enabled = true });
        Receive(first, second);
        Receive(second, first);
        Assert.Empty(first.View.Conflicts);
        Assert.True(first.View.Definition.Enabled);
        Assert.Empty(second.View.Conflicts);
    }

    /// <summary>本文履歴の上限を超える連続編集後も、古い版番号は判定できます。</summary>
    [Fact]
    public void OldAnnouncementsStayOldAfterHistoryIsTrimmed()
    {
        DraftSession first = Session(), second = Session();
        Apply(first, Original, Original with { Name = "old" });
        Receive(second, first);
        DraftAnnouncement stale = Announcement(second);
        for (int index = 0; index < 30; index++)
        {
            Apply(first, first.View.Definition, first.View.Definition with { Name = index.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            _ = first.Announce(2, MaximumBytes);
        }
        first.MergeAnnouncement(stale, DateTimeOffset.UtcNow, Capacity, MaximumBytes);
        Assert.Equal("29", first.View.Definition.Name);
        Assert.Empty(first.View.Conflicts);
    }

    /// <summary>同じ元操作の再送は、後から進んだ内容を戻しません。</summary>
    [Fact]
    public void OperationReceiptTravelsWithTheDraft()
    {
        DraftSession first = Session(), second = Session();
        DraftChangeRequest request = Change(Original, Original with { Name = "first" });
        first.Apply(request, DateTimeOffset.UtcNow, Capacity, MaximumBytes);
        Apply(first, first.View.Definition, first.View.Definition with { Name = "second" });
        Receive(second, first);
        second.Apply(request, DateTimeOffset.UtcNow, Capacity, MaximumBytes);
        Assert.Equal("second", second.View.Definition.Name);
        Assert.Empty(second.View.Conflicts);
    }

    /// <summary>比較履歴の範囲外では双方の全文を残し、推測で編集を消しません。</summary>
    [Fact]
    public void MissingCommonHistoryRequiresExplicitSelection()
    {
        DraftSession first = Session(), second = Session();
        Apply(first, Original, Original with { Name = "shared" });
        Receive(second, first);
        Apply(first, first.View.Definition, first.View.Definition with { Name = "later" });
        Apply(first, first.View.Definition, first.View.Definition with { Enabled = true });
        _ = first.Announce(1, MaximumBytes);
        Apply(second, second.View.Definition, second.View.Definition with { Name = "initial" });
        Receive(first, second);
        EditConflict conflict = Assert.Single(first.View.Conflicts);
        Assert.Equal(string.Empty, conflict.Path);
        Assert.Equal("initial", conflict.Edited!.Value.Deserialize<WorkflowDefinition>(ContractJson.Options)!.Name);
        first.Apply(Change(first.View.Definition, second.View.Definition) with
        {
            Resolutions = new Dictionary<string, EEditResolution> { [string.Empty] = EEditResolution.UseEdited },
        }, DateTimeOffset.UtcNow, Capacity, MaximumBytes);
        Receive(second, first);
        Assert.Equal("initial", first.View.Definition.Name);
        Assert.Empty(first.View.Conflicts);
        Assert.Empty(second.View.Conflicts);
    }

    /// <summary>同じ保存を別Peerで確認しても、無操作の照会で版を増やし続けません。</summary>
    [Fact]
    public void ConcurrentIdenticalRebasesConvergeWithoutManufacturingEdits()
    {
        DraftSession first = Session(), second = Session();
        StoredWorkflow saved = new(Original, ContractJson.Hash(Original), DateTimeOffset.UtcNow, EActivationPolicy.Auto);
        first.Rebase(saved, DateTimeOffset.UtcNow);
        second.Rebase(saved, DateTimeOffset.UtcNow);
        DraftAnnouncement a = Announcement(first), b = Announcement(second);
        first.MergeAnnouncement(b, DateTimeOffset.UtcNow, Capacity, MaximumBytes);
        second.MergeAnnouncement(a, DateTimeOffset.UtcNow, Capacity, MaximumBytes);
        long count = Announcement(first).Clock!.Values.Sum();
        for (int repeat = 0; repeat < 8; repeat++)
        {
            Receive(first, second);
            Receive(second, first);
        }
        Assert.Equal(count, Announcement(first).Clock!.Values.Sum());
        Assert.Equal(count, Announcement(second).Clock!.Values.Sum());
        Assert.Empty(first.View.Conflicts);
    }

    /// <summary>他Peerが同じ保存を確認しただけの遅延通知を、自分の次の編集との競合にしません。</summary>
    [Fact]
    public void DelayedSaveAcknowledgementDoesNotConflictWithItsAuthorsNextEdit()
    {
        DraftSession first = Session(), second = Session(), third = Session();
        Apply(first, Original, Original with { Name = "saved by me" });
        Receive(second, first);
        Receive(third, first);
        StoredWorkflow saved = new(first.View.Definition, first.View.SessionVersion, DateTimeOffset.UtcNow, EActivationPolicy.Auto);
        second.Rebase(saved, DateTimeOffset.UtcNow);
        Receive(first, second);
        Apply(first, first.View.Definition, first.View.Definition with { Name = "my next edit" });
        third.Rebase(saved, DateTimeOffset.UtcNow);
        Receive(first, third);
        Receive(second, first);
        Receive(third, first);
        Assert.Equal("my next edit", first.View.Definition.Name);
        Assert.Empty(first.View.Conflicts);
        Assert.Empty(second.View.Conflicts);
        Assert.Empty(third.View.Conflicts);
        Assert.Equal(first.View.SessionVersion, third.View.SessionVersion);
    }

    /// <summary>独立した草稿です。</summary>
    /// <returns>草稿です。</returns>
    private static DraftSession Session() => new(Original, null, DateTimeOffset.UtcNow);

    /// <summary>複製状態を取得します。</summary>
    /// <returns>通知です。</returns>
    private static DraftAnnouncement Announcement(DraftSession session) => session.Announce(Capacity, MaximumBytes);

    /// <summary>別Peerの状態を受信します。</summary>
    private static void Receive(DraftSession target, DraftSession source) => target.MergeAnnouncement(Announcement(source), DateTimeOffset.UtcNow, Capacity, MaximumBytes);

    /// <summary>UIの差分を適用します。</summary>
    private static void Apply(DraftSession session, WorkflowDefinition original, WorkflowDefinition edited)
        => Assert.Empty(session.Apply(Change(original, edited), DateTimeOffset.UtcNow, Capacity, MaximumBytes).Validation);

    /// <summary>版を固定した要求です。</summary>
    /// <returns>差分要求です。</returns>
    private static DraftChangeRequest Change(WorkflowDefinition original, WorkflowDefinition edited)
        => new(Guid.NewGuid(), original.Id, Guid.NewGuid(), null, ContractJson.Hash(original), original, edited, new Dictionary<string, EEditResolution>());
}
