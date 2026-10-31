using System.Globalization;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>表示の地域設定を試験内で指定し、終了時に元の設定へ戻します。</summary>
internal sealed class ClientCultureScope : IDisposable
{
    private readonly CultureInfo previous = CultureInfo.CurrentCulture;

    /// <summary>呼出元の実行コンテキストだけに地域設定を適用します。</summary>
    internal ClientCultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

    /// <summary>次の試験へ地域設定を持ち越しません。</summary>
    public void Dispose() => CultureInfo.CurrentCulture = previous;
}
