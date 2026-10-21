using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>実行せずに.NET10/C#14の構文と専用Analyzerを適用します。</summary>
public static class CSharpScriptCompiler
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> references = new(References);
    private static readonly Dictionary<string, Lazy<(ScriptAnalysis Analysis, byte[]? Assembly)>> cache = new(StringComparer.Ordinal);
    private static readonly Lock gate = new();

    /// <summary>保存・編集・実行で同じコードと型契約を検証します。</summary>
    /// <returns>不正なコードを実行せず返す診断です。</returns>
    public static ScriptAnalysis Analyze(string code, ScriptPackageEnvironment? environment = null, bool continuous = false)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Compile(code, environment ?? ScriptPackageEnvironment.Empty, continuous).Value.Analysis;
    }

    /// <summary>コードブロックの構文を実行せずに検査し、Runの型契約は接続先の解析へ委ねます。</summary>
    /// <returns>構文エラーだけを含む指紋付き診断です。</returns>
    public static ScriptAnalysis AnalyzeSyntax(ScriptAnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string fingerprint = ContractJson.Hash(request);
        if (65536 < request.Code.Length)
        {
            return new(fingerprint, null, ["script.languageOrCodeInvalid"]);
        }
        SyntaxTree tree = CSharpSyntaxTree.ParseText(request.Code, new CSharpParseOptions(LanguageVersion.CSharp14, kind: SourceCodeKind.Script));
        return new(fingerprint, null, Messages(tree.GetDiagnostics()));
    }

    /// <summary>確認済みコードだけを実行プロセス用アセンブリへ出力します。</summary>
    /// <returns>解析結果と、成功時だけ存在するバイト列です。</returns>
    internal static (ScriptAnalysis Analysis, byte[]? Assembly) Emit(string code, ScriptPackageEnvironment? environment = null, bool continuous = false)
    {
        (ScriptAnalysis analysis, byte[]? assembly) = Compile(code, environment ?? ScriptPackageEnvironment.Empty, continuous).Value;
        return (analysis, assembly?.ToArray());
    }

    /// <summary>確認済みの型契約とアセンブリを同じ有限キャッシュに保持し、受信ごとに再出力しません。</summary>
    /// <returns>利用者コードをロードせずに準備した実行用バイト列です。</returns>
    private static (ScriptAnalysis Analysis, byte[]? Assembly) EmitCompilation(CSharpCompilation compilation, ScriptAnalysis analysis)
    {
        if (analysis.Contract is null || analysis.Diagnostics.Count != 0)
        {
            return (analysis, null);
        }
        using MemoryStream stream = new();
        EmitResult emitted = compilation.Emit(stream);
        if (!emitted.Success)
        {
            return (analysis with { Contract = null, Diagnostics = Messages(emitted.Diagnostics) }, null);
        }
        return (analysis, stream.ToArray());
    }

    /// <summary>共通の意味検査を行い、有限個だけコンパイル結果を保持します。</summary>
    /// <returns>利用者コードをロードしないコンパイル状態です。</returns>
    private static Lazy<(ScriptAnalysis Analysis, byte[]? Assembly)> Compile(string code, ScriptPackageEnvironment environment, bool continuous)
    {
        string fingerprint = ContractJson.Hash(new ScriptAnalysisRequest("csharp", code, Continuous: continuous));
        string key = fingerprint + ":" + environment.Info?.Revision;
        if (string.IsNullOrWhiteSpace(code) || 65536 < code.Length)
        {
            return new(() => (new(fingerprint, null, ["script.codeRequiredOrTooLarge"]), null));
        }
        using (gate.EnterScope())
        {
            if (cache.TryGetValue(key, out Lazy<(ScriptAnalysis Analysis, byte[]? Assembly)>? existing))
            {
                return existing;
            }
        }
        string source = Source(code);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
        CSharpCompilation compilation = CSharpCompilation.Create("WorkflowScript_" + fingerprint, [tree], PackageReferences(environment),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: false));
        string[] diagnostics = Messages(compilation.GetDiagnostics());
        ScriptContract? contract = null;
        if (diagnostics.Length == 0)
        {
            // Node.Validateは同期契約です。Analyzer側には同期コンテキストを渡さず、同じ検査結果を編集APIでも再利用します。
            ImmutableArray<Diagnostic> analyzerDiagnostics = compilation.WithAnalyzers([new ScriptContractAnalyzer { Continuous = continuous }]).GetAnalyzerDiagnosticsAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            diagnostics = Messages(analyzerDiagnostics);
            if (diagnostics.Length == 0)
            {
                contract = ScriptContractAnalyzer.Inspect(compilation, continuous);
            }
        }
        ScriptAnalysis analysis = new(fingerprint, contract, diagnostics, PackageEnvironmentId: environment.Info?.Revision, ValueProtocolVersion: 1);
        Lazy<(ScriptAnalysis Analysis, byte[]? Assembly)> prepared = new(() => EmitCompilation(compilation, analysis), LazyThreadSafetyMode.ExecutionAndPublication);
        using (gate.EnterScope())
        {
            if (cache.TryGetValue(key, out Lazy<(ScriptAnalysis Analysis, byte[]? Assembly)>? existing))
            {
                return existing;
            }
            if (16 <= cache.Count)
            {
                cache.Remove(cache.Keys.First());
            }
            cache[key] = prepared;
        }
        return prepared;
    }

    /// <summary>利用者のusingをラッパークラスの外へ置き、元コードの診断行を保ちます。</summary>
    /// <returns>名前空間参照と単一エントリーポイントを持つC#ソースです。</returns>
    private static string Source(string code)
    {
        SyntaxTree parsed = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.CSharp14, kind: SourceCodeKind.Script));
        char[] body = code.ToCharArray();
        System.Text.StringBuilder imports = new();
        foreach (UsingDirectiveSyntax directive in parsed.GetCompilationUnitRoot().Usings)
        {
            int line = parsed.GetLineSpan(directive.Span).StartLinePosition.Line + 1;
            imports.Append(System.Globalization.CultureInfo.InvariantCulture, $"#line {line} \"script.cs\"\n{directive}\n");
            for (int index = directive.SpanStart; index < directive.Span.End; index++)
            {
                if (body[index] is not ('\r' or '\n'))
                {
                    body[index] = ' ';
                }
            }
        }
        return imports + """
            #line default
            #nullable enable
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Threading;
            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;
            public static class WorkflowScript {
            #line 1 "script.cs"
            """ + "\n" + new string(body) + "\n}";
    }

    /// <summary>同じアセンブリの古いパッケージ参照で.NET標準参照を重複させません。</summary>
    /// <returns>名前ごとに最も新しい互換参照を一つ選んだメタデータです。</returns>
    private static IEnumerable<MetadataReference> PackageReferences(ScriptPackageEnvironment environment)
    {
        if (environment.References.Count == 0)
        {
            return references.Value;
        }
        return references.Value.Select(reference => reference.Display!).Concat(environment.References)
            .Select(path => (Path: path, Name: System.Reflection.AssemblyName.GetAssemblyName(path)))
            .GroupBy(item => item.Name.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => (MetadataReference)MetadataReference.CreateFromFile(group.MaxBy(item => item.Name.Version).Path));
    }

    /// <summary>.NET10実行環境のBCLだけをスクリプトの参照対象にします。</summary>
    /// <returns>一度だけロードする標準アセンブリ参照です。</returns>
    private static ImmutableArray<MetadataReference> References()
    {
        string directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? throw new InvalidOperationException("script.runtimeReferencesUnavailable");
        return trusted.Split(Path.PathSeparator).Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase) && StandardAssembly(Path.GetFileNameWithoutExtension(path)))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToImmutableArray();
    }

    /// <summary>self-contained配置でも、同じフォルダーの製品DLLを暗黙参照へ加えません。</summary>
    /// <returns>.NET標準参照の対象名かです。</returns>
    private static bool StandardAssembly(string name) => name.StartsWith("System.", StringComparison.Ordinal)
        || name is "System" or "mscorlib" or "netstandard" or "Microsoft.CSharp" or "Microsoft.VisualBasic" or "Microsoft.VisualBasic.Core" or "Microsoft.Win32.Registry" or "Microsoft.Win32.Primitives";

    /// <summary>大量診断で編集応答を無制限にしません。</summary>
    /// <returns>構文・型エラーの先頭32件です。</returns>
    private static string[] Messages(IEnumerable<Diagnostic> diagnostics) => diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(32).Select(diagnostic => diagnostic.ToString()).ToArray();
}
