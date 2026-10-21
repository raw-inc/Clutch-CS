using System.Globalization;
using ReArtWord.Contents.BackgroundService.Client.Localization;

namespace ReArtWord.Contents.BackgroundService.Client.Services;

/// <summary>未確定の入力文字列を送信直前に検証します。</summary>
internal static class FormInput
{
    private static readonly string[] localTimeFormats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

    /// <summary>日時入力の空欄を区別し、表示用タイムゾーンで一意な日時へ変換します。</summary>
    /// <returns>指定がなければnull、指定時はその日時のoffsetを持つ値です。</returns>
    // 抑制理由: offset未確定の壁時計入力をTimeZoneInfoで検証する境界に限定してDateTimeを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW171", Justification = "datetime-localの壁時計時刻はoffsetを持たないため、TimeZoneInfoで夏時間とoffsetを確定するまではUnspecifiedのDateTimeが必要です。")]
    internal static DateTimeOffset? ZonedTime(string? text, string field, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (!DateTime.TryParseExact(text, localTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime value))
        {
            throw UiError.InvalidOperation("Enter a valid local date and time for {0}.", field);
        }
        if (zone.IsInvalidTime(value) || zone.IsAmbiguousTime(value))
        {
            throw UiError.InvalidOperation("display.timezone.ambiguous", field, zone.Id);
        }
        return new DateTimeOffset(value, zone.GetUtcOffset(value));
    }

    /// <summary>入力不正を古い数値へ戻さず、項目名を添えて拒否します。</summary>
    /// <returns>範囲内の確定値です。</returns>
    internal static int Integer(string text, string field, int minimum = 0, int maximum = int.MaxValue)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < minimum || maximum < value)
        {
            throw UiError.InvalidOperation("Enter an integer from {1} to {2} for {0}.", field, minimum, maximum);
        }
        return value;
    }
}
