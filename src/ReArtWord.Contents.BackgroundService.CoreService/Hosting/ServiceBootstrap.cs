using System.Security.Cryptography;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting;

/// <summary>初回のYAML定義導入を既存の永続保存へ接続します。</summary>
internal sealed class ServiceBootstrap
{
    private readonly ServiceRuntime runtime;
    private static string MarkerKey => "workflows";

    /// <summary>保存と実行の共通経路を指定します。</summary>
    public ServiceBootstrap(ServiceRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        this.runtime = runtime;
    }

    /// <summary>完了済みの導入を空の初回クラスタと誤認しないためのローカル確認です。</summary>
    /// <returns>初回定義導入を完了したことが保存されているかです。</returns>
    internal static async ValueTask<bool> IsInitializedAsync(ServiceRuntime runtime, CancellationToken cancellationToken = default)
        => await runtime.Repository.ReadAsync("peer:" + runtime.Cluster.LocalPeerId + ":bootstrap", MarkerKey, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>初回だけ設定ファイルを導入し、再起動時に古いファイルで保存版を上書きしません。</summary>
    /// <returns>初回導入の確認です。</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string scope = "peer:" + runtime.Cluster.LocalPeerId + ":bootstrap";
        if (await IsInitializedAsync(runtime, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        if (runtime.Configuration.Cluster.NewCluster)
        {
            List<WorkflowDefinition> definitions = [];
            foreach (string path in runtime.Configuration.WorkflowPaths)
            {
                WorkflowDefinition definition = WorkflowYamlReader.Read(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
                CompilationResult compiled = runtime.Compiler.Compile(definition);
                if (compiled.Workflow is null)
                {
                    throw new InvalidDataException("初期ワークフローの検証に失敗しました。設定ファイルを検証してください。");
                }
                definitions.Add(definition);
            }
            if (definitions.Select(static definition => definition.Id).Distinct(StringComparer.Ordinal).Count() != definitions.Count)
            {
                throw new InvalidDataException("初期ワークフローの識別子が重複しています。");
            }
            foreach (WorkflowDefinition definition in definitions)
            {
                if (await runtime.Workflows.GetAsync(definition.Id, cancellationToken).ConfigureAwait(false) is not null)
                {
                    continue;
                }
                Guid operationId = Identity("seed", runtime.Configuration.Cluster.Id, definition.Id);
                WorkflowSaveResult result = await runtime.Workflows.SaveAsync(new WorkflowSaveRequest(operationId, definition, null), cancellationToken).ConfigureAwait(false);
                if (result.Saved.Outcome != EOutcome.Succeeded || result.Shared.Outcome != EOutcome.Succeeded)
                {
                    throw new InvalidDataException("初期定義の保存を確認できません。操作結果を確認して明示的に保存してください。");
                }
            }
        }
        await runtime.Repository.WriteLocalSnapshotAsync(scope, MarkerKey, NodeValue.FromObject(true), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>暗黙の再受付で別操作IDを生成しないための内容識別子です。</summary>
    /// <returns>操作種類と対象から生成したIDです。</returns>
    private static Guid Identity(params string[] parts) => new(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts))).AsSpan(0, 16));
}
