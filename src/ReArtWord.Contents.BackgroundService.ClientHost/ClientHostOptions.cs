using System.Net;

namespace ReArtWord.Contents.BackgroundService.ClientHost;

/// <summary>常駐Coreとは独立したGUI配信の待受と接続先です。</summary>
internal sealed record ClientHostOptions(Uri Listen, string StateDirectory)
{
    /// <summary>指定先以外への暗黙接続を行わず、明示したURLを確認します。</summary>
    /// <returns>配信と管理APIの二つの入口です。</returns>
    internal static ClientHostOptions Parse(string[] args)
    {
        string stateOption = "--state";
        string listenOption = "--listen";
        string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReArtWord", "Contents", "Client");
        string listen = "http://127.0.0.1:5081/";
        HashSet<string> supplied = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (args.Length <= index + 1 || !supplied.Add(args[index]))
            {
                throw new ArgumentException("引数は重複のない --state directory と --listen URL で指定してください。", nameof(args));
            }
            if (string.Equals(args[index], stateOption, StringComparison.Ordinal))
            {
                state = args[index + 1];
            }
            else
            {
                if (!string.Equals(args[index], listenOption, StringComparison.Ordinal))
                {
                    throw new ArgumentException("未定義の引数です。", nameof(args));
                }
                listen = args[index + 1];
            }
        }
        Uri listenUri = Validate(listen);
        if (!IPAddress.TryParse(listenUri.Host, out IPAddress? address) || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("画面配信はローカルPCのloopback IPを指定してください。", nameof(args));
        }
        return new ClientHostOptions(listenUri, Path.GetFullPath(state));
    }

    /// <summary>認証情報や任意パスを接続先へ混入させません。</summary>
    /// <returns>Coreまたは配信先のroot URLです。</returns>
    internal static Uri Validate(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.Length != 1)
        {
            throw new ArgumentException("接続先は認証情報やパスを含まないHTTP/HTTPSのroot URLで指定してください。", nameof(value));
        }
        return uri;
    }
}
