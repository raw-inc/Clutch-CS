using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>ログからの画面移動で識別子・接続先・他の表示設定を取り違えません。</summary>
public sealed class EditorWorkflowLocationTests
{
    /// <summary>URLに意味を持つ文字や日本語を含む識別子も一つの値として往復します。</summary>
    [Fact]
    public void LocationPreservesIdentifiersAndSourceCluster()
    {
        EditorWorkflowLocation expected = new("日本語&WF=1", "a+b/#?", "cluster/1", "peer&2");
        Assert.Equal(expected, EditorWorkflowLocation.Read("http://localhost" + expected.ToUri()));
        Assert.Null(EditorWorkflowLocation.Read("http://localhost/editor?node=alone"));
        Assert.Null(EditorWorkflowLocation.Read("http://localhost/editor?workflow="));
        Assert.Equal(new EditorWorkflowLocation("flow 1"), EditorWorkflowLocation.Read("http://localhost/editor?workflow=flow+1"));
    }

    /// <summary>移動後の再読込で再ジャンプせず、experimental等は維持します。</summary>
    [Fact]
    public void ConsumedLocationLeavesOtherQuerySettings()
    {
        Navigation navigation = new("http://localhost/editor?experimental=true&workflow=flow&node=a&cluster=c&peer=p#test");
        string uri = EditorWorkflowLocation.WithoutTarget(navigation);
        Assert.Equal("http://localhost/editor?experimental=true#test", uri);
        Assert.Null(EditorWorkflowLocation.Read(uri));
        Assert.Equal("/editor?workflow=flow", new EditorWorkflowLocation("flow").ToUri());
    }

    /// <summary>URLの整形だけを実行し、ブラウザーを操作しない移動境界です。</summary>
    private sealed class Navigation : NavigationManager
    {
        /// <summary>与えたURLを現在地として保持します。</summary>
        internal Navigation(string uri) => Initialize("http://localhost/", uri);

        /// <summary>この試験では実際の画面遷移を禁止します。</summary>
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
}
