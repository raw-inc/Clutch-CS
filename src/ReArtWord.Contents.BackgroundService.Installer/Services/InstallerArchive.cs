using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Clients;

namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>修復画面から一つのクラスターへ、確認済みアーカイブを復元します。</summary>
internal static class InstallerArchive
{
    /// <summary>明示した通信経路の実体へ転送し、変更するWF一覧を取得します。</summary>
    /// <returns>実行確認画面の準備です。</returns>
    internal static async Task PrepareAsync(InstallationViewModel state, CancellationToken cancellationToken = default)
    {
        state.Repair.Archive.Preview = null;
        state.Repair.Archive.Target = null;
        ObservedTarget[] targets = state.Selected.ToArray();
        if (targets.Length == 0 || targets.Select(target => (target.ClusterId, target.Binding)).Distinct().Count() != 1
            || targets.Any(target => target.Peer?.Status != EPeerStatus.Ready))
        {
            throw new InvalidDataException(state.Text("Select one Ready cluster and one network route for archive restore.", "復元するクラスターを一つ選び、同じ通信経路で使用可能な対象を指定してください。"));
        }
        if (string.IsNullOrWhiteSpace(state.Repair.Archive.Path))
        {
            throw new InvalidDataException(state.Text("Select an archive ZIP file.", "復元するZIPファイルを選んでください。"));
        }
        ObservedTarget target = targets.OrderBy(item => item.PeerId, StringComparer.Ordinal).First();
        UpdateObserver observer = new(target.Binding, target.ClusterId);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
        await observer.FindAsync(target.PeerId, target.Peer!.InstanceId, cancellationToken).ConfigureAwait(false);
        using FileStream file = File.OpenRead(state.Repair.Archive.Path);
        Guid operation = Guid.NewGuid();
        Task<JsonElement> SendAsync(string action, object body, CancellationToken token) => SendCoreAsync(observer, target, action, body, false, token);
        await ArchiveFileTransfer.UploadAsync(file, operation, SendAsync, cancellationToken).ConfigureAwait(false);
        JsonElement response = await SendAsync("archive.preview", new ArchivePreviewRequest(operation, state.Repair.Archive.Mode), cancellationToken).ConfigureAwait(false);
        ArchiveImportPreview preview = response.Deserialize<ArchiveImportPreview>(ContractJson.Options) ?? throw new InvalidDataException("Archive preview is missing.");
        if (preview.OperationId != operation)
        {
            throw new InvalidDataException("Archive preview belongs to another operation.");
        }
        state.Repair.Archive.Preview = preview;
        state.Repair.Archive.Target = target;
    }

    /// <summary>確認画面で固定した内容を開始し、通信断でも別実体へ再送しません。</summary>
    /// <returns>結果と元操作IDです。</returns>
    internal static async Task<WizardResult> ExecuteAsync(InstallationViewModel state, IProgress<OperationProgress> progress, CancellationToken cancellationToken = default)
    {
        ArchiveImportPreview preview = state.Repair.Archive.Preview ?? throw new InvalidOperationException("Preview the archive first.");
        ObservedTarget target = state.Repair.Archive.Target ?? throw new InvalidOperationException("The archive target is missing.");
        bool submitted = false;
        string identity = "Operation: " + preview.OperationId.ToString("D");
        try
        {
            UpdateObserver observer = new(target.Binding, target.ClusterId);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
            await observer.FindAsync(target.PeerId, target.Peer!.InstanceId, cancellationToken).ConfigureAwait(false);
            submitted = true;
            JsonElement response = await SendCoreAsync(observer, target, "archive.import", new ArchiveImportRequest(preview.OperationId, preview.Confirmation), false, cancellationToken).ConfigureAwait(false);
            ArchiveOperationResult result = Read(response, preview.OperationId);
            while (!result.Completed)
            {
                progress.Report(new("working", 0, 1, target.ClusterId));
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                response = await SendCoreAsync(observer, target, "archive.operation", preview.OperationId, true, cancellationToken).ConfigureAwait(false);
                result = Read(response, preview.OperationId);
            }
            StringBuilder details = new(identity + Environment.NewLine + result.Stage + " · " + result.Reason);
            foreach (ArchiveStepResult step in result.Steps ?? [])
            {
                details.AppendLine(Environment.NewLine + step.Target + " · " + step.Outcome + " · " + step.Reason);
            }
            progress.Report(new("outcome-" + result.Outcome, 1, 1, target.ClusterId));
            return new(WizardExecution.Outcome(state, result.Outcome), details.ToString());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (submitted)
            {
                outcome = EOutcome.Unknown;
            }
            return new(WizardExecution.Outcome(state, outcome), identity + Environment.NewLine + exception.Message);
        }
    }

    /// <summary>開始時の実体と操作名を保持する一回の管理要求です。</summary>
    /// <returns>業務結果のJSONです。</returns>
    private static async Task<JsonElement> SendCoreAsync(UpdateObserver observer, ObservedTarget target, string action, object body, bool readOnly,
        CancellationToken cancellationToken = default)
    {
        ManagementReply reply = await observer.SendAsync(target.PeerId, target.Peer!.InstanceId, action, UpdateObserver.Payload(body), readOnly, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            throw new IOException("Archive request was not confirmed: " + reply.Reason);
        }
        return reply.Value.Json;
    }

    /// <summary>別の操作結果を完了として表示しません。</summary>
    /// <returns>元操作の実結果です。</returns>
    private static ArchiveOperationResult Read(JsonElement value, Guid operation)
    {
        ArchiveOperationResult result = value.Deserialize<ArchiveOperationResult>(ContractJson.Options) ?? throw new InvalidDataException("Archive result is missing.");
        if (result.OperationId != operation)
        {
            throw new InvalidDataException("Archive result belongs to another operation.");
        }
        return result;
    }
}
