namespace ReArtWord.Contents.BackgroundService.Client.Operations.Resources;

/// <summary>空文字や空白を含む一つの引数を、そのままOSへ渡す編集値です。</summary>
internal sealed record ApplicationArgument(string Initial)
{
    internal Guid EditorId
    {
        get;
    } = Guid.NewGuid();

    internal string Value
    {
        get;
        set;
    } = Initial;
}
