using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>人が書く設定の型とバイナリの区別を検証します。</summary>
public sealed class ConfigurationReaderTests
{
    /// <summary>既存rootsの設定にかかわらず通常のファイル機能はOSアクセス権を使用します。</summary>
    [Fact]
    public void CoreFilesUseOperatingSystemPermissionsOutsideLegacyRoots()
    {
        CommonConfiguration configuration = CommonConfigurationReader.Read("""
            [cluster]
            id = "test"
            peerId = "a"
            address = "127.0.0.1"
            broadcast = "127.255.255.255"
            [files]
            roots = ["legacy-content"]
            """, Path.GetTempPath());
        Assert.True(configuration.Files.UseOperatingSystemAccess);
        ReArtWord.Contents.BackgroundService.Runtime.Files.FileAccessPolicy policy = new(configuration.Files);
        string outside = Path.Combine(Path.GetTempPath(), "contents-access-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(outside, policy.ResolvePath(outside));
        Assert.Throws<UnauthorizedAccessException>(() => policy.ResolvePath(Path.GetPathRoot(outside)!));
        ReArtWord.Contents.BackgroundService.Runtime.Files.FileAccessPolicy staging = new(new([configuration.Files.AllowedRoots[0]]));
        Assert.Throws<UnauthorizedAccessException>(() => staging.ResolvePath(outside));
    }

    /// <summary>誤記で安全側の設定が黙って既定値へ戻ることを防ぎます。</summary>
    [Fact]
    public void UnknownTomlSettingIsRejected()
    {
        Assert.Throws<FormatException>(() => CommonConfigurationReader.Read("""
            [cluster]
            id = "test"
            peerId = "a"
            address = "127.0.0.1"
            broadcast = "127.255.255.255"
            [files]
            conflictPolcy = "wait"
            """, Path.GetTempPath()));
    }

    /// <summary>更新管理は同じ明示UDP、永久ログはnullで保持します。</summary>
    [Fact]
    public void UpdatesAndPerLevelRetentionKeepTheirIntendedBindings()
    {
        CommonConfiguration configuration = CommonConfigurationReader.Read("""
            [cluster]
            id = "test"
            peerId = "a"
            address = "192.0.2.10"
            broadcast = "192.0.2.255"
            [updates]
            stagingRoot = "staging"
            [logs.retention.ERROR]
            rawDays = 0
            totalDays = "unlimited"
            """, Path.GetTempPath());
        Assert.Equal("test", configuration.Updates.ClusterId);
        Assert.Equal("192.0.2.255", configuration.Updates.BroadcastAddress);
        Assert.Equal(47740, configuration.Updates.ManagementPort);
        Assert.Equal("192.0.2.10", configuration.Updates.LocalAddress);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "staging"), configuration.Updates.StagingRoot);
        Assert.Null(Assert.Single(configuration.Logs.Retention!).TotalDays);
        Assert.Equal(0, configuration.Logs.Retention![0].RawDays);
    }

    /// <summary>起動時のTOML読込みも共通の更新期限を使い、明示値を保持します。</summary>
    [Theory]
    [InlineData("", 120000, 30000)]
    [InlineData("startTimeoutMilliseconds = 45000\nstopTimeoutMilliseconds = 12000", 45000, 12000)]
    public void UpdateTimeoutsUseSharedDefaultsAndPreserveExplicitSettings(string settings, int start, int stop)
    {
        CommonConfiguration configuration = CommonConfigurationReader.Read($$"""
            [cluster]
            id = "test"
            peerId = "a"
            address = "127.0.0.1"
            broadcast = "127.255.255.255"
            [updates]
            {{settings}}
            """, Path.GetTempPath());
        Assert.Equal(start, configuration.Updates.StartTimeoutMilliseconds);
        Assert.Equal(stop, configuration.Updates.StopTimeoutMilliseconds);
    }

    /// <summary>通常のYAML構造と生の定数入力を読み取ります。</summary>
    [Fact]
    public void StructuredYamlPreservesLiteralInputsAndBinary()
    {
        WorkflowDefinition definition = WorkflowYamlReader.Read("""
            id: example
            nodes:
              - id: input
                type: control.pass
                inputs:
                  value:
                    id: 9007199254740993
                    optional: null
                binaryInputs:
                  packet: AP8=
            """);

        Assert.Equal("example", definition.Name);
        Assert.Equal(9007199254740993L, definition.Nodes[0].Inputs!["value"].Json.GetProperty("id").GetInt64());
        Assert.Equal(EValueKind.Binary, definition.Nodes[0].Inputs!["packet"].Kind);
        Assert.Equal(new byte[] { 0, 255 }, definition.Nodes[0].Inputs!["packet"].Binary.ToArray());
    }

    /// <summary>入力の型間違いを黙って無視しません。</summary>
    [Fact]
    public void InvalidInputMappingIsRejected()
    {
        Assert.Throws<FormatException>(() => WorkflowYamlReader.Read("""
            id: example
            nodes:
              - id: input
                type: control.pass
                inputs: [1, 2]
            """));
    }

    /// <summary>本体相対パスと共通タイムゾーンを使用します。</summary>
    [Fact]
    public void TomlUsesExplicitNetworkAndConfigurationRelativePaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-config-reader");
        CommonConfiguration configuration = CommonConfigurationReader.Read("""
            workflows = ["workflows/main.yaml"]
            [cluster]
            id = "test"
            peerId = "peer-a"
            address = "127.0.0.1"
            broadcast = "127.255.255.255"
            initialize = true
            [storage]
            directory = "state"
            [time]
            zone = "UTC"
            [files]
            roots = ["content"]
            """, root);

        Assert.Equal(Path.Combine(root, "state"), configuration.DataDirectory);
        Assert.Equal(Path.Combine(root, "content"), configuration.Files.AllowedRoots[0]);
        Assert.Equal(Path.Combine(root, "workflows", "main.yaml"), configuration.WorkflowPaths[0]);
        Assert.Equal(TimeZoneInfo.Utc.Id, configuration.TimeZone.Id);
    }
}
