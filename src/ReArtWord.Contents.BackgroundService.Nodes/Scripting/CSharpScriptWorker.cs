using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>Coreの通常起動前に分岐する、ユーザーコード専用の子プロセス入口です。</summary>
public static class CSharpScriptWorker
{
    /// <summary>単発値と継続値で共通の辞書キー変換を使用します。</summary>
    internal static JsonSerializerOptions ValueOptions
    {
        get;
    } = new() { Converters = { new CSharpBooleanKeys() } };

    /// <summary>ユーザー標準出力を分け、例外診断は親の共通ログ保護境界へ渡します。</summary>
    /// <returns>一回のコード実行と結果出力です。</returns>
    // 抑制理由: ユーザーコードを実行する子プロセスの障害を、容量を制限した診断へ変換します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "自由記述コードの失敗を結果化し、親の秘密値保護を通して記録します。")]
    public static async Task RunAsync(string requestPath, CancellationToken cancellationToken = default)
    {
        TextWriter protocol = Console.Out;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        int maximum = 1048576;
        try
        {
            if (16777216 < new FileInfo(requestPath).Length)
            {
                throw new InvalidDataException("script.inputLimit");
            }
            CSharpWorkerRequest request = JsonSerializer.Deserialize<CSharpWorkerRequest>(await File.ReadAllTextAsync(requestPath, cancellationToken).ConfigureAwait(false), ContractJson.Options)
                ?? throw new InvalidDataException("script.requestInvalid");
            maximum = Math.Clamp(request.MaximumDiagnosticBytes, 1024, 16777216);
            if (request.SourcePipe is not null)
            {
                await CSharpSourceWorker.RunAsync(request, cancellationToken).ConfigureAwait(false);
                return;
            }
            object? value = CSharpScriptInvocation.Invoke(request, cancellationToken);
            if (value is Task pending)
            {
                await pending.ConfigureAwait(false);
                value = pending.GetType().GetProperty("Result")!.GetValue(pending);
            }
            string valueKind = value is byte[] ? "binary"
                                               : "json";
            string result = JsonSerializer.Serialize(new { completed = true, value, valueKind }, ValueOptions);
            await protocol.WriteAsync(result.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            string failed = JsonSerializer.Serialize(new { completed = false, error = "script.executionFailed", exception = ExceptionDiagnostic.Capture(error) }, ContractJson.Options);
            if (maximum < System.Text.Encoding.UTF8.GetByteCount(failed))
            {
                failed = JsonSerializer.Serialize(new { completed = false, error = "script.executionFailed", exception = new ExceptionDiagnostic("ScriptError", "[diagnostic omitted: output limit]", null, 0, [], true) }, ContractJson.Options);
            }
            await protocol.WriteAsync(failed.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }
}
