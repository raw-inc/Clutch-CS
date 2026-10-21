using System.Text;
using ReArtWord.Contents.BackgroundService.SignalCapture.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Osc;

/// <summary>OSC の解析中に原文のパスと有界な引数プレビューを収集します。</summary>
internal sealed class OscPreview
{
    private readonly bool bundle;
    private readonly StringBuilder paths = new();
    private readonly ValuePreview values = new();
    private bool firstValue;

    /// <summary>Bundle では各値とメッセージの対応も表示します。</summary>
    internal OscPreview(bool bundle)
    {
        this.bundle = bundle;
    }

    /// <summary>フィルター用の原文パス。</summary>
    internal string Paths => paths.ToString();

    /// <summary>型付き引数の一覧値。</summary>
    internal string Value => values.ToString();

    /// <summary>表示文字列の再解析をせず、実際のアドレスだけを収集します。</summary>
    internal void BeginMessage(string address, bool hasArguments)
    {
        if (paths.Length != 0)
        {
            paths.Append('\n');
            values.Append("; ");
        }

        paths.Append(address);
        firstValue = true;
        if (bundle)
        {
            values.Append(address + ": ");
        }

        if (!hasArguments)
        {
            values.Append("()");
        }
    }

    /// <summary>値を検証した後でプレビューへ追加します。</summary>
    internal void AddValue(char tag, string value)
    {
        if (!firstValue)
        {
            values.Append(", ");
        }

        firstValue = false;
        values.Append($"{tag}=");
        if (tag is 's' or 'S')
        {
            values.Append("\"");
            values.Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal));
            values.Append("\"");
        }
        else
        {
            values.Append(value);
        }
    }

    /// <summary>値のプレビューを返します。</summary>
    /// <returns>型付きの値。</returns>
    public override string ToString() => Value;
}
