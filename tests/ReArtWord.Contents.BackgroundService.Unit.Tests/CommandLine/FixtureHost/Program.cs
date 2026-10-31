using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ReArtWord.Contents.BackgroundService.CommandLineFixture;

/// <summary>前景実起動の引数と終了待機だけを検証する子です。</summary>
internal static class Program
{
    /// <summary>明示設定から終了コードまたは待機を選択します。</summary>
    /// <returns>設定された実終了コードです。</returns>
    // 抑制理由: C#固定entrypointです。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "C#固定entrypointです。"),
     SuppressMessage("SourceQuality", "RAW601", Justification = "C#固定entrypointです。")]
    private static async Task<int> Main(string[] args)
    {
        string applicationSettings = Path.Combine(AppContext.BaseDirectory, "application-fixture.json");
        if (File.Exists(applicationSettings))
        {
            return await ApplicationFixture.RunAsync(args).ConfigureAwait(false);
        }
        string config = "--config";
        if (args.Length != 2 || !string.Equals(args[0], config, StringComparison.Ordinal))
        {
            return 19;
        }
        string text = await File.ReadAllTextAsync(args[1]).ConfigureAwait(false);
        string marker = args[1] + ".started";
        await File.WriteAllTextAsync(marker + ".tmp", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        File.Move(marker + ".tmp", marker);
        string wait = "wait";
        string grace = "grace";
        string pipe = "pipe";
        string inputMode = "input";
        string inheritedOutput = "inherited-output", inheritedEmpty = "inherited-empty", inheritedFailure = "inherited-failure";
        string diagnosticSuccess = "diagnostic-success", diagnosticFailure = "diagnostic-failure";
        if (string.Equals(text, diagnosticSuccess, StringComparison.Ordinal) || string.Equals(text, diagnosticFailure, StringComparison.Ordinal))
        {
            await Console.Out.WriteAsync("completed").ConfigureAwait(false);
            await Console.Error.WriteAsync("known-error\n" + new string('e', 32768) + "end-of-stderr").ConfigureAwait(false);
            if (string.Equals(text, diagnosticFailure, StringComparison.Ordinal))
            {
                return 7;
            }
            return 0;
        }
        if (string.Equals(text, inheritedOutput, StringComparison.Ordinal) || string.Equals(text, inheritedEmpty, StringComparison.Ordinal)
            || string.Equals(text, inheritedFailure, StringComparison.Ordinal))
        {
            string childConfig = args[1] + ".child";
            await File.WriteAllTextAsync(childConfig, "wait").ConfigureAwait(false);
            ProcessStartInfo start = new(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(childConfig);
            using Process child = Process.Start(start) ?? throw new IOException("The pipe fixture child did not start.");
            if (!string.Equals(text, inheritedEmpty, StringComparison.Ordinal))
            {
                await Console.Out.WriteAsync("completed").ConfigureAwait(false);
                await Console.Out.FlushAsync().ConfigureAwait(false);
            }
            if (string.Equals(text, inheritedFailure, StringComparison.Ordinal))
            {
                return 7;
            }
            return 0;
        }
        if (string.Equals(text, inputMode, StringComparison.Ordinal))
        {
            Console.InputEncoding = System.Text.Encoding.UTF8;
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            await Console.Out.WriteAsync(new string('o', 8192)).ConfigureAwait(false);
            await Console.Error.WriteAsync(new string('e', 8192)).ConfigureAwait(false);
            string input = await Console.In.ReadToEndAsync().ConfigureAwait(false);
            await Console.Out.WriteAsync(input).ConfigureAwait(false);
            return 0;
        }
        if (string.Equals(text, pipe, StringComparison.Ordinal))
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes("日本語🚀" + new string('x', 8192))).ToArray();
            using Stream output = Console.OpenStandardOutput();
            for (int index = 0; index < bytes.Length; index += 7)
            {
                await output.WriteAsync(bytes.AsMemory(index, Math.Min(7, bytes.Length - index))).ConfigureAwait(false);
            }
            return 0;
        }
        if (string.Equals(text, wait, StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }
        if (string.Equals(text, grace, StringComparison.Ordinal))
        {
            string release = args[1] + ".release";
            while (!File.Exists(release))
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
            return 0;
        }
        return int.Parse(text, CultureInfo.InvariantCulture);
    }
}
