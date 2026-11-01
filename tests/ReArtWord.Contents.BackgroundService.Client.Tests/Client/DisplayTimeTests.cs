using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>表示設定の保存と、日時を検索条件へ戻す際の境界を検証します。</summary>
public sealed class DisplayTimeTests
{
    /// <summary>新規ブラウザーはUTCを使い、選択した設定だけを次回へ引き継ぎます。</summary>
    /// <returns>保存と再読取りの確認です。</returns>
    [Fact]
    public async Task DefaultAndSavedZoneAreIndependentOfMachineZoneAsync()
    {
        BrowserTestRuntime script = new();
        DisplayTimePreferences preferences = new(new(script));
        await preferences.InitializeAsync().ConfigureAwait(true);
        Assert.Equal("UTC", preferences.Zone.Id);
        int changes = 0;
        preferences.Changed += () => changes++;
        await preferences.ChangeAsync("Asia/Tokyo").ConfigureAwait(true);
        Assert.Equal(1, changes);
        Assert.Single(script.Local);
        DisplayTimePreferences restored = new(new(script));
        await restored.InitializeAsync().ConfigureAwait(true);
        Assert.Equal("Asia/Tokyo", restored.Zone.Id);
        Assert.Null(restored.Problem);
        await Assert.ThrowsAsync<InvalidOperationException>(() => preferences.ChangeAsync("missing/zone")).ConfigureAwait(true);
        Assert.Equal("Asia/Tokyo", preferences.Zone.Id);
        Assert.Equal(1, changes);
    }

    /// <summary>壊れた保存値はUTCへ戻し、保存不能でも現在の画面の選択は維持します。</summary>
    /// <returns>読取りと書込みの障害表示です。</returns>
    [Theory]
    [InlineData("\"missing/zone\"")]
    [InlineData("{broken")]
    public async Task InvalidStorageFallsBackAndWriteFailureRemainsVisibleAsync(string saved)
    {
        BrowserTestRuntime script = new();
        script.Local["contents.ui.time-zone"] = saved;
        DisplayTimePreferences preferences = new(new(script));
        await preferences.InitializeAsync().ConfigureAwait(true);
        Assert.Equal("UTC", preferences.Zone.Id);
        Assert.Equal("display.timezone.readFailed", preferences.Problem);
        script.FailWrites = true;
        await preferences.ChangeAsync("Asia/Tokyo").ConfigureAwait(true);
        Assert.Equal("Asia/Tokyo", preferences.Zone.Id);
        Assert.Equal("display.timezone.writeFailed", preferences.Problem);
        Assert.Equal(saved, script.Local["contents.ui.time-zone"]);
    }

    /// <summary>夏時間と半時間・四半時間offsetを、その入力日時に対応する瞬間へ変換します。</summary>
    [Theory]
    [InlineData("Asia/Tokyo", "2026-10-04T00:00:00.123", "2026-10-03T15:00:00.123Z")]
    [InlineData("Asia/Kathmandu", "2026-10-04T00:00", "2026-10-03T18:15:00Z")]
    [InlineData("America/New_York", "2026-07-01T08:00", "2026-07-01T12:00:00Z")]
    [InlineData("America/New_York", "2026-01-01T08:00", "2026-01-01T13:00:00Z")]
    [InlineData("Australia/Adelaide", "2026-07-01T08:00", "2026-06-30T22:30:00Z")]
    public void InputUsesOffsetAtTheEnteredDate(string id, string input, string expected)
    {
        DateTimeOffset? value = FormInput.ZonedTime(input, "From", TimeZoneInfo.FindSystemTimeZoneById(id));
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    /// <summary>夏時間で欠落・重複する壁時計時刻を勝手に一方へ確定しません。</summary>
    [Theory]
    [InlineData("2026-03-08T02:30")]
    [InlineData("2026-11-01T01:30")]
    public void DstTransitionRequiresAnUnambiguousTime(string text)
        => Assert.Throws<InvalidOperationException>(() => FormInput.ZonedTime(text, "From", TimeZoneInfo.FindSystemTimeZoneById("America/New_York")));

    /// <summary>同じコンポーネントへcascade変更を届け、日付とoffsetが同時に変わることを確認します。</summary>
    /// <returns>実Blazorによる表示更新の確認です。</returns>
    [Fact]
    public async Task CascadeChangesTimeWithoutChangingInstantAsync()
    {
        using ClientCultureScope culture = new("en-US");
        ServiceCollection services = new();
        CascadeActivator activator = new();
        services.AddLogging();
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment content = builder =>
            {
                builder.OpenComponent<TimeProbe>(0);
                builder.CloseComponent();
            };
            Dictionary<string, object?> parameters = new()
            {
                ["Name"] = "DisplayTimeZone", ["Value"] = TimeZoneInfo.Utc, ["ChildContent"] = content,
            };
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root = await renderer.RenderComponentAsync<CascadingValue<TimeZoneInfo>>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Contains("2026-10-03 15:00:00.123", root.ToHtmlString(), StringComparison.Ordinal);
            parameters["Value"] = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
            await activator.Cascade!.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Contains("2026-10-04 00:00:00.123", root.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("09:00", root.ToHtmlString(), StringComparison.Ordinal);
        }).ConfigureAwait(true);
    }

    /// <summary>製品の表示境界だけを使って同じ瞬間を描画します。</summary>
    private sealed class TimeProbe : LocalizedComponentBase
    {
        /// <summary>確認対象の日時を表示します。</summary>
        protected override void BuildRenderTree(RenderTreeBuilder builder)
            => builder.AddContent(0, FormatTime(new DateTimeOffset(2026, 10, 3, 15, 0, 0, 123, TimeSpan.Zero)));
    }

    /// <summary>通常の生成を維持したままcascadeへ表示設定の変更を届けます。</summary>
    private sealed class CascadeActivator : IComponentActivator
    {
        internal CascadingValue<TimeZoneInfo>? Cascade
        {
            get; private set;
        }

        /// <summary>コンポーネントを生成し、設定を配布する要素だけを保持します。</summary>
        /// <returns>生成したコンポーネントです。</returns>
        public IComponent CreateInstance(Type componentType)
        {
            if (componentType == typeof(TimeProbe))
            {
                return new TimeProbe();
            }
            IComponent component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is CascadingValue<TimeZoneInfo> cascade)
            {
                Cascade = cascade;
            }
            return component;
        }
    }
}
