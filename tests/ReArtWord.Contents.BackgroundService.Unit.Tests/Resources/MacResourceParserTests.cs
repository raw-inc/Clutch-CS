using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;

/// <summary>実OSなしでもmacOS観測の単位・欠損・差分標本を検証します。</summary>
public sealed class MacResourceParserTests
{
    /// <summary>二回目だけを採用し、壊れた百分率を正常値にしません。</summary>
    [Fact]
    public void CpuUsesLastIntervalAndRejectsMissingOrInvalidSamples()
    {
        const string first = "CPU usage: 1.00% user, 2.00% sys, 97.00% idle\n";
        Assert.Null(MacResourceParser.Cpu(first).Value);
        Assert.Equal(30, MacResourceParser.Cpu(first + "CPU usage: 20.00% user, 10.00% sys, 70.00% idle").Value);
        Assert.Null(MacResourceParser.Cpu(first + "CPU usage: 20.00% user, 10.00% sys, 170.00% idle").Value);
    }

    /// <summary>arm64の16KiBページを固定4KiBと取り違えません。</summary>
    [Fact]
    public void MemoryUsesReportedPageSizeAndDoesNotInventMissingValues()
    {
        const string sample = "Mach Virtual Memory Statistics: (page size of 16384 bytes)\nPages free: 100.\nPages inactive: 200.\nPages speculative: 50.\n";
        IReadOnlyList<ResourceMetric> values = MacResourceParser.Memory(sample, "16777216\n");
        Assert.Equal(2457600, Assert.Single(values, value => string.Equals(value.Name, "memory.available", StringComparison.Ordinal)).Value);
        Assert.Equal(3276800, Assert.Single(values, value => string.Equals(value.Name, "memory.inactive", StringComparison.Ordinal)).Value);
        Assert.Throws<InvalidDataException>(() => MacResourceParser.Memory(sample.Replace("Pages free:", "Missing:", StringComparison.Ordinal), "16777216"));
        Assert.Throws<InvalidDataException>(() => MacResourceParser.Memory(sample, "4096"));
    }
}
