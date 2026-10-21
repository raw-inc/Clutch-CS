using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>前回のWF完了後に入力する通常時の遅延を、高負荷試験から分離して計測します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class ForwardingLatencyProbeTests(ITestOutputHelper output)
{
    /// <summary>4つの実Coreと隔離UDPで、単発・分配それぞれの転送時間を確認します。</summary>
    /// <returns>各入力の送信完了と、初回・通常時の計測です。</returns>
    [Fact]
    public Task MeasuresForwardingLatency() => new ForwardingScenario(output).RunAsync(
        Environment.GetEnvironmentVariable("CONTENTS_PROBE_LABEL") ?? "latency", ForwardingScenario.Iterations(8));
}
