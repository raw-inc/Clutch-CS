using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Http;
using ReArtWord.Contents.BackgroundService.SignalCapture.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Packets;

/// <summary>キャプチャの原本と、上位プロトコルを解析するための情報です。</summary>
internal sealed class PacketRecord
{
    // 抑制理由: 受信時には未完成なので解析後に関連付けます。非 HTTP パケットで List を確保しないための借用参照です。
    [SuppressMessage("Design", "RAW170", Justification = "受信後の TCP 再構成で完成したメッセージ群を関連付けるため、参照の再代入が必要です。非 HTTP パケットごとの空 List 確保を避けます。")]
    private IReadOnlyList<HttpMessage> httpMessages = Array.Empty<HttpMessage>();

    /// <summary>別アダプタやファイルの通信を混ぜないための取得元識別子。</summary>
    internal string CaptureInterface
    {
        get;
        set;
    } = "";

    /// <summary>最後のクリア以降は再利用しない、1 からの通し番号。</summary>
    internal long Index
    {
        get;
        set;
    }

    /// <summary>キャプチャ時刻。</summary>
    internal DateTimeOffset Time
    {
        get;
        init;
    }

    /// <summary>pcap のリンク種別。</summary>
    internal int LinkType
    {
        get;
        init;
    }

    /// <summary>キャプチャした原本。</summary>
    internal required byte[] Data
    {
        get;
        init;
    }

    /// <summary>回線上の元の長さ。</summary>
    internal int OriginalLength
    {
        get;
        init;
    }

    /// <summary>キャプチャ機構が報告した観測点と方向。</summary>
    internal string Observation
    {
        get;
        init;
    } = "";

    /// <summary>Group 等の毎回変わる値を含まない、取得元内の安定した観測点。</summary>
    internal string ObservationPoint
    {
        get;
        init;
    } = "";

    /// <summary>検証できた IP ヘッダーのアドレス族。ARP・未知・ヘッダー不正は未指定です。</summary>
    internal AddressFamily NetworkFamily
    {
        get;
        set;
    }

    /// <summary>送信元 IP または MAC。</summary>
    internal string Source
    {
        get;
        set;
    } = "-";

    /// <summary>宛先 IP または MAC。</summary>
    internal string Destination
    {
        get;
        set;
    } = "-";

    /// <summary>送信元ポート。</summary>
    internal int SourcePort
    {
        get;
        set;
    }

    /// <summary>宛先ポート。</summary>
    internal int DestinationPort
    {
        get;
        set;
    }

    /// <summary>通信内容を表すプロトコル。</summary>
    internal string Protocol
    {
        get;
        set;
    } = "Unknown";

    /// <summary>トランスポート。</summary>
    internal string Transport
    {
        get;
        set;
    } = "";

    /// <summary>Art-Net などが内包する通信規格。</summary>
    internal string ContentProtocol
    {
        get;
        set;
    } = "";

    /// <summary>一覧用に長さを制限した値。原本の保存・検索には使用しません。</summary>
    internal string Value
    {
        get;
        set;
    } = "";

    /// <summary>一覧の内容。</summary>
    internal string Summary
    {
        get;
        set;
    } = "Unparsed";

    /// <summary>詳細ペインの解析結果。</summary>
    internal string Details
    {
        get;
        set;
    } = "";

    /// <summary>OSC Bundle 内を含む Path の集合表現。</summary>
    internal string Paths
    {
        get;
        set;
    } = "";

    /// <summary>上位層データの開始位置。</summary>
    internal int PayloadOffset
    {
        get;
        set;
    }

    /// <summary>上位層データの長さ。</summary>
    internal int PayloadLength
    {
        get;
        set;
    }

    /// <summary>TCP シーケンス番号。</summary>
    internal uint Sequence
    {
        get;
        set;
    }

    /// <summary>TCP フラグ。</summary>
    internal byte TcpFlags
    {
        get;
        set;
    }

    /// <summary>完全に再構成できた HTTP メッセージ。</summary>
    internal HttpMessage? Http
    {
        get;
        set;
    }

    /// <summary>言語切替時に本文を再解析せず、同一フレーム上の全 HTTP 表示を再構築する原本。</summary>
    internal IReadOnlyList<HttpMessage> HttpMessages => httpMessages;

    /// <summary>完成済みの HTTP 群と、再送対象の最終メッセージを関連付けます。</summary>
    internal void SetHttpMessages(IReadOnlyList<HttpMessage> messages)
    {
        httpMessages = messages;
        Http = messages[^1];
        Value = ValuePreview.Hex(Http.Body);
    }

    /// <summary>表示用の一行を生成します。</summary>
    /// <returns>時刻と送受信先を含む一覧行。</returns>
    public override string ToString()
    {
        return $"{Index, 6} {Protocol, -8} {Time.ToLocalTime():HH:mm:ss.fff} {Source}:{SourcePort} > {Destination}:{DestinationPort}  {UiText.Summary(Summary, Protocol)}  Value: {Value}";
    }
}
