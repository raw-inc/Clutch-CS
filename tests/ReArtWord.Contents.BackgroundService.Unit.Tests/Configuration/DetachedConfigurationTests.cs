using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>離脱時のローカル運転既定と明示した用途制限を確認します。</summary>
public sealed class DetachedConfigurationTests
{
    /// <summary>省略した用途はローカル運転を許可し、明示したfalseだけを制限します。</summary>
    [Fact]
    public void OmittedPurposesKeepTheLocalOperationDefault()
    {
        CommonConfiguration configuration = CommonConfigurationReader.Read("""
            [cluster]
            id = "test"
            peerId = "a"
            address = "127.0.0.1"
            broadcast = "127.255.255.255"
            [detached]
            fileChange = false
            externalCommunication = false
            """, Path.GetTempPath());
        Assert.True(configuration.Detached.Edit);
        Assert.True(configuration.Detached.Compute);
        Assert.True(configuration.Detached.ProcessChange);
        Assert.False(configuration.Detached.FileChange);
        Assert.False(configuration.Detached.ExternalCommunication);
    }

    /// <summary>制限の型や誤記を許可既定へ読み替えません。</summary>
    [Theory]
    [InlineData("compute = 'false'")]
    [InlineData("fileChanges = false")]
    public void InvalidPolicyDoesNotFallBackToAllow(string policy)
    {
        Assert.Throws<FormatException>(() => CommonConfigurationReader.Read("""
            [cluster]
            id = "test"
            peerId = "a"
            address = "127.0.0.1"
            broadcast = "127.255.255.255"
            [detached]
            """ + Environment.NewLine + policy, Path.GetTempPath()));
    }
}
