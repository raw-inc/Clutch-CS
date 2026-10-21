using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>人が指定する用途名と保存媒体を曖昧な既定値へ読み替えません。</summary>
public sealed class PersistenceConfigurationTests
{
    /// <summary>保持方針を変更しても、用途割当の保存先指紋を変更しません。</summary>
    [Fact]
    public void HistoryPolicyIsIndependentOfBackendRouting()
    {
        CommonConfiguration defaults = Read(string.Empty);
        CommonConfiguration configured = Read("""
            [storage.history]
            enabled = false
            keepDays = 3
            maxRecords = 25000
            batchRecords = 500
            """);
        Assert.Equal(new HistoryArchiveOptions(), defaults.HistoryArchive);
        Assert.Equal(new HistoryArchiveOptions(false, 3, 25000, 500), configured.HistoryArchive);
        Assert.Equal(Contracts.Serialization.ContractJson.Hash(defaults.Persistence), Contracts.Serialization.ContractJson.Hash(configured.Persistence));
    }

    /// <summary>不正な件数や無制限の一括処理を起動前に拒否します。</summary>
    [Theory]
    [InlineData("keepDays = -1")]
    [InlineData("maxRecords = 0")]
    [InlineData("batchRecords = 10001")]
    public void InvalidHistoryLimitsAreRejected(string setting)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Read("[storage.history]" + Environment.NewLine + setting));
    }

    /// <summary>共通既定と、明示された用途の媒体を別々に保持します。</summary>
    [Fact]
    public void PurposeOverridesKeepTheConfiguredDefault()
    {
        CommonConfiguration configuration = Read("""
            backend = "json"
            [storage.purposes]
            configuration = "sqlite"
            diagnostics = "json"
            """);
        Assert.Equal(EStorageBackend.Json, configuration.Persistence.DefaultBackend);
        Assert.Equal(EStorageBackend.Sqlite, configuration.Persistence.Purposes![EStoragePurpose.Configuration]);
        Assert.Equal(EStorageBackend.Json, configuration.Persistence.Purposes[EStoragePurpose.Diagnostics]);
        Assert.False(configuration.Persistence.Purposes.ContainsKey(EStoragePurpose.Runtime));
    }

    /// <summary>未対応用途と媒体を起動前に拒否します。</summary>
    [Theory]
    [InlineData("[storage.purposes]\ndiagnostic = 'json'")]
    [InlineData("[storage.purposes]\nruntime = 'memory'")]
    public void UnknownPurposeOrBackendIsRejected(string policy)
    {
        Assert.Throws<FormatException>(() => Read(policy));
    }

    /// <summary>未保存稼働と明示代替保存、切断中の内部候補を別々に指定できます。</summary>
    [Fact]
    public void AvailabilityPoliciesAreAppliedOnlyToTheirDeclaredPurpose()
    {
        CommonConfiguration configuration = Read("""
            [storage.availability]
            maximumItems = 80
            maximumBytes = 4096
            [storage.availability.configuration]
            saveFailure = "UseUnsaved"
            fallbackBackend = "Sqlite"
            [storage.availability.runtime]
            offline = "LocalUnconfirmed"
            """);
        Assert.Equal(80, configuration.Availability.MaximumItems);
        Assert.Equal(4096, configuration.Availability.MaximumBytes);
        Assert.Equal(new(EAvailabilitySaveFailure.UseUnsaved, FallbackBackend: EStorageBackend.Sqlite), configuration.Availability.Purposes![EStoragePurpose.Configuration]);
        Assert.Equal(new(Offline: EAvailabilityOffline.LocalUnconfirmed), configuration.Availability.Purposes[EStoragePurpose.Runtime]);
    }

    /// <summary>外部動作や非対応用途の弱い保存方針を黙って許可しません。</summary>
    [Theory]
    [InlineData("[storage.availability.processes]\nsaveFailure = 'UseUnsaved'")]
    [InlineData("[storage.availability.diagnostics]\noffline = 'LocalUnconfirmed'")]
    [InlineData("[storage.availability.runtime]\nfallbackBackend = 'memory'")]
    [InlineData("[storage.availability]\nmaximumItems = 0")]
    public void UnsupportedAvailabilityPolicyIsRejected(string policy)
    {
        Assert.Throws<FormatException>(() => Read(policy));
    }

    /// <summary>副作用のない共通設定読取りだけを行います。</summary>
    /// <returns>解析した用途別設定です。</returns>
    private static CommonConfiguration Read(string storage) => CommonConfigurationReader.Read("""
        [cluster]
        id = "test"
        peerId = "a"
        address = "127.0.0.1"
        broadcast = "127.255.255.255"
        [storage]
        """ + Environment.NewLine + storage, Path.GetTempPath());
}
