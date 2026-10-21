using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Database;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting.Packages;

/// <summary>実配布パッケージの推移依存とネイティブ呼出しを、隔離したワーカーで確認します。</summary>
public sealed class NugetNativePackageTests
{
    /// <summary>OS限定依存を持つ通信ライブラリーとネイティブOpusを同じ環境で使用します。</summary>
    /// <returns>外部通信を行わない符号化・復号と管理アセンブリ読込みの確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NUGET_SOURCE")]
    public async Task SipSorceryAndNativeOpusExecuteInIsolatedWorkerAsync()
    {
        ScriptPackageFixture fixture = new(nugetSource: Environment.GetEnvironmentVariable("CONTENTS_TEST_NUGET_SOURCE"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        ScriptPackageReference[] packages = [new("sipsorcery", "10.0.17"), new("concentus", "2.2.2"), new("concentus.native.netcore", "1.5.2")];
        foreach (ScriptPackageReference package in packages)
        {
            ScriptPackageChangeRequest request = new(Guid.NewGuid(), EScriptPackageKind.Nuget, package, ExpectedRevision: fixture.Store.Current(EScriptPackageKind.Nuget)?.Revision);
            fixture.Manager.Change(request);
            ScriptPackageOperation result = await fixture.WaitAsync(request.OperationId).ConfigureAwait(true);
            Assert.True(result.Outcome == EOutcome.Succeeded, result.Reason);
        }
        string code = """
            using Concentus;
            using Concentus.Enums;
            using SIPSorcery.Net;
            using System.Runtime.InteropServices;
            [DllImport("opus", EntryPoint = "opus_get_version_string", CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr NativeVersion();
            public static string Run()
            {
                if (string.IsNullOrEmpty(Marshal.PtrToStringAnsi(NativeVersion())))
                    throw new InvalidOperationException("Native Opus version is missing.");
                using var encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
                using var decoder = OpusCodecFactory.CreateDecoder(48000, 2);
                if (encoder.GetType().Namespace != "Concentus.Native" || decoder.GetType().Namespace != "Concentus.Native")
                    throw new InvalidOperationException("Native codec was not selected.");
                var input = new float[1920];
                var output = new float[1920];
                var packet = new byte[4000];
                for (int i = 0; i < input.Length; i++) input[i] = (float)(0.01 * Math.Sin(i * 0.1));
                int count = encoder.Encode(input.AsSpan(), 960, packet.AsSpan(), packet.Length);
                int samples = decoder.Decode(packet.AsSpan(0, count), output.AsSpan(), 960, false);
                if (!output.Any(sample => sample != 0)) throw new InvalidOperationException("No audio was decoded.");
                return typeof(RTCPeerConnection).Assembly.GetName().Name + "|" + encoder.GetType().FullName + "|" + samples;
            }
            """;
        string settings = await fixture.SettingsAsync("csharp", code).ConfigureAwait(true);
        RecordingWorkflowLog log = new();
        NodeResult execution = await fixture.Node("csharp").ExecuteAsync(NodeTestContext.Create(settings) with { Log = log }).ConfigureAwait(true);
        Assert.True(execution.Outcome == EOutcome.Succeeded, execution.ReasonCode + ": " + string.Join("; ", log.Entries.Select(entry => entry.Data?.Json.GetRawText())));
        Assert.Equal("SIPSorcery|Concentus.Native.NativeOpusEncoder|960", execution.Outputs["value"].GetString());
    }
}
