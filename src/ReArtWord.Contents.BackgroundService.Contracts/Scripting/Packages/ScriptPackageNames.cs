using System.Text.RegularExpressions;

namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

/// <summary>検索・表示・導入で同じ配布名の規則を使用します。</summary>
public static class ScriptPackageNames
{
    /// <summary>配布名をURLやCLIの追加指定として解釈させません。</summary>
    /// <returns>この製品で導入可能な配布名かです。</returns>
    public static bool Valid(string? id, EScriptPackageKind kind)
    {
        string pattern = "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z";
        if (kind == EScriptPackageKind.Npm)
        {
            pattern = "\\A(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*\\z";
        }
        return Enum.IsDefined(kind) && id is { Length: > 0 and <= 214 } && Regex.IsMatch(id, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>NuGetの大文字小文字とPython配布名の区切りを統一します。</summary>
    /// <returns>同じパッケージを照合するための名前です。</returns>
    // 抑制理由: 公開配布規格がASCII小文字の正規化名を使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308", Justification = "配布規格の小文字正規化です。")]
    public static string Normalize(string id, EScriptPackageKind kind)
    {
        ArgumentNullException.ThrowIfNull(id);
        string value = id.ToLowerInvariant();
        if (kind == EScriptPackageKind.Pip)
        {
            value = Regex.Replace(value, "[-_.]+", "-", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        return value;
    }
}
