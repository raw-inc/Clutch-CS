using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>スクリプト環境の小さいローカル記録を、完全なJSONとしてだけ公開します。</summary>
internal static class ScriptRecordFile
{
    /// <summary>欠落だけを初期状態とし、壊れた記録は呼出元へ返します。</summary>
    /// <returns>保存された値、または未作成です。</returns>
    internal static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ContractJson.Options) ?? throw new InvalidDataException("script.packages.invalidRecord");
    }

    /// <summary>一時ファイルを耐久保存してから、同じボリューム上で公開します。</summary>
    internal static void Write<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, ContractJson.Options);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
