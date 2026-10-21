using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>継続警告と操作結果を分け、旧通知の終了で最新結果を消しません。</summary>
public sealed class EditorNoticeHubTests
{
    /// <summary>本文のない結果は通知枠を消費せず、同用途の旧結果だけを解除します。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\r\n\t")]
    public void EmptyResultsClearOnlyTheirOwnChannel(string text)
    {
        EditorNoticeHub notices = new();
        int changes = 0;
        notices.Changed += () => changes++;
        notices.Set("operation", () => text);
        Assert.Empty(notices.Items);
        Assert.Equal(0, changes);

        notices.Set("cluster-observations", () => "offline", persistent: true);
        notices.Set("operation", () => "saved");
        notices.Set("operation", () => text);
        Assert.Equal(3, changes);
        EditorNotice retained = Assert.Single(notices.Items);
        Assert.Equal("cluster-observations", retained.Channel);
        Assert.True(retained.Persistent);
        Assert.Equal("offline", retained.Text());
        notices.Set("operation", () => text);
        Assert.Equal(3, changes);
    }

    /// <summary>警告の復帰と、期限切れの古い操作結果を独立して扱います。</summary>
    [Fact]
    public void RecoveryAndOldDismissalPreserveTheLatestOperation()
    {
        EditorNoticeHub notices = new();
        notices.Set("operation", () => "first");
        Guid previous = Assert.Single(notices.Items).Id;
        notices.Set("cluster-observations", () => "offline", persistent: true);
        notices.Set("operation", () => "second");
        Assert.Equal(2, notices.Items.Count);
        Assert.True(notices.Items.Single(item => item.Channel is "cluster-observations").Persistent);
        notices.Dismiss(previous);
        Assert.Equal("second", notices.Items.Single(item => item.Channel is "operation").Text());
        notices.Set("cluster-observations", null);
        Assert.Equal("second", Assert.Single(notices.Items).Text());
        notices.Dismiss(Assert.Single(notices.Items).Id);
        Assert.Empty(notices.Items);
    }

    /// <summary>同じ通知の更新は増殖せず、保持件数にも上限があります。</summary>
    [Fact]
    public void ReplacingAChannelDoesNotAccumulateCards()
    {
        EditorNoticeHub notices = new();
        for (int index = 0; index < 100; index++)
        {
            notices.Set("operation", () => "saved");
        }
        Assert.Single(notices.Items);
        notices.Set("cluster", () => "offline", persistent: true);
        notices.Set("selection", () => "unconfirmed");
        notices.Set("extra", () => "latest");
        Assert.Equal(3, notices.Items.Count);
        Assert.Equal("latest", notices.Items[0].Text());
    }
}
