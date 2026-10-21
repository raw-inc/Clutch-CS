using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>通常遅延とは別に、連続入力と瞬間集中で欠落・重複・滞留の解放を確認します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class ForwardingLoadTests(ITestOutputHelper output)
{
    /// <summary>前回実行を待たず約60Hzで入力し、各出力を元の入力へ照合します。</summary>
    /// <returns>全入力の処理結果とCoreの負荷・解放後の観測です。</returns>
    [Fact]
    public Task ContinuousInputsPreserveEveryForwardingResult()
    {
        int interval = 17;
        bool configured = int.TryParse(Environment.GetEnvironmentVariable("CONTENTS_PROBE_INTERVAL_MS"), out int requested);
        if (configured && requested is >= 1 and <= 1000)
        {
            interval = requested;
        }
        return new ForwardingScenario(output).RunAsync((Environment.GetEnvironmentVariable("CONTENTS_PROBE_LABEL") ?? "load") + "-continuous",
            ForwardingScenario.Iterations(64), interval);
    }

    /// <summary>待ち時間なしの一斉入力でも、出力を混同せず全件終了後に実行枠を解放します。</summary>
    /// <returns>有限burstに対する出力と負荷の観測です。</returns>
    [Fact]
    public Task BurstInputsPreserveEveryForwardingResult() => new ForwardingScenario(output).RunAsync(
        (Environment.GetEnvironmentVariable("CONTENTS_PROBE_LABEL") ?? "load") + "-burst", ForwardingScenario.Iterations(16), 0);
}
