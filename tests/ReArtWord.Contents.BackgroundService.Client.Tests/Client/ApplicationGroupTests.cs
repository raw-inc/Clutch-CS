using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Resources;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>グループの編集と実Razorの操作結果を確認します。</summary>
public sealed class ApplicationGroupTests
{
    /// <summary>表示名や順序の編集でIDを作り直さず、保存済みの一覧を変更しません。</summary>
    [Fact]
    public void DraftPreservesIdentityAndSeparatesSavedMembers()
    {
        ProcessGroupDefinition original = new("group", "Original", [new("first", 1000), new("second", 2000)], RecoveryMode: EProcessGroupRecoveryMode.EntireGroup);
        ApplicationGroupDraft draft = new(original) { Name = "Renamed" };
        draft.Move(draft.Members[1], -1);
        draft.Members[0].Delay = "3000";
        ProcessGroupDefinition saved = draft.Build();
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal(new ProcessGroupMember("second", 3000), saved.Members[0]);
        Assert.Equal(new ProcessGroupMember("first", 1000), original.Members[0]);
        Assert.Equal(EProcessGroupRecoveryMode.EntireGroup, saved.RecoveryMode);
    }

    /// <summary>未設定のグループから勝手に監視や強制終了を有効化しません。</summary>
    [Fact]
    public void NewGroupRequiresMembersAndExplicitAutomaticRecovery()
    {
        ApplicationGroupDraft draft = new(null) { Name = "Display" };
        Assert.False(draft.Automatic);
        Assert.Equal(EProcessStopPolicy.WaitForManual, draft.StopPolicy);
        Assert.Throws<InvalidOperationException>(() => draft.Build());
        draft.Members.Add(new(new("first")));
        draft.Members.Add(new(new("first")));
        Assert.Throws<InvalidOperationException>(() => draft.Build());
    }

    /// <summary>受付を完了と表示せず、結果不明時も同じ操作を再送しません。</summary>
    /// <returns>固定した所有先・確認版と、待機表示の確認です。</returns>
    [Theory]
    [InlineData(EOutcome.Succeeded)]
    [InlineData(EOutcome.Unknown)]
    public async Task PanelPinsOwnerAndVersionWithoutReplayingUnknownCommandsAsync(EOutcome outcome)
    {
        ProcessGroupDefinition definition = new("group", "Display", [new("first"), new("second", 1000)]);
        RegisteredProcessGroup group = new("version-1", definition, new("group", null, EProcessGroupPhase.Idle));
        List<ProcessGroupCommandRequest> commands = [];
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            Assert.Equal("cluster", Assert.Single(request.Headers.GetValues("X-Contents-Cluster")));
            Assert.Equal("peer", Assert.Single(request.Headers.GetValues("X-Contents-Peer")));
            string commandPath = "/api/process-groups/command";
            object value;
            if (string.Equals(request.RequestUri!.AbsolutePath, commandPath, StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                commands.Add((await request.Content!.ReadFromJsonAsync<ProcessGroupCommandRequest>(ContractJson.Options, token).ConfigureAwait(false))!);
                group = group with { Status = new("group", Guid.NewGuid(), EProcessGroupPhase.Waiting) };
                value = new ProcessGroupOperationResult(outcome, group.Status);
            }
            else
            {
                value = new[] { group };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ContractJson.Options) };
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(http);
        GroupActivator activator = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(new BrowserTestRuntime());
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        services.AddSingleton(api);
        services.AddSingleton<IComponentActivator>(activator);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = await renderer.RenderComponentAsync<ApplicationGroupPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ClusterId"] = "cluster", ["PeerId"] = "peer", ["Owner"] = "Display PC", ["Available"] = true,
            })).ConfigureAwait(true);
            Assert.Contains("Application groups", output.ToHtmlString(), StringComparison.Ordinal);
            ApplicationGroupPanel panel = activator.Panel!;
            typeof(ApplicationGroupPanel).GetMethod("Edit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(panel, [group]);
            await panel.SetParametersAsync(ParameterView.Empty).ConfigureAwait(true);
            Assert.Contains("Stop all applications, then restart in order", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("Delay before this application starts (ms)", output.ToHtmlString(), StringComparison.Ordinal);
            typeof(ApplicationGroupPanel).GetMethod("CloseEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(panel, null);
            api.SelectedClusterId = "different-cluster";
            api.SelectedPeerId = "different-peer";
            MethodInfo command = typeof(ApplicationGroupPanel).GetMethod("CommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await ((Task)command.Invoke(panel, [group, EProcessGroupCommand.Start, CancellationToken.None])!).ConfigureAwait(true);
            await panel.RefreshAsync().ConfigureAwait(true);
            await panel.SetParametersAsync(ParameterView.Empty).ConfigureAwait(true);
            Assert.Equal(new ProcessGroupCommandRequest("group", EProcessGroupCommand.Start, "version-1"), Assert.Single(commands));
            string html = output.ToHtmlString();
            Assert.Contains("Waiting for group startup", html, StringComparison.Ordinal);
            if (outcome == EOutcome.Succeeded)
            {
                Assert.Contains("Group operation accepted.", html, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Application group status could not be confirmed.", html, StringComparison.Ordinal);
                Assert.DoesNotContain("Group operation was not accepted", html, StringComparison.Ordinal);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>実コンポーネントのインスタンスを、UIイベントと同じ境界で操作します。</summary>
    private sealed class GroupActivator : IComponentActivator
    {
        internal ApplicationGroupPanel? Panel
        {
            get; private set;
        }

        /// <summary>子コンポーネントは通常どおり生成します。</summary>
        /// <returns>生成した実Razorコンポーネントです。</returns>
        public IComponent CreateInstance(Type componentType)
        {
            IComponent instance = (IComponent)Activator.CreateInstance(componentType)!;
            if (instance is ApplicationGroupPanel panel)
            {
                Panel = panel;
            }
            return instance;
        }
    }
}
