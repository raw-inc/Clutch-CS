using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>画面終了の取消しとローカル日時入力を、外から観測できる挙動で確認します。</summary>
public sealed class ComponentLifetimeTests
{
    /// <summary>画面が終了すると開始済みI/Oを取り消し、再度の破棄も安全に完了します。</summary>
    /// <returns>取消しと破棄の確認です。</returns>
    [Fact]
    public async Task ComponentDisposalCancelsItsIoAndRemainsIdempotent()
    {
        Probe component = new();
        CancellationToken token = component.Token;
        Assert.True(token.CanBeCanceled);
        Task pending = Task.Delay(Timeout.InfiniteTimeSpan, token);
        await component.DisposeAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending).ConfigureAwait(true);
        Assert.True(component.Token.IsCancellationRequested);
        await component.DisposeAsync().ConfigureAwait(true);
        component.Notify();
    }

    /// <summary>I/Oを開始しなかった画面も、破棄後に未取消の処理を開始できません。</summary>
    /// <returns>遅い通知が停止状態を維持する確認です。</returns>
    [Fact]
    public async Task DisposalBeforeFirstIoKeepsFutureTokensCancelled()
    {
        Probe component = new();
        await component.DisposeAsync().ConfigureAwait(true);
        Assert.True(component.Token.IsCancellationRequested);
        component.Notify();
    }

    /// <summary>日時入力を文化依存の数値解釈にせず、選択したUTCのoffsetへ変換します。</summary>
    [Theory]
    [InlineData("2026-09-15T10:20")]
    [InlineData("2026-09-15T10:20:00")]
    [InlineData("2026-09-15T10:20:00.123")]
    public void DateTimeInputUsesSelectedUtcZone(string text)
    {
        DateTimeOffset value = Assert.IsType<DateTimeOffset>(FormInput.ZonedTime(text, "field", TimeZoneInfo.Utc));
        Assert.Equal(2026, value.Year);
        Assert.Equal(9, value.Month);
        Assert.Equal(15, value.Day);
        Assert.Equal(10, value.Hour);
        Assert.Equal(20, value.Minute);
        Assert.Equal(TimeSpan.Zero, value.Offset);
    }

    /// <summary>未指定と不正な日時を区別し、古い値へ戻しません。</summary>
    [Fact]
    public void EmptyAndInvalidLocalTimesRemainDistinct()
    {
        Assert.Null(FormInput.ZonedTime(null, "field", TimeZoneInfo.Utc));
        Assert.Null(FormInput.ZonedTime(string.Empty, "field", TimeZoneInfo.Utc));
        Assert.Throws<InvalidOperationException>(() => FormInput.ZonedTime("2026-20-40T10:20", "field", TimeZoneInfo.Utc));
        Assert.Throws<InvalidOperationException>(() => FormInput.ZonedTime("not a date", "field", TimeZoneInfo.Utc));
    }

    /// <summary>基底が子画面へ公開する存続期間だけを観測します。</summary>
    private sealed class Probe : LocalizedComponentBase
    {
        /// <summary>画面のI/Oへ渡すトークンです。</summary>
        public CancellationToken Token => UiCancellation;

        /// <summary>モデルの変更通知を模擬します。</summary>
        public void Notify() => RequestRenderFromEvent();
    }
}
