namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>更新試験に使うアプリケーションのビルド出力を選びます。</summary>
internal static class TestApplicationOutput
{
    /// <summary>同じアプリの別ビルドを除き、元のディレクトリ構成を保ちます。</summary>
    /// <returns>試験用の配布元に含めるファイルです。</returns>
    internal static IEnumerable<string> EnumerateFiles(string source, string assemblyName)
    {
        foreach (string file in Directory.EnumerateFiles(source))
        {
            yield return file;
        }
        string configuration = assemblyName + ".runtimeconfig.json";
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            if (File.Exists(Path.Combine(directory, configuration)))
            {
                continue;
            }

            // RIDを指定しないビルドでは、アーキテクチャ共通のDLLが runtimes/win などに配置されます。
            // .deps.json の参照先を残し、.NET ホストが実行時に選べるようにします。
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }
}
