using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transfers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>ファイル操作の再開で、元の生成方式と要求本文を取り違えないことを確認します。</summary>
public sealed class FileIdentityGenerationTests
{
    /// <summary>旧記録の読み書きでは本文と派生IDを変えません。</summary>
    [Fact]
    public void LegacyRecordKeepsItsPayloadAndPhaseIds()
    {
        FileTransactionOperation operation = Create();
        JsonElement legacy = JsonSerializer.SerializeToElement(operation);
        FileTransactionOperation restored = FileTransactionRecord.Read(legacy);
        Assert.Equal(EDerivedIdFormat.Legacy, restored.IdentityFormat);
        Assert.Equal(ContractJson.Hash(legacy), ContractJson.Hash(FileTransactionRecord.Write(restored).Json));
        Guid original = FileStageClient.Identity(operation.Request.OperationId, "capture");
        using DerivedIdentityScope identities = new(restored.IdentityFormat);
        Assert.Equal(original, FileStageClient.Identity(restored.Request.OperationId, "capture"));
    }

    /// <summary>新記録は方式を復元し、方式を知らない旧型では再開できません。</summary>
    [Fact]
    public void NewRecordPreservesItsGenerationAndCannotBeReadAsLegacy()
    {
        FileTransactionOperation operation = Create() with { IdentityFormat = EDerivedIdFormat.Uuid8 };
        NodeValue saved = FileTransactionRecord.Write(operation);
        Assert.Throws<JsonException>(() => saved.Json.Deserialize<FileTransactionOperation>());
        FileTransactionOperation restored = FileTransactionRecord.Read(saved.Json);
        Assert.Equal(operation.IdentityFormat, restored.IdentityFormat);
        Assert.Equal(ContractJson.Hash(operation.Request), ContractJson.Hash(restored.Request));
        Assert.Equal(operation.Fingerprint, restored.Fingerprint);
        Assert.Equal(operation.OwnerInstanceId, restored.OwnerInstanceId);
        using DerivedIdentityScope identities = new(restored.IdentityFormat);
        Guid first = FileStageClient.Identity(restored.Request.OperationId, "capture");
        Assert.False(OperationIdentity.TryGetIssuedAt(first, out _));
        FileTransactionOperation next = FileTransactionRecord.Read(FileTransactionRecord.Write(restored).Json);
        Assert.Equal(first, FileStageClient.Identity(next.Request.OperationId, "capture"));
    }

    /// <summary>新形式の本文を旧方式へ推測で読み替えません。</summary>
    [Fact]
    public void UnsupportedPayloadGenerationIsRejected()
    {
        JsonElement value = JsonSerializer.SerializeToElement(new object[] { "clutch-derived-future", new { value = 1 } });
        Assert.Throws<InvalidDataException>(() => DerivedIdentityPayload.Read(value));
    }

    /// <summary>実アクセスを伴わない保存形式の固定条件です。</summary>
    /// <returns>要求・所有実体・初回結果を持つ操作です。</returns>
    private static FileTransactionOperation Create()
    {
        FileTransactionRequest request = new(Guid.NewGuid(), []);
        return new(ContractJson.Hash(request), request, [], "peer-a", Guid.NewGuid(), null, new(request.OperationId, EOutcome.Unknown, []));
    }
}
