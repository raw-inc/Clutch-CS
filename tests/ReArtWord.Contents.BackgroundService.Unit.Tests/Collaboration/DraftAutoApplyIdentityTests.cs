using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;

/// <summary>下書きの新しい編集にだけ自動保存IDを固定し、同期・再起動で作り直さないことを確認します。</summary>
public sealed class DraftAutoApplyIdentityTests
{
    /// <summary>新しい編集のIDは複製され、同じ要求の再送では変更されません。</summary>
    [Fact]
    public void EditedDraftKeepsItsOperationIdAcrossPeerReplication()
    {
        WorkflowDefinition original = new("flow", "initial", [], []);
        DraftSession first = new(original, null, DateTimeOffset.UtcNow), second = new(original, null, DateTimeOffset.UtcNow);
        DraftChangeRequest request = new(Guid.NewGuid(), original.Id, Guid.NewGuid(), null, ContractJson.Hash(original), original,
            original with { Name = "edited" }, new Dictionary<string, Contracts.Configuration.Editing.EEditResolution>());
        first.Apply(request, DateTimeOffset.UtcNow, 256, 1048576);
        Guid id = first.View.AutoApplyOperationId!.Value;
        Assert.False(OperationIdentity.TryGetIssuedAt(id, out _));
        DraftAnnouncement saved = ContractJson.Copy(first.Announce(256, 1048576));
        second.MergeAnnouncement(saved, DateTimeOffset.UtcNow, 256, 1048576);
        first.Apply(request, DateTimeOffset.UtcNow, 256, 1048576);
        Assert.Equal(id, first.View.AutoApplyOperationId);
        Assert.Equal(id, second.View.AutoApplyOperationId);
    }

    /// <summary>生成方式のない旧下書きは、読取り・再通知だけでは新しい操作にしません。</summary>
    [Fact]
    public void LegacyDraftAndNotificationRetainTheirOriginalGeneration()
    {
        WorkflowDefinition original = new("flow", "initial", [], []);
        DraftSession session = new(original, null, DateTimeOffset.UtcNow);
        DraftView old = new(original.Id, null, ContractJson.Hash(original with { Name = "old edit" }), original with { Name = "old edit" }, [], [], DateTimeOffset.UtcNow);
        session.MergeAnnouncement(new(original, old), DateTimeOffset.UtcNow, 256, 1048576);
        Assert.Null(session.View.AutoApplyOperationId);
        Assert.Null(DraftAutoApplyIdentity.Change(old, old.SessionVersion, Guid.NewGuid()));
    }

    /// <summary>並行した同値編集を受信する順序によって、自動保存IDを分岐させません。</summary>
    [Fact]
    public void ConcurrentEqualDraftsChooseTheSameStoredOperationId()
    {
        WorkflowDefinition definition = new("flow", "same", [], []);
        DraftView left = new(definition.Id, null, ContractJson.Hash(definition), definition, [], [], DateTimeOffset.UtcNow) { AutoApplyOperationId = Guid.NewGuid() };
        DraftView right = left with { AutoApplyOperationId = Guid.NewGuid() };
        Assert.Equal(DraftAutoApplyIdentity.Merge(left, right, left.SessionVersion), DraftAutoApplyIdentity.Merge(right, left, left.SessionVersion));
        Assert.Null(DraftAutoApplyIdentity.Merge(left, right with { AutoApplyOperationId = null }, left.SessionVersion));
    }
}
