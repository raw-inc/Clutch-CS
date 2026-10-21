namespace ReArtWord.Contents.BackgroundService.SignalCapture.Text;

/// <summary>英語を既定として、アプリケーションの表示文言だけを切り替えます。</summary>
internal static class UiText
{
    private static int japanese;
    private static readonly Dictionary<string, string> translations = new(StringComparer.Ordinal)
    {
        ["Adapter"] = "アダプタ",
        ["Demo"] = "デモ",
        ["Capture backend"] = "キャプチャ機構",
        ["Cancel"] = "キャンセル",
        ["Close"] = "閉じる",
        ["Apply"] = "適用",
        ["Clear all"] = "すべて解除",
        ["~Alt+F~ Filter bytes"] = "~Alt+F~ 選択で絞込",
        ["~Shift+Alt+F~ Clear filters"] = "~Shift+Alt+F~ 絞込解除",
        ["Display filters cleared."] = "表示フィルターを解除しました。",
        ["Capture cleared. Next packet: Idx 1."] = "キャプチャをクリアしました。次のパケットは Idx 1 です。",
        ["Select bytes in Hex / ASCII first."] = "先に Hex / ASCII で byte を選択してください。",
        ["Selected bytes applied to the Hex filter."] = "選択した byte を Hex フィルターに適用しました。",
        ["Select bytes / Shift: range / Alt+F: filter"] = "byte 選択 / Shift: 範囲 / Alt+F: 絞込",
        ["First 4096 bytes"] = "先頭 4096 bytes",
        ["IP version (Left/Right, Space: select)"] = "IP バージョン (左右キー、Space: 選択)",
        ["All"] = "すべて",
        ["v4 only"] = "v4 のみ",
        ["v6 only"] = "v6 のみ",
        ["Time / Zone"] = "時刻 / 地域",
        ["OK"] = "決定",
        ["Language"] = "言語",
        ["Japanese"] = "日本語",
        ["Select the display language"] = "表示言語を選択してください",
        ["Display language: English"] = "表示言語: 日本語",
        ["Stop capture with T before changing adapters."] = "アダプタ変更前に T で停止してください。",
        ["The demo does not use the network. Start normally to select an adapter."] = "デモは実通信を行いません。通常起動でアダプタを選択できます。",
        ["Packet Monitor: no additional installation / Ethernet and vEthernet / administrator required\nNpcap: uses an installed driver / loopback and Ethernet injection"] = "Packet Monitor: 追加インストール不要 / Ethernet・vEthernet 対応 / 管理者権限\nNpcap: 既に導入されたドライバーを使用 / ループバック・Ethernet 注入",
        ["No capture adapters are available."] = "取得可能なアダプタがありません。",
        ["Up / Wheel: browse; End / PageDown: follow"] = "↑ / Wheel で閲覧、End / PageDown で追従",
        ["Stopped. R: Dump / W: Send"] = "停止しました。R: Dump / W: Send",
        ["Dump pcapng"] = "pcapng 保存",
        ["Save all retained packets, including those hidden by the display filter"] = "保持中の全パケットを保存（表示フィルター対象外も含む）",
        ["Stop capture with T before opening a file."] = "T で停止してから読み込んでください。",
        ["Open"] = "開く",
        ["Open pcap / pcapng"] = "pcap / pcapng を開く",
        ["Path to the capture file"] = "読み込むファイルのパス",
        ["Read the first 100000 packets (retention: {0} packets / {1} MiB)."] = "先頭 100000 件まで読み込みました（保持上限 {0} 件 / {1} MiB）。",
        ["Operation failed"] = "操作できませんでした",
        ["Display filter / blank = no condition / all conditions AND"] = "表示フィルター / 空欄は条件なし / 全条件 AND",
        ["Protocol (= OSC / Art-Net / HTTP / UDP / TCP)"] = "プロトコル (= OSC / Art-Net / HTTP / UDP / TCP)",
        ["Protocol (OSC / Art-Net / DMX512 / ARP / ICMP / DNS ...)"] = "プロトコル (OSC / Art-Net / DMX512 / ARP / ICMP / DNS ...)",
        ["Source IP (substring)"] = "送信元 IP (部分一致)",
        ["Destination IP (substring)"] = "宛先 IP (部分一致)",
        ["OSC / HTTP Path (substring, case-sensitive)"] = "OSC / HTTP パス (部分一致・大小区別)",
        ["Packet text (UTF-8 substring, case-sensitive)"] = "パケット文字列 (UTF-8 部分一致・大小区別)",
        ["Packet Hex (example: 41 72 74 2D 4E 65 74)"] = "パケット Hex (例: 41 72 74 2D 4E 65 74)",
        ["Hex filter"] = "Hex フィルター",
        ["Compose / Send - one transmission"] = "作成 / 送信 — 一回送信",
        ["UDP: space-separated Hex / OSC: i 1, f 0.5, s hello on separate lines / ArtDmx: space-separated 0-255"] = "UDP: 空白区切り Hex / OSC: i 1、f 0.5、s hello を一行ずつ / ArtDmx: 0～255 を空白区切り",
        ["No Npcap/libpcap adapter selected"] = "Npcap/libpcap アダプタ未選択",
        ["OSC: i 1 / f 0.5 / s hello / b 0102 on separate lines. T/F/N/I have no value."] = "OSC: i 1 / f 0.5 / s hello / b 0102 を一行ずつ。T/F/N/I は値なし。",
        ["ArtDmx: Port-Address 0-32767 / channel values 0-255, an even count from 2 to 512."] = "ArtDmx: Port-Address 0～32767 / チャンネル値 0～255、2～512 個の偶数。",
        ["HTTP: Target URL sets the Path/Query. Specify Content-Length in UTF-8 bytes."] = "HTTP: 送信先 Path/Query は Target URL。本文は Content-Length (UTF-8 bytes) を指定。",
        ["Ethernet: Target is the injection adapter. The first 6 Hex bytes are the destination MAC. Edit all headers yourself."] = "Ethernet: Target は注入アダプタ。宛先 MAC は Hex の先頭 6 bytes。全ヘッダーを自分で編集。",
        ["UDP: space-separated Hex. Leave Local IP blank to use the OS route."] = "UDP: 空白区切り Hex。Local IP 空欄は OS が送信経路を選択。",
        ["Select a Npcap/libpcap Ethernet adapter for frame injection."] = "Ethernet 注入には Npcap/libpcap の Ethernet アダプタを選択してください。",
        ["Ethernet frames require at least 14 bytes."] = "Ethernet は 14 bytes 以上です。",
        ["Target"] = "送信先",
        ["Port (UDP)"] = "ポート (UDP)",
        ["Local IP"] = "送信元 IP",
        ["Path / Universe"] = "パス / Universe",
        ["Preview / Send"] = "確認 / 送信",
        ["Confirm transmission"] = "送信内容の確認",
        ["Send once"] = "一回送信",
        ["Send result"] = "送信結果",
        ["Input error"] = "入力エラー",
        ["Frame injected. Verify delivery with capture."] = "フレームを注入しました。相手への到達はキャプチャで確認してください。",
        ["Send failed / cancelled: "] = "送信失敗 / 中断: ",
        ["DEMO / no network"] = "デモ / 通信なし",
        ["DEMO / no real traffic"] = "デモ / 実通信なし",
        ["The capture backend stopped. "] = "バックエンドの取得が停止しました。 ",
        ["BROWSING / End to follow"] = "閲覧中 / End で追従",
        ["CAPTURING"] = "取得中",
        ["STOPPED"] = "停止中",
        ["FOLLOW"] = "自動追従",
        ["The selected packet was evicted. Showing the oldest retained packet."] = "選択パケットは保持上限外です。保持中の先頭を表示しています。",
        ["Adapter details / Enter to select"] = "アダプタ詳細 / Enter で選択",
        ["Network summary"] = "ネットワークサマリ",
        ["Network summary / FOLLOW"] = "ネットワークサマリ / 自動追従",
        ["Packet details / browsing (capture continues)"] = "パケット詳細 / 閲覧中（取得は継続）",
        ["Packet details / browsing"] = "パケット詳細 / 閲覧中",
        ["No packets."] = "パケットがありません。",
        ["Select Adapter / Up Down Click / Enter: Select / Esc: Cancel"] = "アダプタ選択 / ↑ ↓ クリック / Enter: 決定 / Esc: 戻る",
        ["Selected: "] = "選択済み: ",
        ["\n\nLocal interfaces:\n"] = "\n\nローカルインターフェース:\n",
        ["E: Select Adapter / N: Compose / O: Open / Q: Help / L: Language"] = "E: アダプタ選択 / N: 作成 / O: 開く / Q: ヘルプ / L: 言語",
        ["Packets  Idx / Protocol / Time / Source > Destination / Info"] = "パケット  Idx / プロトコル / 時刻 / 送信元 > 宛先 / 情報",
        ["Hex / ASCII - captured frame"] = "Hex / ASCII — 取得フレーム",
        ["SignalScope Help"] = "SignalScope ヘルプ",
        ["~E~ Adapter"] = "~E~ アダプタ",
        ["~S~ Start"] = "~S~ 開始",
        ["~T~ Stop"] = "~T~ 停止",
        ["~C~ Clear"] = "~C~ クリア",
        ["~R~ Dump"] = "~R~ 保存",
        ["~Q~ Help"] = "~Q~ ヘルプ",
        ["~F~ Filter"] = "~F~ 絞込",
        ["~W~ Send"] = "~W~ 送信",
        ["~N~ New"] = "~N~ 作成",
        ["~O~ Open"] = "~O~ 開く",
        ["~H~ Hex"] = "~H~ Hex",
        ["~L~ Language"] = "~L~ 言語",
        ["~^Q~ Exit"] = "~^Q~ 終了",
        ["Display limited to 4096 bytes. Dump saves all retained original data."] = "表示は 4096 bytes まで。Dump には保持中の原本を保存します。",
        ["[Binary body: send without editing to preserve the original]"] = "[バイナリ本文: 編集せず送信すると原本を保持します]",
        ["Binary HTTP bodies cannot be edited as text. Send the original body unchanged."] = "バイナリ HTTP 本文のテキスト編集はできません。原本のまま送信してください。",
        ["Separate HTTP headers and body with a blank line."] = "HTTP ヘッダーと本文の間に空行が必要です。",
        ["Provide one HTTP request with a correct Content-Length in UTF-8 bytes."] = "HTTP リクエスト一件と正しい Content-Length (UTF-8 bytes) を指定してください。",
        ["Specify an explicit URL to resend this HTTP request-target."] = "この HTTP request-target の再送には明示的な URL を指定してください。",
        ["pcapng timestamp is outside the supported range."] = "pcapng の時刻が対応範囲外です。",
        ["pcapng interface limit exceeded."] = "pcapng インターフェース数の上限です。",
        ["HTTP headers are limited to 64 KiB."] = "HTTP header は 64 KiB までです。",
        ["Not an HTTP/1 start line."] = "HTTP/1 開始行ではありません。",
        ["Duplicate HTTP Transfer-Encoding."] = "HTTP Transfer-Encoding が重複しています。",
        ["Duplicate or conflicting HTTP length declarations."] = "HTTP 長さ指定が重複・競合しています。",
        ["Unsupported Transfer-Encoding"] = "未対応 Transfer-Encoding",
        ["HTTP Content-Length must be between 0 and 1 MiB."] = "HTTP Content-Length は 0～1 MiB です。",
        ["HTTP bodies are limited to 1 MiB."] = "HTTP 本文は 1 MiB までです。",
        ["Invalid packet length, link type, timestamp, or comment for capture export."] = "保存するパケットの長さ・リンク種別・時刻・コメントが不正です。",
        ["Specify a pcap or pcapng file."] = "pcap / pcapng ファイルを指定してください。",
        ["\nThe captured length is shorter than the original; data is missing."] = "\nキャプチャ長が元の長さより短いため、欠落があります。",
        ["Incomplete / malformed: "] = "不完全 / 不正: ",
        ["IPv4 header length"] = "IPv4 ヘッダー長",
        ["IP fragment (reassembly unsupported)"] = "IP fragment（再構成対象外）",
        ["Too many IPv6 extension headers"] = "IPv6 拡張ヘッダーが多すぎます",
        ["IPv6 fragment (reassembly unsupported)"] = "IPv6 fragment（再構成対象外）",
        ["Packet is too short"] = "パケット長が不足しています",
        ["Packet Monitor returned an out-of-range data descriptor."] = "Packet Monitor が範囲外のデータを返しました。",
        ["Packet Monitor metadata overlaps the packet data."] = "Packet Monitor のメタデータとパケット領域が重なっています。",
        ["Hex data is limited to 65535 bytes."] = "Hex は 65535 bytes までです。",
        ["Port-Address must be between 0 and 32767."] = "Port-Address は 0～32767 です。",
        ["Use an even channel count from 2 to 512, with values from 0 to 255."] = "チャンネル数は 2～512 の偶数です。各値は 0～255 です。",
        ["\nHTTP is reassembled TCP data. Hex shows the original frame."] = "\nHTTP は TCP 再構成結果。Hex はこのフレームの原本です。",
        [" / awaiting HTTP boundary or unidentified data"] = " / HTTP 境界待ち・未識別データ",
        ["\nHTTP reassembly unavailable: "] = "\nHTTP 再構成対象外: ",
        ["Cannot decode: "] = "解析不可: ",
        ["Sequence 0 = no sequence checking / Channels are 1-based"] = "Sequence 0 = 順序検出なし / Channel は 1-based",
        ["OSC Bundle nesting limit exceeded"] = "OSC Bundle の深さが上限を超えました",
        ["OSC addresses must start with /"] = "OSC address は / から始まります",
        ["Missing OSC type tag"] = "OSC type tag がありません",
        ["OSC type tags do not match the data length"] = "OSC の型情報とデータ長が一致しません",
        ["OSC paths must start with / and must not contain NUL."] = "OSC Path は / から開始し、NUL を含めないでください。",
        ["T/F/N/I do not take a value."] = "T/F/N/I は値を持ちません。",
        ["Supported compose types: i / f / s / b / T / F / N / I"] = "作成対応型: i / f / s / b / T / F / N / I",
        ["Strings must not contain NUL."] = "文字列に NUL は使用できません。",
        ["Invalid Packet Monitor data-source list size."] = "Packet Monitor のデータソース一覧サイズが不正です。",
        ["Adapter configuration changed. Select an adapter again."] = "アダプタ構成が変化しました。再選択してください。",
        ["The selected adapter is no longer available. Select an adapter again."] = "選択したアダプタがなくなりました。再選択してください。",
        ["TCP retransmission conflicts with previously received data."] = "TCP の再送データが既存の内容と競合しています。",
        ["TCP reassembly is limited to 1 MiB."] = "TCP 再構成は 1 MiB までです。",
        ["TCP out-of-order reassembly limit reached."] = "TCP 再構成の欠落待ち上限に達しました。",
        ["Conflicting out-of-order TCP segments."] = "TCP の欠落待ちセグメント同士が競合しています。",
        ["libpcap capture terminated unexpectedly."] = "libpcap のキャプチャが異常終了しました。",
        ["Ethernet frame length must be between 14 and 65535 bytes."] = "Ethernet フレーム長は 14～65535 bytes です。",
        ["Select an adapter with an Ethernet link type for frame injection."] = "Ethernet フレームの注入先には Ethernet リンク種別のアダプタを指定してください。",
        ["An HTTP/1.0 or HTTP/1.1 start line is required."] = "HTTP/1.0 または HTTP/1.1 の開始行が必要です。",
        ["Control character in HTTP start line"] = "HTTP 開始行の制御文字",
        ["Invalid HTTP header."] = "HTTP header が不正です。",
        ["Invalid Packet Monitor data-source list."] = "Packet Monitor のデータソース一覧が不正です。",
        ["Invalid Packet Monitor data-source count."] = "Packet Monitor のデータソース件数が不正です。",
        ["Packet Monitor data-source reference is outside the valid buffer."] = "Packet Monitor のデータソース参照が有効範囲外です。",
        ["Packet Monitor / Ethernet and IP / maximum 9000 bytes\n"] = "Packet Monitor / Ethernet・IP / 最大 9000 bytes\n",
        ["Specify a destination IP, port 1-65535, and UDP data no larger than 65507 bytes."] = "宛先 IP、ポート 1～65535、UDP 長 65507 bytes 以下を指定してください。",
        ["Send an HTTP request to an http:// or https:// target URL."] = "送信対象は HTTP リクエストです。http:// または https:// の宛先 URL を指定してください。",
        ["Unparsed"] = "未解析",
        ["Idx"] = "Idx",
        ["Time"] = "時刻",
        ["Source"] = "送信元",
        ["Destination"] = "宛先",
        ["Info"] = "情報",
        ["Value"] = "値",
        ["Packets"] = "パケット",
        ["Type names: "] = "型名: ",
        ["Encapsulated protocol: "] = "内包プロトコル: ",
        ["none (control message)"] = "なし（制御メッセージ）",
        ["not decoded"] = "未解析",
        ["Saved: {0} packets / {1}"] = "保存: {0} パケット / {1}",
        ["Opened: {0} packets / {1}"] = "読み込み: {0} パケット / {1}",
        ["Target: {0}:{1}\nLocal: {2} (blank = OS route)\n{3} bytes / once\n"] = "送信先: {0}:{1}\n送信元: {2} (空欄は OS 経路)\n{3} bytes / 一回\n",
        ["Adapter: {0}\nDestination MAC: {1}\n{2} bytes / once\n"] = "アダプタ: {0}\n宛先 MAC: {1}\n{2} bytes / 一回\n",
        ["Sent {0} UDP bytes. Verify delivery with capture."] = "UDP {0} bytes を送信しました。到達はキャプチャで確認してください。",
        ["Adapter: {0}\nReceived: {1} packets / {2} bytes\nRetained: {3} (after display filter)\nEvicted: {4}\nUI queue dropped: {5}\nCapture dropped: {6}\nCapture scope excluded: {7}\n\n"] = "アダプタ: {0}\n受信: {1} パケット / {2} bytes\n保持: {3} (表示フィルター適用後)\n保持上限による除外: {4}\nUI キュー欠落: {5}\nキャプチャ欠落: {6}\n取得範囲による除外: {7}\n\n",
        ["Virtual NIC: only NIC Rx/Tx observations are retained. Switch and drop observations are excluded. Use Npcap if VLAN isolation is required."] = "仮想 NIC: NIC 自身の Rx/Tx のみ保持します。スイッチ内と破棄通知は除外します。VLAN 分離が必要な場合は Npcap を使用してください。",
        ["The selected Packet Monitor adapter has no usable component ID. Select Npcap instead."] = "選択した Packet Monitor アダプタに利用可能な観測点 ID がありません。Npcap を選択してください。",
        ["\n\nUp / Wheel / Click: browse\nEnd / PageDown: resume following\nT: stop capture\nH: details/Hex on narrow screens"] = "\n\n↑ / ホイール / クリック: 閲覧へ\nEnd / PageDown: 追従再開\nT: キャプチャ停止\nH: 狭い画面の詳細/Hex切替",
        ["Idx {0} / {1:O}\n{2}:{3} -> {4}:{5}\n{6}\nCaptured {7} / Original {8} bytes\n\n{9}"] = "Idx {0} / {1:O}\n{2}:{3} → {4}:{5}\n{6}\n取得 {7} / 原本 {8} bytes\n\n{9}",
        ["Hex / ASCII - frame #{0}"] = "Hex / ASCII — フレーム #{0}",
        ["Path: "] = "パス: ",
        ["Types: "] = "型タグ: ",
        ["Endpoint: "] = "エンドポイント: ",
        ["Query (raw): "] = "クエリ (原文): ",
        ["\nBody: {0} bytes"] = "\n本文: {0} bytes",
        ["Unsupported link type "] = "未対応リンク種別 ",
        ["Unsupported OSC type "] = "未対応 OSC 型 ",
        ["Payload: "] = "ペイロード: ",
        ["TCP flags: "] = "TCP フラグ: ",
        ["Bundle / Timetag: 0x{0:X16} (1 = immediate)"] = "Bundle / 時刻タグ: 0x{0:X16} (1 = 即時)",
        ["Field decoding for this OpCode is not supported. Inspect the Hex data."] = "この OpCode のフィールド解析は未対応です。Hex で確認できます。",
    };
    private static readonly string[] diagnosticPrefixes =
    {
        "Incomplete / malformed: ",
        "Cannot decode: ",
        "HTTP reassembly unavailable: ",
        "Unsupported link type ",
        "Unsupported OSC type ",
        "Payload: ",
        "TCP flags: "
    };
    private static string waitingSuffix => " / awaiting HTTP boundary or unidentified data";
    private static string pathPrefix => "Path: ";
    private static string httpProtocol => "HTTP";
    private static string oscProtocol => "OSC";
    private static string newLine => "\n";

    /// <summary>今回の起動中に選択している言語。初期値は英語です。</summary>
    internal static bool Japanese
    {
        get => Volatile.Read(ref japanese) != 0;
        set => Volatile.Write(ref japanese, value ? 1
                                                  : 0);
    }

    /// <summary>既知のアプリ文言を翻訳します。受信データ全体には使用しません。</summary>
    /// <returns>選択言語の文言。</returns>
    internal static string Get(string english) => Japanese && translations.TryGetValue(english, out string? translated) ? translated
                                                                                                                        : english;

    /// <summary>数値やパスを原文のまま翻訳済みテンプレートへ差し込みます。</summary>
    /// <returns>整形済み文言。</returns>
    internal static string Format(string english, params object? [] values) => string.Format(CultureInfo.InvariantCulture, Get(english), values);

    /// <summary>本文を含まないアプリ生成の診断だけを翻訳します。</summary>
    /// <returns>翻訳した診断。</returns>
    internal static string Diagnostic(string english)
    {
        if (!Japanese)
        {
            return english;
        }

        foreach (string prefix in diagnosticPrefixes)
        {
            if (english.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Get(prefix) + Get(english[prefix.Length..]);
            }
        }

        if (translations.TryGetValue(newLine + english, out string? line))
        {
            return line.TrimStart('\n');
        }

        return Get(english);
    }

    /// <summary>パケットの原文は維持し、一覧の説明ラベルだけを翻訳します。</summary>
    /// <returns>現在言語の一覧概要。</returns>
    internal static string Summary(string text, string protocol)
    {
        if (!Japanese || string.Equals(protocol, httpProtocol, StringComparison.Ordinal))
        {
            return text;
        }

        if (string.Equals(protocol, oscProtocol, StringComparison.Ordinal) && text.StartsWith(pathPrefix, StringComparison.Ordinal))
        {
            return Get(pathPrefix) + text[pathPrefix.Length..];
        }

        if (text.EndsWith(waitingSuffix, StringComparison.Ordinal))
        {
            return text[..^waitingSuffix.Length] + Get(waitingSuffix);
        }

        return Diagnostic(text);
    }
}
