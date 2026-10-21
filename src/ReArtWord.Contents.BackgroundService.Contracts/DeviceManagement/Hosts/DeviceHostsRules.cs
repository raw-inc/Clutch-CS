using System.Net;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Hosts;

/// <summary>hostsのコメント・複数別名を保持し、OSが解釈する行の形式を確認します。</summary>
public static class DeviceHostsRules
{
    /// <summary>不正な内容をOSファイルへ渡す前に拒否します。</summary>
    public static void Validate(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (131072 < Encoding.UTF8.GetByteCount(content) || content.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException("Device.InvalidHostsFile", nameof(content));
        }
        foreach (string line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string[] words = line.Split('#')[0].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                continue;
            }
            if (words.Length < 2 || !IPAddress.TryParse(words[0], out IPAddress? address) || words.Skip(1).Any(name => 253 < name.Length || !name.Any(char.IsAsciiLetterOrDigit)
                    || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))))
            {
                throw new ArgumentException("Device.InvalidHostsEntry", nameof(content));
            }
            int family = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 4
                                                                                                : 6;
            _ = Network.DeviceNetworks.Address(words[0], family);
        }
    }
}
