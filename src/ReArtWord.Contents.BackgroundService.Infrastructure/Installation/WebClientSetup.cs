using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>Webクライアントだけを選択した権限で導入し、結果を保存します。</summary>
public sealed class WebClientSetup(IInstallationPlatform platform)
{
    /// <summary>同じ操作を暗黙に再実行せず、完了したクライアント情報を返します。</summary>
    /// <returns>導入・起動確認の結果です。</returns>
    public async Task<SetupResult> ExecuteAsync(WebClientSetupJob job, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        WebClientSetupJob owned = ContractJson.Copy(job);
        string journalRoot = InstallationFiles.Root(directory);
        Directory.CreateDirectory(journalRoot);
        SetupOperationJournal journal = new(owned.OperationId, journalRoot, owned.Settings.Root);
        SetupResult? previous = await journal.ExistingAsync(owned, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            return previous;
        }
        SetupProgressJournal progress = SetupProgressJournal.ForWebClient(journalRoot);
        try
        {
            await journal.BeginAsync(owned, cancellationToken).ConfigureAwait(false);
            await progress.BeginAsync("validate", cancellationToken).ConfigureAwait(false);
            ArgumentOutOfRangeException.ThrowIfEqual(owned.OperationId, Guid.Empty);
            if (!owned.Settings.LicenseAccepted || owned.Settings.WebClient is not { } choice || (!choice.Install && choice.ExistingRoot is null))
            {
                throw new InvalidDataException("Select a web client and confirm its license before installation.");
            }
            InstallationSettings settings = owned.Settings with { WebClient = choice with { Install = true } };
            InstallationRecord owner = new(owned.OperationId, settings, string.Empty, string.Empty, string.Empty, string.Empty, DateTimeOffset.UtcNow);
            await progress.BeginAsync("web-client", cancellationToken).ConfigureAwait(false);
            WebClientInstallation client = await new WebClientInstaller(platform).InstallAsync(owner, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The web client was not installed.");
            await progress.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return await journal.CompleteAsync(new(owned.OperationId, EOutcome.Succeeded, "installed", "The web client was installed.", client.Root, WebClient: client), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            EOutcome outcome = EOutcome.Failed;
            if (exception is OperationCanceledException)
            {
                outcome = EOutcome.Unknown;
            }
            return await journal.CompleteAsync(new(owned.OperationId, outcome, progress.Current.Stage, exception.Message, owned.Settings.Root), CancellationToken.None).ConfigureAwait(false);
        }
    }
}
