using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.CoreService.Files;
using ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>Core再起動時に開始済みコピーを暗黙再実行しない保存境界です。</summary>
public sealed class FileTransferQueueTests
{
    /// <summary>待機・実行中・完了を別々に復元します。</summary>
    /// <returns>実ファイルに保存した共有キューの復元です。</returns>
    [Fact]
    public async Task JournalRestoresPendingAndMarksInterruptedCopiesUnknown()
    {
        using MediaTestFiles files = new();
        string directory = Path.Combine(files.Root, "queue");
        FileTransferJournal journal = new(directory);
        FileTransferPeer peer = new("cluster", "peer", Guid.NewGuid(), 47740);
        FileTransferRequest request = new(Guid.NewGuid(), peer, peer, "source", "destination");
        FileTransferJob pending = new(request, "Queued", 0, 0, DateTimeOffset.UtcNow);
        FileTransferJob running = pending with
        {
            Request = request with
            {
                Id = Guid.NewGuid()
            },
            State = "Copying",
            BytesCopied = 3,
            TotalBytes = 10
        };
        FileTransferJob finished = pending with
        {
            Request = request with
            {
                Id = Guid.NewGuid()
            },
            State = "Succeeded",
            BytesCopied = 10,
            TotalBytes = 10
        };
        await journal.SaveAsync([pending, running, finished]).ConfigureAwait(true);
        FileTransferJob[] restored = await journal.LoadAsync().ConfigureAwait(true);
        Assert.Equal("Queued", restored[0].State);
        Assert.Equal("Unknown", restored[1].State);
        Assert.Equal(3, restored[1].BytesCopied);
        Assert.Equal("Succeeded", restored[2].State);
        Assert.Equal(request, restored[0].Request);
    }
}
