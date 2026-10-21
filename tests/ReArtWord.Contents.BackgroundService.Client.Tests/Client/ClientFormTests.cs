using ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>フォーム入力を古い値で送らず、確定時に検証します。</summary>
public sealed class ClientFormTests
{
    /// <summary>実際に入力したtimezoneとHost集合を要求へ反映します。</summary>
    [Fact]
    public void ConfigurationPreservesEditedValues()
    {
        ClusterOperationalSettings settings = ConfigurationForm.Build("Tokyo Standard Time", EFileConflictPolicy.Wait, EFileInternalFailurePolicy.Restore, [("test-hosts", "host-local")]);
        Assert.Equal("Tokyo Standard Time", settings.TimeZoneId);
        Assert.Equal(["host-local"], settings.HostGroups["test-hosts"]);
    }

    /// <summary>入力不正は操作ID生成より前に項目名を添えて拒否します。</summary>
    [Theory]
    [InlineData("", "hosts", "Group 1")]
    [InlineData("group", " , ", "Host IDs")]
    public void InvalidGroupReportsField(string name, string hosts, string expected)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ConfigurationForm.Build("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, [(name, hosts)]));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    /// <summary>空欄・不正整数・範囲超過を以前の値へ戻しません。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("999999999999999999")]
    public void UnconfirmedNumberCannotBecomePreviousValue(string text)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => FormInput.Integer(text, "確認期限", 1, int.MaxValue / 1000));
        Assert.Contains("確認期限", error.Message, StringComparison.Ordinal);
        Assert.Equal(30, FormInput.Integer("30", "確認期限", 1));
    }
}
