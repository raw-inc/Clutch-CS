using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>専用子プロセスが一度だけ実行する固定入力です。</summary>
/// <param name="AssemblyPath">検証済みコードをemitしたアセンブリです。</param>
/// <param name="Arguments">名前付きJSON引数です。</param>
/// <param name="Assemblies">固定したパッケージの実行時DLLです。</param>
/// <param name="NativeLibraries">実行先OS向けのネイティブ資産です。</param>
/// <param name="MaximumDiagnosticBytes">失敗診断も既存の出力量制限以内に収めます。</param>
/// <param name="SourcePipe">継続実行の出力と取消を運ぶ、親プロセスが所有するローカルpipeです。</param>
public sealed record CSharpWorkerRequest(string AssemblyPath, JsonElement Arguments, IReadOnlyList<string>? Assemblies = null, IReadOnlyList<string>? NativeLibraries = null, int MaximumDiagnosticBytes = 1048576, string? SourcePipe = null);
