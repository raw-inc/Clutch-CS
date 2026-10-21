using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Services;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>標準の発光設定と既存ブラウザー設定の復元を検証します。</summary>
public sealed class GraphViewOptionsTests
{
    /// <summary>未保存・旧形式では発光し、保存済みの明示選択は試験機能の有無に依存しません。</summary>
    /// <returns>実際のブラウザー保存境界を通した表示設定の検証です。</returns>
    [Theory]
    [InlineData(null, true)]
    [InlineData("{}", true)]
    [InlineData("{\"wireShape\":\"straight\",\"showPortTypes\":false}", true)]
    [InlineData("{\"highlightRunning\":false,\"realtimePointer\":true}", false)]
    [InlineData("{\"highlightRunning\":true,\"realtimePointer\":true}", true)]
    public async Task RunningGlowIsStandardAndSavedChoicesRemainAsync(string? json, bool expected)
    {
        BrowserTestRuntime browser = new();
        string key = "contents.editor.view.v1";
        if (json is not null)
        {
            browser.Local[key] = json;
        }
        BrowserStorage storage = new(browser);
        GraphViewOptions saved = await storage.ReadAsync<GraphViewOptions>(key).ConfigureAwait(true) ?? new();
        Assert.Equal(expected, saved.HighlightRunning);
        Assert.Equal(expected, saved.ForEditor(false).HighlightRunning);
        Assert.Equal(expected, saved.ForEditor(true).HighlightRunning);
        Assert.False(saved.ForEditor(false).RealtimePointer);
        Assert.Equal(saved.RealtimePointer, saved.ForEditor(true).RealtimePointer);
        await storage.WriteAsync(key, saved).ConfigureAwait(true);
        Assert.Equal(saved, await storage.ReadAsync<GraphViewOptions>(key).ConfigureAwait(true));
    }
}
