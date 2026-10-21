using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>新しい音声モデルと保存方針を、実際の翻訳資源で表示できることを確認します。</summary>
public sealed class AiGenerationSettingsTests
{
    /// <summary>保存値と表示ラベルを分離し、各モデルに同じ保存方針を表示します。</summary>
    /// <returns>日本語と英語の選択肢の確認です。</returns>
    [Fact]
    public async Task FilePoliciesHaveLocalizedChoicesAsync()
    {
        UiText text = new(new(new BrowserTestRuntime()));
        await text.InitializeAsync().ConfigureAwait(true);
        Assert.Equal("When the file exists", text.SettingName("existingFileMode"));
        Assert.Equal("Skip", text.SettingChoiceName("ai.audio.elevenlabs.v4.generate", "lockedFileMode", "skip"));
        Assert.Equal("Error", text.SettingChoiceName("file.copy", "existingFileMode", "error"));
        await text.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("ファイル存在時モード", text.SettingName("existingFileMode"));
        Assert.Equal("上書き", text.SettingChoiceName("ai.audio.qwen.audio3.generate", "existingFileMode", "overwrite"));
        Assert.Equal("エラー", text.SettingChoiceName("ai.audio.generate", "lockedFileMode", "error"));
        Assert.Equal("エラー", text.SettingChoiceName("file.transfer", "existingFileMode", "error"));
        Assert.Equal("Workspace ID", text.SettingName("workspaceId"));
        Assert.Equal("類似度", text.SettingName("similarity"));
    }

    /// <summary>画面に表示するWorkspace専用URLを、旧QwenやElevenの接続先と区別します。</summary>
    [Fact]
    public void ModelEndpointsMatchWorkspaceAndProvider()
    {
        Assert.Equal("https://api.elevenlabs.io/", AiServiceConnection.Endpoint("ai.audio.elevenlabs.v4.generate")!.AbsoluteUri);
        Assert.Equal("https://workspace-1.ap-southeast-1.maas.aliyuncs.com/", AiServiceConnection.Endpoint("ai.audio.qwen.audio3.generate", "singapore", "workspace-1")!.AbsoluteUri);
        Assert.Null(AiServiceConnection.Endpoint("ai.audio.qwen.audio3.generate"));
    }
}
