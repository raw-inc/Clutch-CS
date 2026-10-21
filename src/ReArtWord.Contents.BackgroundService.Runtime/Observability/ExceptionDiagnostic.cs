namespace ReArtWord.Contents.BackgroundService.Runtime.Observability;

/// <summary>例外オブジェクトや入力値を保持せず、障害調査に必要な情報だけをログへ渡します。</summary>
/// <param name="Type">例外型。</param>
/// <param name="Message">例外の説明。秘密値は共通ログ境界で伏せます。</param>
/// <param name="StackTrace">例外が発生した呼出し履歴。</param>
/// <param name="HResult">OSや実装が設定したエラー番号。</param>
/// <param name="InnerExceptions">内部・集約例外の限定した一覧。</param>
/// <param name="Truncated">件数・文字数の上限で省略が発生したか。</param>
public sealed record ExceptionDiagnostic(string Type, string? Message, string? StackTrace, int HResult, IReadOnlyList<ExceptionDiagnostic> InnerExceptions, bool Truncated)
{
    /// <summary>通常実行には割り当てず、例外発生時だけ最大8件・合計32Ki文字の診断を生成します。</summary>
    /// <returns>循環する追加データや入力・出力を含まない診断です。</returns>
    // 抑制理由: 拡張例外のMessage/StackTrace取得自体が失敗しても、元の実行結果を変えません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "障害診断の生成失敗を型名と省略表示へ限定します。")]
    public static ExceptionDiagnostic Capture(Exception error, bool includeMessage = true)
    {
        ArgumentNullException.ThrowIfNull(error);
        int remaining = 8;
        int characters = 32768;
        try
        {
            return Capture(error, includeMessage, ref remaining, ref characters);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return new(error.GetType().FullName ?? error.GetType().Name, "[diagnostic unavailable]", null, error.HResult, [], true);
        }
    }

    /// <summary>長すぎる説明は全体を省略し、切断された秘密値の一部をログへ残しません。</summary>
    /// <returns>容量上限内の説明です。</returns>
    public static string? MessageWithinLimit(string? message, int limit = 2048)
    {
        if (message is not null && limit < message.Length)
        {
            return "[message omitted: length limit]";
        }
        return message;
    }

    /// <summary>子プロセス由来の診断にも同じ件数・容量上限を適用します。</summary>
    /// <returns>ログへ渡せる容量へ制限した診断です。</returns>
    public ExceptionDiagnostic Bound()
    {
        int remaining = 8;
        int characters = 32768;
        return Bound(ref remaining, ref characters);
    }

    /// <summary>診断の来歴によらず自由文と内部例外を有限容量へ収めます。</summary>
    /// <returns>今回の診断と限定した内部診断です。</returns>
    private ExceptionDiagnostic Bound(ref int remaining, ref int characters)
    {
        remaining--;
        bool truncated = Truncated;
        string type = Text(Type, 256, ref characters, ref truncated) ?? "unknown";
        string? message = Text(Message, 2048, ref characters, ref truncated);
        string? stack = Text(StackTrace, 16384, ref characters, ref truncated);
        List<ExceptionDiagnostic> children = [];
        foreach (ExceptionDiagnostic item in InnerExceptions ?? [])
        {
            if (remaining == 0 || characters < 512)
            {
                truncated = true;
                break;
            }
            ExceptionDiagnostic child = item.Bound(ref remaining, ref characters);
            truncated |= child.Truncated;
            children.Add(child);
        }
        return new(type, message, stack, HResult, children, truncated);
    }

    /// <summary>親から共有した件数・容量内で内部例外を辿ります。</summary>
    /// <returns>今回の例外と残容量に収まる内部例外です。</returns>
    private static ExceptionDiagnostic Capture(Exception error, bool includeMessage, ref int remaining, ref int characters)
    {
        remaining--;
        bool truncated = false;
        string type = Text(error.GetType().FullName ?? error.GetType().Name, 256, ref characters, ref truncated)!;
        string? message = null;
        if (includeMessage)
        {
            message = Text(error.Message, 2048, ref characters, ref truncated);
        }
        string? stack = Text(error.StackTrace, 16384, ref characters, ref truncated);
        List<ExceptionDiagnostic> children = [];
        IReadOnlyList<Exception> inner = [];
        if (error is AggregateException aggregate)
        {
            inner = aggregate.InnerExceptions;
        }
        else
        {
            if (error.InnerException is { } child)
            {
                inner = [child];
            }
        }
        foreach (Exception item in inner)
        {
            if (remaining == 0 || characters < 512)
            {
                truncated = true;
                break;
            }
            ExceptionDiagnostic diagnostic = Capture(item, includeMessage, ref remaining, ref characters);
            truncated |= diagnostic.Truncated;
            children.Add(diagnostic);
        }
        return new(type, message, stack, error.HResult, children, truncated);
    }

    /// <summary>上限超過は明示し、自由文の一部だけを切り取らないようにします。</summary>
    /// <returns>取得できた文字列または省略表示です。</returns>
    private static string? Text(string? value, int limit, ref int remaining, ref bool truncated)
    {
        if (value is null)
        {
            return null;
        }
        if (Math.Min(limit, remaining) < value.Length)
        {
            truncated = true;
            value = "[omitted: length limit]";
        }
        remaining -= value.Length;
        return value;
    }
}
