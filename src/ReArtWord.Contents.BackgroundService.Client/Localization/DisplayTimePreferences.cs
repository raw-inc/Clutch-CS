using System.Text.Json;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Services;

namespace ReArtWord.Contents.BackgroundService.Client.Localization;

/// <summary>ブラウザーごとの表示用タイムゾーンを保存し、稼働側の日時設定から分離します。</summary>
internal sealed class DisplayTimePreferences(BrowserStorage storage)
{
    private static string StorageKey => "contents.ui.time-zone";
    private bool initialized;

    internal event Action? Changed;

    internal TimeZoneInfo Zone
    {
        get; private set;
    } = TimeZoneInfo.Utc;

    internal string? Problem
    {
        get; private set;
    }

    /// <summary>保存した表示設定を復元し、不正な値では既定のUTCを使用します。</summary>
    /// <returns>保存設定の読取りです。</returns>
    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }
        initialized = true;
        try
        {
            string? saved = await storage.ReadAsync<string>(StorageKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (saved is not null)
            {
                Zone = TimeZoneInfo.FindSystemTimeZoneById(saved);
            }
        }
        catch (Exception exception) when (exception is JSException or JsonException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            Problem = "display.timezone.readFailed";
        }
    }

    /// <summary>この画面へ反映して次回起動用に保存します。保存不能時も表示変更は維持します。</summary>
    /// <returns>設定の保存確認です。</returns>
    internal async Task ChangeAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? next))
        {
            throw UiError.InvalidOperation("display.timezone.invalid");
        }
        Zone = next;
        Problem = null;
        Changed?.Invoke();
        try
        {
            await storage.WriteAsync(StorageKey, next.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JSException)
        {
            Problem = "display.timezone.writeFailed";
            Changed?.Invoke();
        }
    }
}
