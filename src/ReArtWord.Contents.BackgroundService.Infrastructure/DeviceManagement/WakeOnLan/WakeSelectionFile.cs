using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WakeOnLan;

/// <summary>UnixのPC共通NIC選択を、一つの原子的なファイルで保持します。</summary>
internal sealed class WakeSelectionFile(string path)
{
    /// <summary>未保存だけを既定選択とし、破損や権限不足は設定なしに読み替えません。</summary>
    /// <returns>別Coreによる最新の保存も含む選択です。</returns>
    internal DeviceWakeSelection Read()
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (16384 < stream.Length)
            {
                throw new InvalidDataException("Device.InvalidWakeSelection");
            }
            DeviceWakeSelection selection = JsonSerializer.Deserialize<DeviceWakeSelection>(stream, ContractJson.Options)
                ?? throw new InvalidDataException("Device.InvalidWakeSelection");
            selection.Validate();
            return selection;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(true, []);
        }
    }

    /// <summary>同一PCの変更排他内で、完成した選択だけを他Coreへ公開します。</summary>
    internal void Write(DeviceWakeSelection selection)
    {
        selection.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, selection, ContractJson.Options);
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
