using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>C#スクリプトの入口と引数・返却型を検査する専用Roslyn Analyzerです。</summary>
// 抑制理由: IDE/ビルドへ配布する拡張ではなく、製品が明示的に生成して.NET10内で実行するAnalyzerです。
[System.Diagnostics.CodeAnalysis.SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1036", Justification = "製品内でのみ生成する型検査であり、コンパイラー拡張パッケージとして配布しません。"),
 System.Diagnostics.CodeAnalysis.SuppressMessage("MicrosoftCodeAnalysisReleaseTracking", "RS2008", Justification = "独立Analyzerパッケージではなく、通常ノードのContractVersionで診断契約を管理します。"),
 System.Diagnostics.CodeAnalysis.SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1041", Justification = ".NET10製品内のスクリプト検査専用です。IDEへAnalyzer参照として読み込ませません。"),
 DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ScriptContractAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor rule = new("SCRIPT001", "Script contract", "{0}", "Scripting", DiagnosticSeverity.Error, true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>非同期列挙の一要素を出力契約として検査します。</summary>
    internal bool Continuous
    {
        get; init;
    }

    /// <summary>実行前と編集時に共通で使用する診断です。</summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [rule];

    /// <summary>利用者コードの実行を伴わず、コンパイルの意味モデルを検査します。</summary>
    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(compilation =>
        {
            if (Inspect(compilation.Compilation, Continuous) is null)
            {
                string message = Continuous ? "Define exactly one public static IAsyncEnumerable<T> Run method with supported argument and item types, and at most one CancellationToken parameter."
                                            : "Define exactly one public static Run method with supported JSON argument and return types.";
                compilation.ReportDiagnostic(Diagnostic.Create(rule, Location.None, message));
            }
        });
    }

    /// <summary>同期のワークフロー検証もAnalyzerと同じ型検査を使います。</summary>
    /// <returns>検証済みの型契約です。</returns>
    internal static ScriptContract? Inspect(Compilation compilation, bool continuous = false)
    {
        INamedTypeSymbol? script = compilation.GetTypeByMetadataName("WorkflowScript");
        IMethodSymbol[] methods = script?.GetMembers("Run").OfType<IMethodSymbol>().ToArray() ?? [];
        if (methods.Length != 1)
        {
            return null;
        }
        IMethodSymbol method = methods[0];
        if (!method.IsStatic || method.DeclaredAccessibility != Accessibility.Public || method.IsGenericMethod || method.ReturnsVoid || method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            return null;
        }
        Dictionary<string, ValueTypeDefinition> arguments = new(StringComparer.Ordinal);
        string? variadic = null;
        bool cancellation = false;
        string tokenType = "System.Threading.CancellationToken", sequenceType = "System.Collections.Generic.IAsyncEnumerable<T>";
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (continuous && string.Equals(parameter.Type.ToDisplayString(), tokenType, StringComparison.Ordinal))
            {
                if (cancellation || parameter.RefKind != RefKind.None)
                {
                    return null;
                }
                cancellation = true;
                continue;
            }
            ValueTypeDefinition? type = CSharpScriptTypes.Read(parameter.Type);
            if (parameter.IsParams && parameter.Type is IArrayTypeSymbol { Rank: 1 } array && CSharpScriptTypes.Read(array.ElementType) is { } element)
            {
                type = new("array", element);
                variadic = parameter.Name;
            }
            if (type is null || parameter.RefKind != RefKind.None || parameter.IsParams && variadic is null
                || parameter.IsOptional && !parameter.HasExplicitDefaultValue || 128 <= arguments.Count)
            {
                return null;
            }
            if (parameter.HasExplicitDefaultValue || parameter.IsParams)
            {
                type = type with { Optional = true };
            }
            arguments.Add(parameter.Name, type);
        }
        ITypeSymbol result = method.ReturnType;
        if (continuous)
        {
            if (result is not INamedTypeSymbol sequence || !string.Equals(sequence.OriginalDefinition.ToDisplayString(), sequenceType, StringComparison.Ordinal))
            {
                return null;
            }
            result = sequence.TypeArguments[0];
        }
        string task = "System.Threading.Tasks.Task<TResult>";
        if (result is INamedTypeSymbol named && string.Equals(named.OriginalDefinition.ToDisplayString(), task, StringComparison.Ordinal))
        {
            result = named.TypeArguments[0];
        }
        ValueTypeDefinition? output = CSharpScriptTypes.Read(result);
        return output is null ? null
                              : new(arguments, output, variadic);
    }
}
