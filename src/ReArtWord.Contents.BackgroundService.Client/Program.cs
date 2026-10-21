using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;

namespace ReArtWord.Contents.BackgroundService.Client;

/// <summary>三つの管理画面をブラウザー内のBlazorで実行します。</summary>
internal static class Program
{
    /// <summary>HTTPとブラウザー境界を構成して画面を起動します。</summary>
    /// <returns>ブラウザーHostの実行です。</returns>
    // 抑制理由: C#エントリポイントの固定signatureであるためです。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "MainはCancellationTokenを受け取れません。"),
     SuppressMessage("SourceQuality", "RAW601", Justification = "MainはC#の固定エントリポイントです。")]
    private static async Task Main(string[] args)
    {
        WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");
        builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress), Timeout = TimeSpan.FromSeconds(30) });
        builder.Services.AddScoped<ManagementApi>();
        builder.Services.AddScoped<BrowserStorage>();
        builder.Services.AddScoped<Localization.DisplayTimePreferences>();
        builder.Services.AddScoped(provider => new Localization.UiText(provider.GetRequiredService<BrowserStorage>()));
        builder.Services.AddScoped<EditorTargetSelection>();
        builder.Services.AddScoped<WorkflowEditor.Navigation.EditorNoticeHub>();
        builder.Services.AddScoped<ConnectionState>();
        builder.Services.AddScoped<EditorDocument>();
        builder.Services.AddScoped<EditorRecipeDrafts>();
        builder.Services.AddScoped<EditorPresentationCache>();
        builder.Services.AddScoped<SettingChoiceProvider>();
        builder.Services.AddScoped<EditorCollaboration>();
        builder.Services.AddScoped<EditorLivePolicy>();
        builder.Services.AddScoped<EditorNodeActivity>();
        builder.Services.AddScoped<EditorScriptAnalysis>();
        builder.Services.AddScoped<EditorDiagnostics>();
        builder.Services.AddScoped<EditorRuntimeDiagnostics>();
        builder.Services.AddScoped<EditorPreviews>();
        builder.Services.AddScoped<EditorConcurrencyQueues>();
        builder.Services.AddScoped<EditorToggleStates>();
        builder.Services.AddScoped<OperationJournal>();
        builder.Services.AddScoped<OperationJournalStorage>();
        builder.Services.AddScoped<Overview.Updates.FleetUpdateProgress>();
        await builder.Build().RunAsync().ConfigureAwait(false);
    }
}
