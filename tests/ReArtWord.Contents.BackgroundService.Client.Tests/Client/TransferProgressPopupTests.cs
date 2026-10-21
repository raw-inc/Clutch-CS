using ReArtWord.Contents.BackgroundService.Client.Layout;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>転送率の表示と、Coreが確定する転送結果を分けます。</summary>
public sealed class TransferProgressPopupTests
{
    /// <summary>未列挙は不定進捗、空ファイルの完了は確認済み成功だけを使います。</summary>
    [Theory]
    [InlineData("Queued", 0, 0, null)]
    [InlineData("Unknown", 0, 0, null)]
    [InlineData("Succeeded", 0, 0, 100d)]
    [InlineData("Copying", 25, 100, 25d)]
    [InlineData("Unknown", 100, 100, 100d)]
    [InlineData("Copying", -5, 100, 0d)]
    [InlineData("Copying", 200, 100, 100d)]
    public void ProgressDoesNotChangeExecutionState(string state, long copied, long total, double? expected)
    {
        FileTransferPeer peer = new("c", "p", Guid.NewGuid());
        FileTransferJob job = new(new(Guid.NewGuid(), peer, peer, "a", "b"), state, copied, total, DateTimeOffset.UtcNow);
        Assert.Equal(expected, TransferProgressPopup.Percent(job));
        Assert.Equal(state, job.State);
    }

    /// <summary>ブラウザーの実行OSに依存せず、WindowsとLinuxのファイル名を表示します。</summary>
    [Theory]
    [InlineData("C:\\contents\\音声.wav", "音声.wav")]
    [InlineData("/var/media/voice.wav", "voice.wav")]
    [InlineData("C:\\contents\\folder\\", "folder")]
    public void FileNameAcceptsBothHostPathStyles(string path, string name) => Assert.Equal(name, TransferProgressPopup.FileName(path));
}
