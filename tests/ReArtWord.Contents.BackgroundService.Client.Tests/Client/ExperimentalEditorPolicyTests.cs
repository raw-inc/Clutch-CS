using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>共通設定は実有効値とCASを使い、非表示時の追加設定照会を抑えます。</summary>
public sealed class ExperimentalEditorPolicyTests
{
    /// <summary>既存の状態キャッシュだけで通常画面へ方針を表示できます。</summary>
    /// <returns>追加のHTTP・Core設定読取りがないことです。</returns>
    [Fact]
    public async Task NormalViewDoesNotFetchClusterSettingsAsync()
    {
        int requests = 0;
        using ClientHttpHandler handler = new((_, _) =>
        {
            requests++;
            return Task.FromResult(Reply<object?>(null));
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost/") };
        ManagementApi api = new(client) { SelectedClusterId = "cluster", SelectedPeerId = "peer" };
        EditorLivePolicy policy = new(api, new(api, new(new BrowserTestRuntime())));
        await policy.RefreshAsync("cluster", "peer", "flow", readSettings: false, knownEnabled: false).ConfigureAwait(true);
        Assert.False(policy.Enabled);
        Assert.Equal(0, requests);
    }

    /// <summary>変更するのは対象の共有方針だけで、未反映の他設定を一緒に適用しません。</summary>
    /// <returns>固定版の保存要求です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingLivePolicyPreservesOtherSettingsAndRejectsPendingActivationAsync(bool pending)
    {
        ClusterOperationalSettings original = new("Asia/Tokyo", EFileConflictPolicy.Wait, EFileInternalFailurePolicy.Restore,
            new Dictionary<string, IReadOnlyList<string>> { ["group"] = ["host"] }, ESafetyConfirmation.Always);
        ClusterOperationalSnapshot saved = new(original, ContractJson.Hash(original), DateTimeOffset.UtcNow);
        ClusterOperationalSnapshot effective = saved;
        if (pending)
        {
            effective = saved with { Revision = "different" };
        }
        ClusterOperationalState state = new(saved, effective, EActivationPolicy.Auto);
        List<ClusterOperationalSaveRequest> writes = [];
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                ClusterOperationalSaveRequest input = JsonSerializer.Deserialize<ClusterOperationalSaveRequest>(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options)!;
                writes.Add(input);
                saved = new(input.Settings, ContractJson.Hash(input.Settings), DateTimeOffset.UtcNow);
                state = new(saved, saved, EActivationPolicy.Auto);
                ConfigurationPhaseResult success = new(EOutcome.Succeeded);
                return Reply(new ClusterOperationalSaveResult(input.OperationId, saved.Revision, [], success, success, success, success));
            }
            return Reply(state);
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost/") };
        ManagementApi api = new(client) { SelectedClusterId = "cluster", SelectedPeerId = "peer" };
        EditorLivePolicy policy = new(api, new(api, new(new BrowserTestRuntime())));
        await policy.RefreshAsync("cluster", "peer", "flow").ConfigureAwait(true);
        if (pending)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ChangeAsync(true)).ConfigureAwait(true);
            Assert.Empty(writes);
            return;
        }
        await policy.ChangeAsync(true).ConfigureAwait(true);
        ClusterOperationalSaveRequest actual = Assert.Single(writes);
        Assert.Equal(ContractJson.Hash(original), actual.ExpectedRevision);
        Assert.Equal(ContractJson.Hash(original), ContractJson.Hash(actual.Settings with { LiveWorkflowUpdates = false }));
        Assert.True(policy.Enabled);
    }

    /// <summary>製品と同じJSONで応答します。</summary>
    /// <returns>呼出元が破棄する応答です。</returns>
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ContractJson.Options) };
}
