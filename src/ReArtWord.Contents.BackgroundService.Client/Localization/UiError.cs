using System.Globalization;

namespace ReArtWord.Contents.BackgroundService.Client.Localization;

/// <summary>例外の種類を変えず、表示テンプレートと原文引数を保持します。</summary>
internal sealed class UiError
{
    /// <summary>既存例外へ所有コピーの表示情報を添えます。</summary>
    private UiError(string template, object?[] arguments)
    {
        Template = template;
        Arguments = Array.AsReadOnly(arguments.ToArray());
    }

    /// <summary>既定英語の表示書式です。</summary>
    internal string Template
    {
        get;
    }

    /// <summary>利用者文字列を含む、翻訳しない固定引数です。</summary>
    internal IReadOnlyList<object?> Arguments
    {
        get;
    }

    /// <summary>元の例外型・StatusCodeを保ったまま表示情報を添付します。</summary>
    /// <returns>渡された例外そのものです。</returns>
    internal static T Attach<T>(T exception, string template, params object?[] arguments) where T : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentNullException.ThrowIfNull(arguments);
        exception.Data[typeof(UiError)] = new UiError(template, arguments);
        return exception;
    }

    /// <summary>推測翻訳せず明示添付された情報だけを取得します。</summary>
    /// <returns>未添付ならnullです。</returns>
    internal static UiError? From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Data[typeof(UiError)] as UiError;
    }

    /// <summary>既存のInvalidOperationExceptionで入力検証の意味を維持します。</summary>
    /// <returns>英語診断と表示情報を持つ同じ例外型です。</returns>
    internal static InvalidOperationException InvalidOperation(string template, params object?[] arguments) => Attach(new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, template, arguments)), template, arguments);
}
