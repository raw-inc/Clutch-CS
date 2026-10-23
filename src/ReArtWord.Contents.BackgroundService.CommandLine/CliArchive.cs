using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Clients;

namespace ReArtWord.Contents.BackgroundService.CommandLine;

/// <summary>アーカイブの書き出し・取り込み用CLIコマンド。</summary>
internal static class CliArchive
{
    /// <summary>手元のClientHostを通して、指定したアーカイブ操作を実行します。</summary>
    /// <returns>CLIの終了コード。</returns>
    internal static async Task<int> RunAsync(CliCommand command, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        bool mutationSent = false;
        Guid id = Guid.Empty;
        try
        {
            string idOption = "--id", operationOption = "--operation-id";
            id = Guid.NewGuid();
            if (command.Options.TryGetValue(idOption, out string? specified) || command.Options.TryGetValue(operationOption, out specified))
            {
                id = Guid.Parse(specified);
            }
            ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
            await error.WriteLineAsync("operationId=" + id.ToString("D")).ConfigureAwait(false);
            using SocketsHttpHandler handler = new() { AllowAutoRedirect = false, UseProxy = false };
            using HttpClient client = new(handler) { BaseAddress = command.BaseAddress, Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 2097152 };
            await CommandLineApplication.VerifyClientHostAsync(client, cancellationToken).ConfigureAwait(false);
            client.DefaultRequestHeaders.Add("X-Contents-Cluster", command.Options["--cluster"]);
            client.DefaultRequestHeaders.Add("X-Contents-Peer", command.Options["--peer"]);
            string display = "--display-name";
            if (command.Options.TryGetValue(display, out string? name))
            {
                client.DefaultRequestHeaders.Add("X-Contents-Display-Name", name);
            }
            async Task<JsonElement> SendAsync(string operation, object body, CancellationToken token)
            {
                string path = operation switch
                {
                    "archive.upload.begin" => "/api/archive/upload/begin",
                    "archive.upload.chunk" => "/api/archive/upload/chunk",
                    "archive.read" => "/api/archive/read",
                    "archive.operation" => "/api/archive/operations/" + id.ToString("D"),
                    "archive.reconcile" => "/api/archive/operations/" + id.ToString("D") + "/reconcile",
                    _ => "/api/" + operation.Replace('.', '/'),
                };
                bool inspect = operation is "archive.operation";
                string importOperation = "archive.import", reconcileOperation = "archive.reconcile";
                if (string.Equals(operation, importOperation, StringComparison.Ordinal) || string.Equals(operation, reconcileOperation, StringComparison.Ordinal))
                {
                    mutationSent = true;
                }
                HttpMethod method = inspect ? HttpMethod.Get
                                            : HttpMethod.Post;
                using HttpRequestMessage request = new(method, path);
                if (!inspect)
                {
                    request.Headers.Add("X-Contents-Request", Guid.NewGuid().ToString("D"));
                    request.Content = JsonContent.Create(body, options: ContractJson.Options);
                }
                using HttpResponseMessage response = await client.SendAsync(request, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                JsonElement value = await response.Content.ReadFromJsonAsync<JsonElement>(ContractJson.Options, token).ConfigureAwait(false);
                return value;
            }
            string exportCommand = "archive export", previewCommand = "archive preview", importCommand = "archive import", reconcileCommand = "archive reconcile";
            if (string.Equals(command.Name, exportCommand, StringComparison.Ordinal))
            {
                string destination = Path.GetFullPath(command.Options["--file"]);
                if (File.Exists(destination))
                {
                    throw new IOException("The destination already exists.");
                }
                string logsOption = "--logs";
                bool logs = command.Options.TryGetValue(logsOption, out string? selected) && bool.Parse(selected);
                JsonElement response = await SendAsync("archive.export", new ArchiveExportRequest(id, logs), cancellationToken).ConfigureAwait(false);
                ArchiveOperationResult result = ReadResult(response, id);
                while (!result.Completed)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    result = ReadResult(await SendAsync("archive.operation", id, cancellationToken).ConfigureAwait(false), id);
                }
                if (result.Outcome != EOutcome.Succeeded || result.File is null)
                {
                    int unavailable = Code(result.Outcome);
                    if (result.File is null && unavailable == 0)
                    {
                        unavailable = 4;
                    }
                    return await PrintAsync(output, id, response: result, unavailable, cancellationToken).ConfigureAwait(false);
                }
                string temporary = destination + ".partial-" + id.ToString("N");
                try
                {
                    using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    {
                        await ArchiveFileTransfer.DownloadAsync(file, result.File, SendAsync, cancellationToken).ConfigureAwait(false);
                    }
                    File.Move(temporary, destination);
                }
                finally
                {
                    File.Delete(temporary);
                }
                return await PrintAsync(output, id, new { file = destination, result.File.Length, result.File.Sha256 }, 0, cancellationToken).ConfigureAwait(false);
            }
            if (string.Equals(command.Name, previewCommand, StringComparison.Ordinal))
            {
                string modeOption = "--mode";
                EArchiveImportMode mode = EArchiveImportMode.Merge;
                if (command.Options.TryGetValue(modeOption, out string? selected))
                {
                    mode = Enum.Parse<EArchiveImportMode>(selected, true);
                }
                if (!Enum.IsDefined(mode))
                {
                    throw new ArgumentOutOfRangeException(nameof(command));
                }
                using FileStream file = File.OpenRead(command.Options["--file"]);
                await ArchiveFileTransfer.UploadAsync(file, id, SendAsync, cancellationToken).ConfigureAwait(false);
                JsonElement response = await SendAsync("archive.preview", new ArchivePreviewRequest(id, mode), cancellationToken).ConfigureAwait(false);
                ArchiveImportPreview preview = response.Deserialize<ArchiveImportPreview>(ContractJson.Options) ?? throw new InvalidDataException("Archive preview is missing.");
                if (preview.OperationId != id)
                {
                    throw new InvalidDataException("Archive preview belongs to another operation.");
                }
                return await PrintAsync(output, id, preview, 0, cancellationToken).ConfigureAwait(false);
            }
            object payload = id;
            string action = "archive.operation";
            if (string.Equals(command.Name, importCommand, StringComparison.Ordinal))
            {
                payload = new ArchiveImportRequest(id, command.Options["--confirmation"]);
                action = "archive.import";
            }
            if (string.Equals(command.Name, reconcileCommand, StringComparison.Ordinal))
            {
                action = "archive.reconcile";
            }
            ArchiveOperationResult status = ReadResult(await SendAsync(action, payload, cancellationToken).ConfigureAwait(false), id);
            int code = 5;
            if (status.Completed)
            {
                code = Code(status.Outcome);
            }
            return await PrintAsync(output, id, status, code, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or KeyNotFoundException or FormatException or JsonException or HttpRequestException or OperationCanceledException)
        {
            int code = mutationSent ? 5
                                    : 2;
            if (!mutationSent && exception is HttpRequestException)
            {
                code = 3;
            }
            if (!mutationSent && exception is OperationCanceledException)
            {
                code = 130;
            }
            await error.WriteLineAsync("Archive operation could not be confirmed: " + exception.GetType().Name + "; operationId=" + id.ToString("D") + ". No automatic retry.").ConfigureAwait(false);
            return code;
        }
    }

    /// <summary>応答の操作IDが、照会した操作に一致するか確認します。</summary>
    /// <returns>IDを確認した操作結果。</returns>
    private static ArchiveOperationResult ReadResult(JsonElement value, Guid id)
    {
        ArchiveOperationResult result = value.Deserialize<ArchiveOperationResult>(ContractJson.Options) ?? throw new InvalidDataException("Archive result is missing.");
        if (result.OperationId != id)
        {
            throw new InvalidDataException("Archive result belongs to another operation.");
        }
        return result;
    }

    /// <summary>操作結果をCLIの終了コードへ変換します。</summary>
    /// <returns>成功は0、結果不明は5、それ以外は4。</returns>
    private static int Code(EOutcome outcome) => outcome switch { EOutcome.Succeeded => 0, EOutcome.Unknown => 5, _ => 4 };

    /// <summary>操作IDと応答をJSONで標準出力に書き込みます。</summary>
    /// <returns>引数で指定した終了コード。</returns>
    private static async Task<int> PrintAsync(TextWriter output, Guid operationId, object response, int code, CancellationToken cancellationToken = default)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new { operationId, exitCode = code, response }, ContractJson.Options).AsMemory(), cancellationToken).ConfigureAwait(false);
        return code;
    }
}
