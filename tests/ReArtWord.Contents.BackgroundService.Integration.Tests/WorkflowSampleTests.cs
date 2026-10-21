using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>配布サンプルを画面と同じ形式で読み込み、実Coreの検証へ渡します。</summary>
public sealed class WorkflowSampleTests
{
    /// <summary>JSONの定数入力とYAMLの読込みを含め、全定義を実行前に検証できます。</summary>
    /// <returns>サンプルの読込みと検証の完了です。</returns>
    [Fact]
    public async Task SamplesCanBeImportedAndValidatedAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        CorePeer peer = new(cluster, "samples", CorePeer.AvailablePort(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        string directory = Path.Combine(AppContext.BaseDirectory, "WorkflowSamples");
        string[] files = Directory.GetFiles(directory);
        Assert.NotEmpty(files);
        foreach (string path in files)
        {
            string text = await File.ReadAllTextAsync(path).ConfigureAwait(true);
            WorkflowDefinition? definition;
            if (text.TrimStart().StartsWith('{'))
            {
                definition = JsonSerializer.Deserialize<WorkflowDefinition>(text, ContractJson.Options);
            }
            else
            {
                definition = await peer.GetAsync<WorkflowDefinition>("workflows.parse", new WorkflowYamlImport(text)).ConfigureAwait(true);
            }
            Assert.NotNull(definition);
            JsonElement result = await peer.GetAsync<JsonElement>("workflows.validate", definition).ConfigureAwait(true);
            Assert.True(result.GetProperty("valid").GetBoolean(), Path.GetFileName(path) + ": " + result.GetProperty("issues").GetRawText());
        }
    }
}
