using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>自由記述の構文・CLR型を、コードを実行せず検査します。</summary>
public sealed class CSharpScriptAnalysisTests
{
    /// <summary>using・別名・static参照を、元コードの行番号を保って解析します。</summary>
    [Fact]
    public void UsingDirectivesWorkAndDiagnosticsKeepOriginalLines()
    {
        ScriptAnalysis valid = CSharpScriptCompiler.Analyze("using System.Text;\nusing static System.Math;\npublic static string Run() => new StringBuilder().Append(Abs(-2)).ToString();");
        Assert.Empty(valid.Diagnostics);
        ScriptAnalysis invalid = CSharpScriptCompiler.Analyze("using Alias = System.Text.StringBuilder;\n\npublic static int Run() => Missing();");
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Contains("script.cs(3,", StringComparison.Ordinal));
    }

    /// <summary>非同期結果・名前付き引数・配列・nullableを編集可能な型へ写します。</summary>
    [Fact]
    public void AnalyzerDescribesTypedArgumentsAndAsyncReturn()
    {
        ScriptAnalysis report = CSharpScriptCompiler.Analyze("public static Task<string[]> Run(long count, string? prefix) => Task.FromResult(new[] { prefix ?? count.ToString() });");
        Assert.Empty(report.Diagnostics);
        Assert.NotNull(report.Contract);
        Assert.Equal("int64", report.Contract.Arguments["count"].Type);
        Assert.True(report.Contract.Arguments["prefix"].Optional);
        Assert.Equal("array", report.Contract.Result.Type);
        Assert.Equal("string", report.Contract.Result.ElementType!.Type);
    }

    /// <summary>C#14のfield付きプロパティを.NET10のコンパイラーで扱います。</summary>
    [Fact]
    public void AnalyzerSupportsCSharp14WithoutExecutingModuleInitializer()
    {
        ScriptAnalysis report = CSharpScriptCompiler.Analyze("""
            public sealed class Data { public int Value { get; set => field = value; } }
            public static Data Run(Data value) => value;
            [System.Runtime.CompilerServices.ModuleInitializer]
            public static void Initialize() => throw new Exception("must never execute during analysis");
            """);
        Assert.Empty(report.Diagnostics);
        Assert.Equal("integer", report.Contract!.Result.Properties!["Value"].Type);
    }

    /// <summary>未対応CLR型をobjectとして曖昧に受理しません。</summary>
    [Theory]
    [InlineData("public static System.IO.Stream Run() => System.IO.Stream.Null;")]
    [InlineData("public static int Run(ref int value) => value;")]
    [InlineData("public static Task Run() => Task.CompletedTask;")]
    [InlineData("public sealed class Loop { public Loop Next { get; set; } = null!; } public static Loop Run(Loop value) => value;")]
    [InlineData("public static int Run(int value) => ;")]
    public void UnsupportedTypesAndInvalidSyntaxAreRejected(string code)
    {
        ScriptAnalysis report = CSharpScriptCompiler.Analyze(code);
        Assert.Null(report.Contract);
        Assert.NotEmpty(report.Diagnostics);
    }
}
