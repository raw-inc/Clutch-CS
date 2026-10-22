using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>輸送方式に依存せず、登録した管理操作と参照専用の判定を一か所で管理します。</summary>
internal sealed class ManagementRegistry
{
    private readonly Dictionary<string, Func<NodeValue?, CancellationToken, Task<object?>>> handlers = new(StringComparer.Ordinal);
    private readonly HashSet<string> inspections = new(StringComparer.Ordinal);
    private readonly HashSet<string> transient = new(StringComparer.Ordinal);

    /// <summary>操作開始前にローカル運転方針を確認します。読取りや開始済み処理の回復は呼出側で区別します。</summary>
    public Func<string, string?>? AdmissionCheck
    {
        get; set;
    }

    /// <summary>永続受付を増やさない参照操作を登録します。</summary>
    public void Read(string name, Func<NodeValue?, CancellationToken, Task<object?>> handler)
    {
        handlers.Add(name, handler);
        inspections.Add(name);
    }

    /// <summary>同期で完了する参照操作を登録します。</summary>
    public void ReadValue(string name, Func<NodeValue?, object?> handler) => Read(name, (value, _) => Task.FromResult(handler(value)));

    /// <summary>元要求IDの永続受付を経由する管理操作を登録します。</summary>
    public void Write(string name, Func<NodeValue?, CancellationToken, Task<object?>> handler) => handlers.Add(name, handler);

    /// <summary>下書きや編集者情報の短命な状態に限り、永続受付を作らず通知します。</summary>
    public void Transient(string name, Func<NodeValue?, CancellationToken, Task<object?>> handler)
    {
        handlers.Add(name, handler);
        transient.Add(name);
    }

    /// <summary>一時通知として明示登録された操作を確認します。</summary>
    /// <returns>一時通知に限ってtrueです。</returns>
    public bool IsTransient(string operation) => transient.Contains(operation);

    /// <summary>クライアントの申告によらず参照専用操作かを確認します。</summary>
    /// <returns>登録された参照操作だけがtrueです。</returns>
    public bool IsReadOnly(string operation) => inspections.Contains(operation);

    /// <summary>明示した操作だけを呼び出し、応答のJSON表現を共通契約へ統一します。</summary>
    /// <returns>管理処理の応答です。ドメイン内の段階結果はValueに保持します。</returns>
    public async Task<ManagementReply> ExecuteAsync(ManagementCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!handlers.TryGetValue(command.Operation, out Func<NodeValue?, CancellationToken, Task<object?>>? handler))
        {
            return new(EOutcome.NotExecuted, Reason: "Management.OperationMissing");
        }
        if (AdmissionCheck?.Invoke(command.Operation) is string problem)
        {
            return new(EOutcome.NotExecuted, Reason: problem);
        }
        try
        {
            object? result = await handler(command.Payload, cancellationToken).ConfigureAwait(false);
            return SerializeResult(result);
        }
        catch (ManagementInputException)
        {
            return new(EOutcome.NotExecuted, Reason: "Management.InvalidRequest");
        }
        catch (KeyNotFoundException) when (IsReadOnly(command.Operation))
        {
            return new(EOutcome.NotExecuted, Reason: "Management.ResourceMissing");
        }
        catch (UnauthorizedAccessException)
        {
            EOutcome outcome = EOutcome.Unknown;
            if (IsReadOnly(command.Operation))
            {
                outcome = EOutcome.NotExecuted;
            }
            return new(outcome, Reason: "Management.AccessDenied");
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException or IOException or HttpRequestException)
        {
            return new(EOutcome.Unknown, Reason: "Management.ResultUnconfirmed");
        }
    }

    /// <summary>入力を公開契約の規則で読み、欠損や不明な項目を拒否します。</summary>
    /// <returns>型と所有権を確認した入力です。</returns>
    // 抑制理由: 入力parserの例外だけを副作用前の内部分類へ変換し、実行後の例外と区別します。
    [SuppressMessage("SourceQuality", "RAW333", Justification = "JSON入力境界で例外を未開始分類へ翻訳し、元の例外本文は管理応答へ出しません。")]
    public static T Body<T>(NodeValue? value)
    {
        if (value?.Kind != EValueKind.Json)
        {
            throw new ManagementInputException();
        }
        try
        {
            return value.Json.Deserialize<T>(ContractJson.Options) ?? throw new ManagementInputException();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new ManagementInputException("管理引数の形式を確認してください。", exception);
        }
    }

    /// <summary>識別子だけを取る参照操作の引数を確認します。</summary>
    /// <returns>空でない識別子です。</returns>
    public static string Id(NodeValue? value)
    {
        string id = Body<string>(value);
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ManagementInputException();
        }
        return id;
    }

    /// <summary>元の操作IDを参照・回復に使用します。</summary>
    /// <returns>入力で明示された操作IDです。</returns>
    public static Guid OperationId(NodeValue? value)
    {
        if (!Guid.TryParse(Id(value), out Guid id))
        {
            throw new ManagementInputException();
        }
        return id;
    }

    /// <summary>handler完了後の変換不能を未実行へ読み替えません。</summary>
    /// <returns>小さい応答、または明示した結果確認不能です。</returns>
    private static ManagementReply SerializeResult(object? result)
    {
        using ManagementResponseBuffer buffer = new();
        try
        {
            JsonSerializer.Serialize(buffer, result, ContractJson.Options);
            using JsonDocument document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
            return new(EOutcome.Succeeded, NodeValue.FromJson(document.RootElement));
        }
        catch (IOException) when (buffer.LimitExceeded)
        {
            return new(EOutcome.Unknown, Reason: "Management.ResponseTooLarge");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or NotSupportedException)
        {
            return new(EOutcome.Unknown, Reason: "Management.ResponseSerializationFailed");
        }
    }
}
