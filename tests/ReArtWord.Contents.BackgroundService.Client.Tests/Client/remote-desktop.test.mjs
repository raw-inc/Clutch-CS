import { test } from 'node:test';
import assert from 'node:assert/strict';

const { desktopView } = await import(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop.js', import.meta.url));

/** 画面要素の状態とイベントを試験用に再現します。 */
class Element
{
    /** 試験で操作する状態とブラウザー境界を初期化します。 */
    constructor(tag)
    {
        this.tag = tag;
        this.children = [];
        this.value = '';
        this.dataset = {};
        this.style = {};
    }

    /** 受け取った項目を保持する順序へ追加します。 */
    append(...items)
    {
        for (const item of items)
        {
            if (item.parentElement)
            {
                item.parentElement.children = item.parentElement.children.filter(value => value !== item);
            }
            item.parentElement = this;
            this.children.push(item);
        }
    }

    /** 現在の子要素を指定された内容へ置き換えます。 */
    replaceChildren(...items)
    {
        this.textContent = '';
        this.append(...items);
    }

    /** 属性の値を試験用DOMへ記録します。 */
    setAttribute(name, value)
    {
        (this.attributes ??= {})[name] = value;
    }

    /** 試験用DOMに保持された属性値を読み取ります。
     * @returns 呼出し元が使用する値です。
     */
    getAttribute(name)
    {
        return this.attributes?.[name];
    }

    /** 試験用DOMの文字列内容を読み書きします。 */
    set textContent(value)
    {
        this.text = value;
        for (const item of this.children)
        {
            item.parentElement = null;
        }
        this.children = [];
    }

    /** 試験用DOMの文字列内容を読み書きします。
     * @returns 呼出し元が使用する値です。
     */
    get textContent()
    {
        return (this.text ?? '') + this.children.map(item => item.textContent).join('');
    }

    /** 指定した要素が自分または子孫かを判定します。
     * @returns 呼出し元が使用する値です。
     */
    contains(value)
    {
        return this === value || this.children.some(item => item.contains(value));
    }

    /** 条件に一致する試験用DOM要素を取得します。
     * @returns 呼出し元が使用する値です。
     */
    querySelector(tag)
    {
        return this.children.find(item => item.tag === tag);
    }

    /** 試験で呼び出せるイベントハンドラーを登録します。 */
    addEventListener(name, callback)
    {
        (this.events ??= {})[name] = callback;
    }

    /** 保持している選択候補を返します。
     * @returns 呼出し元が使用する値です。
     */
    get options()
    {
        return this.children;
    }

    /** 条件に一致する試験用DOM要素を列挙します。
     * @returns 呼出し元が使用する値です。
     */
    querySelectorAll()
    {
        return this.children.filter(item => item.dataset.monitor !== undefined);
    }

    /** 試験用canvasの描画境界を返します。
     * @returns 呼出し元が使用する値です。
     */
    getContext()
    {
        this.drawn = [];
        return { drawImage: frame => this.drawn.push(frame.timestamp),

            /** 試験用canvasの消去要求を記録します。 */
            clearRect()
            {},

            /** 試験用canvasの描画経路を開始します。 */
            beginPath()
            {},

            /** 試験用canvasの円弧描画を受け付けます。 */
            arc()
            {},

            /** 試験用canvasの塗りつぶしを受け付けます。 */
            fill()
            {},

            /** 試験用canvasの線描画を受け付けます。 */
            stroke()
            {},

            /** 試験用canvasの描画開始位置を設定します。 */
            moveTo()
            {},

            /** 試験用canvasの線の接続先を設定します。 */
            lineTo()
            {},

            /** 試験用canvasの描画経路を閉じます。 */
            closePath()
            {},

            /** 試験用canvasのグラデーションを作成します。
             * @returns addColorStopを含む結果です。
             */
            createLinearGradient()
            {
                return {
                    /** 試験用グラデーションの色指定を受け付けます。 */
                    addColorStop()
                    {} };
            } };
    }

    /** 試験で指定した画面上の矩形を返します。
     * @returns top・heightを含む結果です。
     */
    getBoundingClientRect()
    {
        return { top: 100, height: 24 };
    }

    /** 試験用要素の表示幅を返します。
     * @returns 呼出し元が使用する値です。
     */
    get clientWidth()
    {
        return 1200;
    }

    /** 試験用要素の表示高さを返します。
     * @returns 呼出し元が使用する値です。
     */
    get clientHeight()
    {
        return 600;
    }

    /** 試験用DOMを全画面表示に切り替えます。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async requestFullscreen()
    {
        document.fullscreenElement = this;
        document.fullscreenChanged();
    }

    /** 操作対象の入力欄へフォーカスを移します。 */
    focus()
    {
        document.activeElement = this;
    }

    /** フォーカス喪失時に保持中の入力状態を解除します。 */
    blur()
    {
        if (document.activeElement === this)
        {
            document.activeElement = null;
        }
    }

    /** 試験用入力欄の選択位置を記録します。 */
    setSelectionRange(start, end)
    {
        this.selectionStart = start;
        this.selectionEnd = end;
    }

    /** 捕捉したポインターの識別子を記録します。 */
    setPointerCapture(id)
    {
        this.capture = id;
    }

    /** 指定したポインターを捕捉中か確認します。
     * @returns 呼出し元が使用する判定結果です。
     */
    hasPointerCapture(id)
    {
        return this.capture === id;
    }

    /** 試験で保持したポインターの捕捉を解除します。 */
    releasePointerCapture()
    {
        this.capture = null;
    }

    /** 試験用canvasの制御をWorkerへ渡す境界を返します。
     * @returns width・heightを含む結果です。
     */
    transferControlToOffscreen()
    {
        return { width: this.width, height: this.height };
    }
}

/** 自分と配下の試験用要素を列挙します。
 * @returns 呼出し元が使用する値です。
 */
const descendants = root => [root, ...root.children.flatMap(descendants)];

/** 試験用のカーソル通知フレームを作成します。
 * @returns 呼出し元が使用する値です。
 */
function cursorFrame(monitor, shape)
{
    const bytes = new Uint8Array(4);
    new DataView(bytes.buffer).setInt32(0, shape);
    return frame(11, monitor, bytes);
}

/** 試験用の通信ヘッダーとペイロードを組み立てます。
 * @returns dataを含む結果です。
 */
function frame(kind, id, data, key = false, timestamp = 0)
{
    const payload = data instanceof Uint8Array ? data : new TextEncoder().encode(JSON.stringify(data));
    const buffer = new ArrayBuffer(24 + payload.length), header = new DataView(buffer);
    header.setInt32(0, kind);
    header.setInt32(4, id);
    header.setInt32(8, payload.length);
    header.setInt32(12, key ? 1 : 0);
    header.setBigInt64(16, BigInt(timestamp));
    new Uint8Array(buffer, 24).set(payload);
    return { data: buffer };
}

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 */
async function fixtureAsync(t, videoCodec = null, settings = {})
{
    const names = ['document', 'window', 'location', 'localStorage', 'WebSocket', 'VideoDecoder', 'EncodedVideoChunk', 'requestAnimationFrame', 'cancelAnimationFrame', 'AudioContext', 'AudioWorkletNode', 'performance', 'setInterval', 'clearInterval', 'ResizeObserver', 'getComputedStyle', 'navigator', 'Worker', 'AudioDecoder', 'EncodedAudioChunk'];
    const before = Object.fromEntries(names.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
    const sockets = [], decoders = [], notices = [], callbacks = [], contexts = [], audioNodes = [], timers = [], documentEvents = {}, windowEvents = {};

    /** イベントの購読と対応する後処理を登録します。 */
    const listen = (events, type, action) =>
    {
        const before = events[type];
        events[type] = event =>
        {
            before?.(event);
            action(event);
        };
    };

    Object.defineProperty(globalThis, 'navigator', { value: {}, configurable: true });
    Object.defineProperty(globalThis, 'localStorage', { value: { getItem: key => key === 'contents.ui.experimental' && settings.alwaysExperimental ? 'true' : null }, configurable: true });
    if (settings.chromium)
    {
        navigator.userAgent = 'Chrome/140';
    }
    const workers = [];
    if (settings.worker)
    {
        navigator.userAgent = 'Firefox/142';
        globalThis.Worker = class
        {
            /** 試験で操作する状態とブラウザー境界を初期化します。 */
            constructor()
            {
                this.sent = [];
                workers.push(this);
            }

            /** 試験対象へのメッセージを記録または配送します。 */
            postMessage(message)
            {
                this.sent.push(message);
                if (message.type === 'close')
                {
                    queueMicrotask(() => this.onmessage({ data: { type: 'closed' } }));
                }
            }

            /** 試験用Workerの終了を記録します。 */
            terminate()
            {
                this.terminated = true;
            }
        };
    }
    let now = 0;

    /** 接続と送受信を試験で制御できるWebSocketです。 */
    class Socket
    {
        static OPEN = 1;

        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(url)
        {
            this.url = url;
            this.readyState = 1;
            this.bufferedAmount = 0;
            this.sent = [];
            sockets.push(this);
        }

        /** 固定された通信形式で入力を送信します。 */
        send(bytes)
        {
            this.sent.push(new Int32Array(Array.from({ length: 6 }, (_, i) => new DataView(bytes).getInt32(i * 4))));
        }

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {
            this.readyState = 3;
        }
    }

    /** 復号処理の開始と出力を試験用に再現します。 */
    class Decoder
    {
        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(callbacks)
        {
            this.callbacks = callbacks;
            this.state = 'unconfigured';
            this.decodeQueueSize = 0;
            this.decoded = [];
            decoders.push(this);
        }

        /** 現在の入力に対応する処理設定を適用します。 */
        configure(options)
        {
            this.configuration = options;
            this.state = 'configured';
        }

        /** 試験用の復号要求を保持して出力を制御します。 */
        decode(chunk)
        {
            this.decoded.push(chunk);
        }

        /** 一時状態を初期値へ戻します。 */
        reset()
        {
            this.state = 'unconfigured';
            this.decodeQueueSize = 0;
        }

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {
            this.state = 'closed';
        }
    }

    /** 音声の再生状態と出力先を試験用に再現します。 */
    class Context
    {
        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor()
        {
            contexts.push(this);
            this.gains = [];
            this.state = 'running';
            this.currentTime = 0;
            this.baseLatency = 0.01;
            this.outputLatency = 0.01;
            this.audioWorklet = {
                /** 試験対象のAudioWorkletモジュールを登録します。
                 * @returns 処理の完了と結果を伝えるPromiseです。
                 */
                async addModule()
                {} };
        }

        /** 一時停止している処理を再開します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async resume()
        {}

        /** 試験用音声の音量操作を構成します。
         * @returns gain・connect・disconnectを含む結果です。
         */
        createGain()
        {
            const values = this.gains;
            return { gain: {
                /** 試験用音声の時刻付き設定を記録します。 */
                setValueAtTime(value)
                {
                    values.push(value);
                } },

            /** 接続対象と応答を確認して通信を開始します。 */
            connect()
            {},

            /** 接続と描画を終了し、保留中の解放処理を保持します。 */
            disconnect()
            {} };
        }

        /** 対象を閉じ、関連する状態を整理します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async close()
        {
            this.state = 'closed';
        }
    }

    /** 音声処理とのメッセージ交換を試験用に再現します。 */
    class Worklet
    {
        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(context, name, options)
        {
            this.configuration = options;
            this.messages = [];
            audioNodes.push(this);
            this.port = { postMessage: value => this.messages.push(value),

                /** 対象を閉じ、関連する状態を整理します。 */
                close()
                {} };
        }

        /** 接続対象と応答を確認して通信を開始します。 */
        connect()
        {}

        /** 接続と描画を終了し、保留中の解放処理を保持します。 */
        disconnect()
        {}
    }

    Object.assign(globalThis, {
        AudioContext: Context, AudioWorkletNode: Worklet,
        AudioDecoder: class
        {
            /** 試験で指定したデコーダー構成の対応状況を返します。
             * @returns 処理の完了と結果を伝えるPromiseです。
             */
            static async isConfigSupported()
            {
                return { supported: settings.opusSupported !== false };
            }

            /** 現在の入力に対応する処理設定を適用します。 */
            configure()
            {
                this.state = 'configured';
            }

            /** 試験用の復号要求を保持して出力を制御します。 */
            decode()
            {}

            /** 対象を閉じ、関連する状態を整理します。 */
            close()
            {
                this.state = 'closed';
            }
        },
        EncodedAudioChunk: class
        {
            /** 試験で操作する状態とブラウザー境界を初期化します。 */
            constructor(value)
            {
                Object.assign(this, value);
            }
        },
        document: { createElement: tag => new Element(tag),

            /** 条件に一致する試験用DOM要素を取得します。
             * @returns 呼出し元が使用する値です。
             */
            querySelector()
            {
                return null;
            },

            /** 試験で呼び出せるイベントハンドラーを登録します。 */
            addEventListener(type, action)
            {
                listen(documentEvents, type, action);
            },

            hidden: false,

            /** 試験用の全画面表示変更を通知します。 */
            fullscreenChanged()
            {
                documentEvents.fullscreenchange?.();
            },

            /** 試験用DOMの全画面表示を終了します。
             * @returns 処理の完了と結果を伝えるPromiseです。
             */
            async exitFullscreen()
            {
                this.fullscreenElement = null;
                this.fullscreenChanged();
            } },
        window: { innerHeight: 1080, scrollY: 0, VideoDecoder: Decoder, isSecureContext: true,

            /** 試験で呼び出せるイベントハンドラーを登録します。 */
            addEventListener(type, action)
            {
                windowEvents[type] = action;
            } },
        ResizeObserver: class
        {
            /** 試験対象の要素を監視対象として記録します。 */
            observe()
            {}

            /** 接続と描画を終了し、保留中の解放処理を保持します。 */
            disconnect()
            {}
        },

        getComputedStyle: () => ({ columnGap: '6.5px' }),

        performance: { now: () => now },

        setInterval: action =>
        {
            timers.push(action);
            return timers.length;
        },

        clearInterval: id =>
        {
            if (id)
            {
                timers[id - 1] = null;
            }
        },

        location: new URL('http://127.0.0.1:5081/desktop' + (settings.query ?? ((settings.experimental ?? (videoCodec === 'h264' || videoCodec === 'h265')) ? '?experimental=true' : ''))), WebSocket: Socket, VideoDecoder: Decoder,
        EncodedVideoChunk: class
        {
            /** 試験で操作する状態とブラウザー境界を初期化します。 */
            constructor(value)
            {
                Object.assign(this, value);
            }
        },

        requestAnimationFrame: callback =>
        {
            callbacks.push(callback);
            return callbacks.length;
        },

        /** 試験で保留した描画予約を取り消します。 */
        cancelAnimationFrame()
        {}
    });
    const root = new Element('main'), controller = new AbortController();
    const ctx = { signal: controller.signal,

        t: en => en,

        say: text => notices.push(text),

        icon: () => new Element('svg'),

        peers: async () => [{ hostId: 'host', clusterId: 'cluster' }],

        /** 操作の実行と処理中表示を持つボタンを構成します。
         * @returns 呼出し元が使用する値です。
         */
        button(text, action)
        {
            const button = new Element('button');
            button.textContent = text;
            button.onclick = action;
            return button;
        } };
    const view = await desktopView(root, ctx);
    t.after(async () =>
    {
        try
        {
            await view.dispose();
        }
        finally
        {
            controller.abort();
            for (const name of names)
            {
                if (before[name])
                {
                    Object.defineProperty(globalThis, name, before[name]);
                }
                else
                {
                    delete globalThis[name];
                }
            }
        }
    });
    const selects = descendants(root).filter(item => item.tag === 'select');
    const codecSelector = selects.find(item => item.getAttribute('aria-label') === 'Video codec'), hostSelector = selects.find(item => item.getAttribute('aria-label') === 'Host');
    const initialCodec = codecSelector.value;
    const presetSelector = selects.find(item => item.getAttribute('aria-label') === 'Transmission preset');
    const initialPreset = presetSelector.value;
    if (settings.preset)
    {
        presetSelector.value = settings.preset;
        presetSelector.onchange();
    }
    hostSelector.value = 'host';
    if (videoCodec !== null)
    {
        assert.ok(codecSelector.options.some(option => option.value === videoCodec), `Video codec ${videoCodec} is not available`);
        codecSelector.value = videoCodec;
    }
    const fields = descendants(root).filter(item => item.tag === 'input');
    if (settings.fps != null)
    {
        fields[0].value = String(settings.fps);
    }
    if (settings.bitrate != null)
    {
        fields[1].value = String(settings.bitrate);
    }
    if (settings.audioSamples != null)
    {
        fields[2].value = String(settings.audioSamples);
    }
    const audioSelector = selects.find(item => item.getAttribute('aria-label') === 'Audio codec'), audioRate = selects.find(item => item.options.some(option => option.textContent === '64 kbps'));
    const initialAudio = { codec: audioSelector.value, bitrate: audioRate.value };
    if (!settings.defaultAudio)
    {
        audioSelector.value = settings.audioCodec ?? 'f32le';
        audioSelector.onchange();
        if (settings.audioCodec)
        {
            audioRate.value = '64';
        }
    }
    const cursorSync = descendants(root).find(item => item.getAttribute('aria-label') === 'Cursor synchronization');
    const initialCursorSync = cursorSync.getAttribute('aria-pressed');

    /** クリック対象を確認して選択または操作を行います。
     * @returns 呼出し元が使用する値です。
     */
    const click = text => descendants(root).find(item => (item.getAttribute('aria-label') ?? item.textContent) === text && item.tag === 'button').onclick();

    const initialVbr = descendants(root).find(item => item.getAttribute('aria-label') === 'Variable bitrate (VBR)').getAttribute('aria-pressed');
    if (settings.customCodec === false)
    {
        await click('Custom codec settings');
    }
    if (settings.vbr === false)
    {
        await click('Variable bitrate (VBR)');
    }
    if (settings.viewOnly)
    {
        await click('View-only mode');
    }
    if (settings.connect === false)
    {
        return { view, root, click, sockets, notices };
    }
    await click('Connect');
    const socket = sockets[0];
    if (settings.connectionDescription !== false)
    {
        socket.onmessage(frame(7, 0, { clusterId: 'cluster', hostId: 'host', cursorShapes: settings.cursorRelay, codecDetails: settings.codecDetails ?? true, variableBitrate: settings.vbrSupported ?? true, viewOnly: settings.viewOnlySupported, annotations: settings.annotations, timing: settings.timing }));
    }
    socket.onmessage(frame(1, 0, [{ id: 0, name: 'Screen A', left: 0, top: 0, width: 1920, height: 1080, primary: true, mousePresent: settings.mousePresent }, { id: 1, name: 'Screen B', left: 1920, top: 0, width: 1280, height: 720, primary: false, mousePresent: settings.mousePresent }]));

    /** 現在の値を確認用の説明に変換します。 */
    const describe = () =>
    {
        if (!socket.sent.some(input => input[0] === 6))
        {
            return;
        }
        socket.onmessage(frame(3, 0, [{ id: 0, encoder: codecSelector.value === 'vp9' ? 'libvpx-vp9' : codecSelector.value === 'h265' ? 'hevc_nvenc' : 'h264_nvenc', codec: codecSelector.value === 'vp9' ? 1 : codecSelector.value === 'h265' ? 2 : 0, selection: socket.sent.findLast(input => input[0] === 6)[3], heartbeat: true, unicodeInput: settings.unicodeInput !== false, scanCodeInput: settings.scanCodeInput !== false, cursorShapes: settings.cursorCore === true, cursorVisibility: settings.cursorVisibility ?? (settings.cursorCore === true), hideCursor: !!(socket.sent.findLast(input => input[0] === 6)[5] & 0x08000000),
            variableBitrate: !!(socket.sent.findLast(input => input[0] === 6)[5] & 0x10000000), codecDetails: codecEcho(), effectiveKeyFrameInterval: 8, framesPerSecond: Number(fields[0].value), maximumBitrateKbps: codecEcho()?.quality != null ? null : Number(fields[1].value), administratorInput: true }]));
    };

    /** 試験用にコーデック設定の応答を組み立てます。
     * @returns 呼出し元が使用する値です。
     */
    function codecEcho()
    {
        const value = socket.sent.findLast(input => input[0] === 9);
        return value?.[1] === 1 ? { maximumKeyFrameInterval: value[2], maximumPFrames: value[3] === -1 ? null : value[3], maximumBFrames: value[4], quality: value[5] === -1 ? null : value[5] } : null;
    }

    describe();
    if (settings.cursorEnabled)
    {
        cursorSync.onclick();
        describe();
    }
    return { view, root, socket, sockets, decoders, notices, click, callbacks, contexts, audioNodes, describe, codecEcho, documentEvents, windowEvents, initialCodec, initialAudio, initialCursorSync, initialVbr, initialPreset, presetSelector, cursorSync, workers,

        /** 試験の時計または待機処理を進めます。 */
        advance(ms)
        {
            now += ms;
            for (const timer of timers)
            {
                timer?.();
            }
        } };
}

test('viewers publish annotations without input and ordinary remote desktops receive them', async t =>
{
    await t.test('view-only sender', async t =>
    {
        const f = await fixtureAsync(t, 'h264', { experimental: true, viewOnly: true, viewOnlySupported: true, annotations: true }), all = descendants(f.root);
        const viewport = all.find(e => e.className === 'desktop-viewport'), canvas = all.find(e => e.tag === 'canvas');
        viewport.getBoundingClientRect = () => ({ left: 0, top: 0 });
        viewport.clientLeft = viewport.clientTop = 0;
        canvas.getBoundingClientRect = () => ({ left: 0, top: 0, width: 960, height: 540 });
        assert.equal(f.socket.sent.filter(packet => packet[0] === 12).length, 1);
        const start = f.socket.sent.length, event = { clientX: 100, clientY: 100, button: 0, pointerId: 1,

            /** 既定動作を取り消したことを記録します。 */
            preventDefault()
            {} };
        canvas.onpointerdown(event);
        canvas.onpointermove({ ...event, clientX: 120 });
        f.advance(50);
        canvas.onpointerup({ ...event, clientX: 130 });
        assert.equal(f.socket.sent.slice(start).some(packet => [1, 2, 3, 4, 5, 10].includes(packet[0])), false);
        assert.ok(f.socket.sent.slice(start).some(packet => packet[0] === 13 && packet[4] === 2));
        f.windowEvents.blur();
        assert.equal(f.socket.sent.at(-1)[0], 13);
        assert.equal(f.socket.sent.at(-1)[4], 0);
        await f.click('View-only mode');
        const before = f.socket.sent.length;
        f.advance(50);
        assert.equal(f.socket.sent.length, before);
    });
    await t.test('non-experimental receiver', async t =>
    {
        const f = await fixtureAsync(t, 'h264', { annotations: true });
        assert.ok(descendants(f.root).some(e => e.className === 'desktop-annotations'));
        f.socket.onmessage(frame(12, 0, { duration: 4000, cursors: [[0xff4422, 100, 100]], points: [[1, 0xff4422, 1, 100, 100, 0]] }));
        f.callbacks.shift()?.();
        assert.equal(f.socket.readyState, 1);
        assert.equal(f.socket.sent.some(packet => packet[0] === 13), false);
        assert.equal(descendants(f.root).find(e => e.getAttribute('aria-label') === 'View-only mode').getAttribute('aria-pressed'), 'false');
    });
});

test('view-only mode is generally available and sits immediately left of the software keyboard', async t =>
{
    for (const query of ['', '?experimental=false', '?experimental=true'])
    {
        await t.test(query || 'normal', async t =>
        {
            const f = await fixtureAsync(t, null, { query }), toolbar = descendants(f.root).find(e => e.className === 'desktop-toolbar');
            const keyboard = toolbar.children.findIndex(e => e.getAttribute('aria-label') === 'Show software keyboard');
            assert.equal(toolbar.children[keyboard - 1].getAttribute('aria-label'), 'View-only mode');
            assert.equal(toolbar.children[keyboard - 1].getAttribute('aria-pressed'), 'false');
            await f.click('View-only mode');
            assert.equal(toolbar.children[keyboard - 1].getAttribute('aria-pressed'), 'true');
            assert.equal(toolbar.children[keyboard].disabled, true);
        });
    }
});

test('automatic reconnect starts enabled after the display separator and can be disabled', async t =>
{
    for (const experimental of [false, true])
    {
        await t.test(String(experimental), async t =>
        {
            const f = await fixtureAsync(t, null, { experimental }), all = descendants(f.root);
            const toolbar = all.find(item => item.className === 'desktop-toolbar');
            const displays = toolbar.children.findIndex(item => item.getAttribute('aria-label') === 'Displays');
            assert.equal(toolbar.children[displays + 1].className, 'desktop-key-separator');
            assert.equal(toolbar.children[displays + 2].getAttribute('aria-label'), 'Automatic reconnect');
            assert.equal(toolbar.children[displays + 2].getAttribute('aria-pressed'), 'true');
            assert.equal(all.some(item => item.getAttribute('aria-label') === 'Reconnect delays (ms)'), experimental);
            await f.click('Automatic reconnect');
            assert.equal(toolbar.children[displays + 2].getAttribute('aria-pressed'), 'false');
            f.socket.onclose({ code: 1006, reason: '' });
            assert.equal(f.sockets.length, 1);
            assert.match(f.notices.at(-1), /without a close notification/u);
            assert.doesNotMatch(f.notices.at(-1), /interactive user/u);
        });
    }
});

test('the view retries failed connections on schedule and a successful stream resets the budget', async t =>
{
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const f = await fixtureAsync(t);
    const staleClose = f.socket.onclose;
    f.socket.onclose({ code: 1006, reason: '' });
    t.mock.timers.tick(0);
    await new Promise(setImmediate);
    assert.equal(f.sockets.length, 2);
    staleClose({ code: 1006, reason: '' });
    assert.equal(f.sockets.at(-1).readyState, 1);
    f.sockets.at(-1).onclose({ code: 1011, reason: 'Desktop.HostUnavailable' });
    t.mock.timers.tick(2999);
    await new Promise(setImmediate);
    assert.equal(f.sockets.length, 2);
    t.mock.timers.tick(1);
    await new Promise(setImmediate);
    assert.equal(f.sockets.length, 3);
    f.sockets.at(-1).onclose({ code: 1006, reason: '' });
    t.mock.timers.tick(9999);
    await new Promise(setImmediate);
    assert.equal(f.sockets.length, 3);
    t.mock.timers.tick(1);
    await new Promise(setImmediate);
    const recovered = f.sockets.at(-1);
    assert.equal(f.sockets.length, 4);
    recovered.onmessage(frame(7, 0, { clusterId: 'cluster', hostId: 'host', codecDetails: true, variableBitrate: true }));
    recovered.onmessage(frame(1, 0, [{ id: 0, name: 'Screen A', width: 1920, height: 1080, primary: true }]));
    recovered.onmessage(frame(3, 0, [{ id: 0, codec: 1, selection: recovered.sent.findLast(input => input[0] === 6)[3], framesPerSecond: 30,
        maximumBitrateKbps: 4096, variableBitrate: true, codecDetails: f.codecEcho() }]));
    recovered.onclose({ code: 1006, reason: '' });
    t.mock.timers.tick(0);
    await new Promise(setImmediate);
    assert.equal(f.sockets.length, 5);
    assert.equal(f.sockets.at(-1).sent.some(input => [1, 2, 3, 4, 10].includes(input[0])), false);
});

test('manual disconnection, disabling recovery and disposal stop delayed or starting reconnects', async t =>
{
    for (const cancel of ['Disconnect', 'Automatic reconnect', 'dispose'])
    {
        await t.test(cancel, async t =>
        {
            t.mock.timers.enable({ apis: ['setTimeout'] });
            const f = await fixtureAsync(t);
            f.socket.onclose({ code: 1006, reason: '' });
            t.mock.timers.tick(0);
            if (cancel === 'dispose')
            {
                await f.view.dispose();
            }
            else
            {
                await f.click(cancel);
            }
            await new Promise(setImmediate);
            t.mock.timers.tick(60000);
            await new Promise(setImmediate);
            assert.equal(f.sockets.length, 1);
        });
    }
});

test('view-only drops all physical, virtual, delayed and Unicode input while stream controls continue', async t =>
{
    const f = await fixtureAsync(t, 'h264', { experimental: true }), all = descendants(f.root), viewport = all.find(e => e.className === 'desktop-viewport'), canvas = all.find(e => e.tag === 'canvas'), input = all.find(e => e.tag === 'textarea');
    await f.click('Show software keyboard');
    input.value = '\u200b' + 'x'.repeat(64);
    input.events.input({ inputType: 'insertFromPaste' });
    await f.click('Ctrl');
    viewport.onkeydown({ code: 'KeyA',

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} });
    await f.click('View-only mode');
    assert.equal(f.socket.sent.at(-1)[0], 5);
    const from = f.socket.sent.length;
    assert.equal(input.hidden, true);
    assert.equal(all.find(e => e.getAttribute('aria-label') === 'Show software keyboard').disabled, true);
    assert.equal(all.find(e => e.textContent === 'Ctrl').getAttribute('aria-pressed'), 'false');
    const event = { clientX: 100, clientY: 150, button: 0, pointerId: 1,

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {},

        deltaY: 100, deltaMode: 0 };
    canvas.onpointermove(event);
    canvas.onpointerdown(event);
    canvas.onpointerup(event);
    canvas.onpointercancel();
    canvas.events.wheel(event);
    viewport.onkeydown({ code: 'KeyB',

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} });
    viewport.onkeyup({ code: 'KeyB',

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} });
    await f.click('Win');
    await f.click('Show software keyboard');
    input.events.compositionend({ data: '入力してはいけない' });
    input.value = '\u200bblocked';
    input.events.input({ inputType: 'insertText' });
    f.windowEvents.blur();
    for (let i = 0;i < f.callbacks.length;i++)
    {
        f.callbacks[i]();
    }
    await f.click('All screens side by side');
    assert.ok(f.socket.sent.slice(from).some(item => item[0] === 6));
    assert.equal(f.socket.sent.slice(from).some(item => [1, 2, 3, 4, 5, 10].includes(item[0])), false);
    assert.equal(f.socket.readyState, 1);
    await f.click('View-only mode');
    const resumed = f.socket.sent.length;
    for (let i = 0;i < f.callbacks.length;i++)
    {
        f.callbacks[i]();
    }
    assert.equal(f.socket.sent.length, resumed);
    viewport.onkeydown({ code: 'KeyC',

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} });
    assert.equal(f.socket.sent.at(-1)[0], 4);
    assert.equal(f.socket.sent.at(-1)[4], 67);
});

test('view-only mode survives reconnect and disconnect does not bypass the input gate', async t =>
{
    const f = await fixtureAsync(t, 'h264', { experimental: true });
    await f.click('View-only mode');
    const from = f.socket.sent.length;
    await f.click('Disconnect');
    assert.equal(f.socket.sent.length, from);
    await f.click('Connect');
    assert.equal(descendants(f.root).find(e => e.getAttribute('aria-label') === 'View-only mode').getAttribute('aria-pressed'), 'true');
});

test('view-only can be selected before connecting and fullscreen does not capture local system keys', async t =>
{
    const f = await fixtureAsync(t, 'h264', { experimental: true, viewOnly: true });
    let captures = 0;
    navigator.keyboard = {
        /** 試験用キーボードの捕捉状態を記録します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async lock()
        {
            captures++;
        },

        /** 試験用キーボードの捕捉状態を解除します。 */
        unlock()
        {} };
    await f.click('Fullscreen');
    assert.equal(captures, 0);
    assert.equal(f.socket.sent.some(item => [1, 2, 3, 4, 5, 10].includes(item[0])), false);
    assert.ok(f.socket.sent.some(item => item[0] === 6));
});

test('view-only negotiates host state before video and reports both toggle directions', async t =>
{
    const f = await fixtureAsync(t, 'h264', { experimental: true, viewOnly: true, viewOnlySupported: true });
    assert.deepEqual(Array.from(f.socket.sent[0]), [11, 1, 0, 0, 0, 0]);
    await f.click('View-only mode');
    assert.deepEqual(Array.from(f.socket.sent.at(-1)), [11, 0, 0, 0, 0, 0]);
    await f.click('View-only mode');
    assert.equal(f.socket.sent.at(-2)[0], 5);
    assert.deepEqual(Array.from(f.socket.sent.at(-1)), [11, 1, 0, 0, 0, 0]);
    await f.click('Disconnect');
    await f.click('Connect');
    const reconnected = f.sockets.at(-1);
    reconnected.onmessage(frame(7, 0, { viewOnly: true }));
    assert.deepEqual(Array.from(reconnected.sent[0]), [11, 1, 0, 0, 0, 0]);
});

test('view-only keeps input local without sending unsupported mode commands to old hosts', async t =>
{
    const f = await fixtureAsync(t, 'h264', { experimental: true, viewOnly: true, viewOnlySupported: false });
    await f.click('View-only mode');
    await f.click('View-only mode');
    assert.equal(f.socket.sent.some(message => message[0] === 11), false);
    f.socket.onmessage(frame(7, 0, { viewOnly: true }));
    assert.equal(f.socket.sent.at(-1)[0], 11);
    await f.click('Disconnect');
    await f.click('Connect');
    await f.click('View-only mode');
    assert.equal(f.sockets.at(-1).sent.some(message => message[0] === 11), false);
});

test('automatic host connection selects the primary display and switches to all screens', async t =>
{
    const f = await fixtureAsync(t);
    assert.equal(f.socket.url.searchParams.get('host'), 'host');
    assert.equal(f.socket.url.searchParams.has('cluster'), false);
    assert.deepEqual(Array.from(f.socket.sent.at(-1)).slice(0, 2), [6, 0]);
    const old = f.decoders[0];
    await f.click('All screens side by side');
    assert.equal(old.state, 'closed');
    assert.equal(descendants(f.root).filter(item => item.tag === 'canvas').length, 2);
    assert.deepEqual(Array.from(f.socket.sent.at(-1)).slice(0, 2), [6, -1]);
    let closed = 0;
    old.callbacks.output({
        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {
            closed++;
        } });
    assert.equal(closed, 1);
});

test('cursor subscription requires both relay and Core support', async t =>
{
    for (const [cursorRelay, cursorCore] of [[false, true], [true, false], [true, true]])
    {
        await t.test(`${cursorRelay}/${cursorCore}`, async t =>
        {
            const f = await fixtureAsync(t, 'h264', { cursorRelay, cursorCore, cursorEnabled: true });
            assert.equal(f.socket.sent.filter(input => input[0] === 8).length, cursorRelay && cursorCore ? 1 : 0);
            f.describe();
            assert.equal(f.socket.sent.filter(input => input[0] === 8).length, cursorRelay && cursorCore ? 1 : 0);
        });
    }
});

test('cursor synchronization defaults off and never subscribes on a normal connection', async t =>
{
    const f = await fixtureAsync(t, 'h264', { cursorRelay: true, cursorCore: true });
    assert.equal(f.initialCursorSync, 'false');
    assert.equal(f.cursorSync.getAttribute('aria-pressed'), 'false');
    assert.equal(f.socket.sent.filter(input => input[0] === 8).length, 0);
    f.describe();
    await f.click('All screens side by side');
    f.describe();
    assert.equal(f.socket.sent.filter(input => input[0] === 8).length, 0);
    assert.ok(descendants(f.root).filter(item => item.tag === 'canvas').every(canvas => canvas.style.cursor === 'default'));
});

test('disabling a mouse-less host cursor restores it immediately and ignores later hidden notifications', async t =>
{
    const f = await fixtureAsync(t, 'h264', { cursorRelay: true, cursorCore: true });
    f.cursorSync.onclick();
    f.describe();
    let canvas = descendants(f.root).find(item => item.tag === 'canvas');
    assert.equal(f.socket.sent.filter(input => input[0] === 8).length, 1);
    f.socket.onmessage(cursorFrame(0, 13));
    assert.equal(canvas.style.cursor, 'none');
    f.cursorSync.onclick();
    f.describe();
    canvas = descendants(f.root).find(item => item.tag === 'canvas');
    assert.equal(canvas.style.cursor, 'default');
    f.socket.onmessage(cursorFrame(0, 13));
    assert.equal(canvas.style.cursor, 'default');
    await f.click('All screens side by side');
    f.describe();
    assert.ok(descendants(f.root).filter(item => item.tag === 'canvas').every(c => c.style.cursor === 'default'));
    f.socket.onmessage(cursorFrame(1, 1));
    f.cursorSync.onclick();
    f.describe();
    assert.deepEqual(descendants(f.root).filter(item => item.tag === 'canvas').map(c => c.style.cursor), ['default', 'text']);
    assert.equal(f.socket.sent.filter(input => input[0] === 8).length, 1);
    assert.equal(f.socket.readyState, 1);
    assert.equal(f.contexts.length, 1);
});

test('enabling cursor synchronization waits for an acknowledged Core and leaves old peers connected', async t =>
{
    const f = await fixtureAsync(t, 'h264', { cursorRelay: true, cursorCore: false });
    f.cursorSync.onclick();
    assert.equal(f.socket.sent.filter(input => input[0] === 8).length, 0);
    assert.equal(f.socket.readyState, 1);
    assert.equal(descendants(f.root).find(item => item.tag === 'canvas').style.cursor, 'default');
});

test('cursor shapes update only their monitor and survive display reconstruction', async t =>
{
    const f = await fixtureAsync(t, 'h264', { cursorRelay: true, cursorCore: true, cursorEnabled: true });
    const names = ['default', 'text', 'wait', 'progress', 'crosshair', 'nwse-resize', 'nesw-resize', 'ew-resize', 'ns-resize', 'move', 'not-allowed', 'pointer', 'help', 'none'];
    let canvas = descendants(f.root).find(item => item.tag === 'canvas');
    for (let shape = 0;shape < names.length;shape++)
    {
        f.socket.onmessage(cursorFrame(0, shape));
        assert.equal(canvas.style.cursor, names[shape]);
    }
    await f.click('All screens side by side');
    const canvases = descendants(f.root).filter(item => item.tag === 'canvas');
    assert.equal(canvases[0].style.cursor, 'none');
    f.socket.onmessage(cursorFrame(1, 8));
    assert.deepEqual(canvases.map(c => c.style.cursor), ['default', 'ns-resize']);
    await f.click('Display 2: Screen B');
    canvas = descendants(f.root).find(item => item.tag === 'canvas');
    assert.equal(canvas.style.cursor, 'ns-resize');
    assert.equal(f.socket.sent.filter(input => input[0] === 8).length, 1);
    assert.equal(descendants(f.root).find(item => item.className === 'desktop-toolbar').style.cursor, undefined);
});

test('malformed cursor states close the session and release held keys', async t =>
{
    const f = await fixtureAsync(t, 'h264', { cursorRelay: true, cursorCore: true, cursorEnabled: true });
    await f.click('Ctrl');
    f.socket.onmessage(cursorFrame(0, 99));
    assert.equal(f.socket.readyState, 3);
    assert.match(f.notices.at(-1), /cursor/);
    const ctrl = descendants(f.root).find(item => item.tag === 'button' && item.textContent === 'Ctrl');
    assert.equal(ctrl.getAttribute('aria-pressed'), 'false');
});

test('all eight toolbar keys independently hold and release before the refresh separator', async t =>
{
    const f = await fixtureAsync(t), toolbar = descendants(f.root).find(item => item.className === 'desktop-toolbar');
    const bar = descendants(f.root).find(item => item.className === 'desktop-keybar');
    assert.equal(bar.parentElement, toolbar);
    assert.equal(toolbar.children.at(-3), bar);
    assert.equal(toolbar.children.at(-2).className, 'desktop-key-separator');
    assert.equal(toolbar.children.at(-1).getAttribute('aria-label'), 'Refresh hosts');
    for (const [name, key] of [['Win', 0x5b], ['Ctrl', 0xa2], ['Alt', 0xa4], ['Del', 46], ['Esc', 27], ['PrSc', 44], ['Shift', 0xa0], ['Tab', 9]])
    {
        const button = bar.children.find(item => item.textContent === name);
        const before = f.socket.sent.length;
        await f.click(name);
        assert.equal(button.getAttribute('aria-pressed'), 'true');
        assert.deepEqual(Array.from(f.socket.sent.at(-1)), [4, 0, 0, 0, key, 1]);
        assert.equal(f.socket.sent.length, before + 1);
        await f.click(name);
        assert.equal(button.getAttribute('aria-pressed'), 'false');
        assert.deepEqual(Array.from(f.socket.sent.at(-1)), [4, 0, 0, 0, key, 0]);
    }
    await f.click('Ctrl');
    await f.click('Alt');
    await f.click('Del');
    assert.ok(bar.children.filter(b => ['Ctrl', 'Alt', 'Del'].includes(b.textContent)).every(b => b.getAttribute('aria-pressed') === 'true'));
    f.windowEvents.blur();
    assert.equal(f.socket.sent.at(-1)[0], 5);
    assert.ok(bar.children.every(b => b.getAttribute('aria-pressed') === 'false'));
    await f.click('Disconnect');
    assert.ok(bar.children.every(b => b.disabled));
});

test('physical and virtual key holds do not release each other and focus stays within keyboard controls', async t =>
{
    const f = await fixtureAsync(t), viewport = descendants(f.root).find(item => item.className === 'desktop-viewport'), bar = descendants(f.root).find(item => item.className === 'desktop-keybar');
    const event = { code: 'ControlLeft',

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} };
    await f.click('Ctrl');
    const count = f.socket.sent.length;
    viewport.onkeydown(event);
    viewport.onkeyup(event);
    assert.equal(f.socket.sent.length, count);
    viewport.onblur({ relatedTarget: bar.children[0] });
    assert.equal(f.socket.sent.length, count);
    await f.click('Ctrl');
    assert.equal(f.socket.sent.at(-1)[5], 0);
    viewport.onkeydown(event);
    await f.click('Ctrl');
    await f.click('Ctrl');
    assert.equal(f.socket.sent.at(-1)[5], 1);
    viewport.onkeyup(event);
    assert.equal(f.socket.sent.at(-1)[5], 0);
    await f.click('Shift');
    bar.events.focusout({ relatedTarget: viewport });
    assert.equal(f.socket.sent.at(-1)[0], 4);
    bar.events.focusout({ relatedTarget: null });
    assert.equal(f.socket.sent.at(-1)[0], 5);
});

test('rendering choices use Canvas2D and WebGL labels without changing their values', async t =>
{
    const f = await fixtureAsync(t, 'h264', { chromium: true }), rendering = descendants(f.root).find(item => item.getAttribute('aria-label') === 'Video rendering');
    assert.deepEqual(rendering.options.map(o => [o.value, o.textContent]), [['inline', 'Canvas2D'], ['worker', 'WebGL']]);
});

test('returning focus does not discard audio that is still playing', async t =>
{
    const f = await fixtureAsync(t), node = f.audioNodes[0];
    for (let i = 0; i < 10; i++)
    {
        f.windowEvents.blur();
        f.windowEvents.focus();
    }
    assert.equal(node.messages.filter(message => message?.type === 'reset').length, 0);
    f.contexts[0].state = 'suspended';
    f.windowEvents.focus();
    assert.equal(node.messages.filter(message => message?.type === 'reset').length, 1);
});

test('Firefox uses the worker and a graphics failure falls back once without replacing audio or transport', async t =>
{
    const f = await fixtureAsync(t, 'h264', { worker: true }), worker = f.workers[0];
    assert.equal(f.decoders.length, 0);
    assert.equal(worker.sent[0].type, 'init');
    assert.equal(worker.sent[1].type, 'codec');
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    assert.equal(worker.sent.at(-1).type, 'frame');
    worker.onmessage({ data: { type: 'backend-failed', reason: 'texture unsupported' } });
    assert.equal(f.decoders.length, 1);
    assert.equal(f.workers.length, 1);
    assert.equal(f.socket.readyState, 1);
    assert.equal(f.contexts.length, 1);
    assert.ok(descendants(f.root).some(item => item.textContent.includes('texture unsupported')));
    f.describe();
    f.socket.onmessage(frame(2, 0, keyPicture, true, 2000));
    assert.equal(f.decoders[0].decoded.length, 1);
    worker.onmessage({ data: { type: 'backend-failed', reason: 'late' } });
    assert.equal(f.decoders.length, 1);
});

test('dot by dot switches live without restarting media or changing capture resolution', async t =>
{
    const f = await fixtureAsync(t), canvas = descendants(f.root).find(item => item.tag === 'canvas');
    const sizing = descendants(f.root).find(item => item.tag === 'button' && item.getAttribute('aria-label') === 'Dot by dot (1:1)');
    const selections = f.socket.sent.filter(input => input[0] === 6).length;
    assert.equal(sizing.getAttribute('aria-pressed'), 'false');
    const fitWidth = canvas.style.width;
    sizing.onclick();
    assert.equal(canvas.style.width, '1920px');
    assert.equal(canvas.style.height, '1080px');
    sizing.onclick();
    assert.equal(canvas.style.width, fitWidth);
    assert.equal(canvas.width, 1920);
    assert.equal(canvas.height, 1080);
    assert.equal(f.socket.sent.filter(input => input[0] === 6).length, selections);
    assert.equal(f.decoders.length, 1);
    assert.equal(f.contexts.length, 1);
    assert.equal(f.socket.readyState, 1);
});

test('pointer following scrolls before mapping motion and a captured click to the remote display', async t =>
{
    const f = await fixtureAsync(t), all = descendants(f.root), canvas = all.find(item => item.tag === 'canvas'), viewport = all.find(item => item.className === 'desktop-viewport');
    const sizing = all.find(item => item.tag === 'button' && item.getAttribute('aria-label') === 'Dot by dot (1:1)');
    const panning = all.find(item => item.tag === 'button' && item.getAttribute('aria-label') === 'Follow pointer');
    assert.equal(panning.disabled, true);
    sizing.onclick();
    assert.equal(panning.disabled, false);
    assert.equal(panning.getAttribute('aria-pressed'), 'true');
    Object.defineProperties(viewport, { clientWidth: { value: 801 }, clientHeight: { value: 601 } });
    Object.assign(viewport, { scrollWidth: 1920, scrollHeight: 1104, clientLeft: 0, clientTop: 0, scrollLeft: 0, scrollTop: 0 });
    viewport.getBoundingClientRect = () => ({ left: 10, top: 20 });
    canvas.getBoundingClientRect = () => ({ left: 10 - viewport.scrollLeft, top: 44 - viewport.scrollTop, width: 1920, height: 1080 });
    const event = { clientX: 410, clientY: 320, pointerType: 'mouse', pointerId: 1, button: 0,

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} };
    canvas.onpointermove(event);
    f.callbacks.shift()();
    assert.equal(viewport.scrollLeft, 559.5);
    const expected = [0, 959, Math.floor(276 + viewport.scrollTop)];
    assert.deepEqual(Array.from(f.socket.sent.at(-1)).slice(1, 4), expected);
    canvas.onpointerdown(event);
    assert.deepEqual(Array.from(f.socket.sent.at(-1)).slice(0, 6), [2, ...expected, 0, 1]);
    canvas.onpointerup(event);
    assert.deepEqual(Array.from(f.socket.sent.at(-1)).slice(0, 6), [2, ...expected, 0, 0]);
    canvas.onpointermove({ ...event, pointerType: 'touch', clientX: 810 });
    assert.equal(viewport.scrollLeft, 559.5);
    sizing.onclick();
    assert.equal(panning.disabled, true);
    assert.equal(viewport.scrollLeft, 0);
    canvas.onpointermove(event);
    assert.equal(viewport.scrollLeft, 0);
});

test('view-only follows locally before publishing annotations and never queues remote input', async t =>
{
    for (const annotations of [false, true])
    {
        await t.test('annotations=' + annotations, async t =>
        {
            const f = await fixtureAsync(t, null, { experimental: false, viewOnly: true, viewOnlySupported: annotations, annotations });
            const all = descendants(f.root), canvas = all.find(item => item.tag === 'canvas'), viewport = all.find(item => item.className === 'desktop-viewport');
            await f.click('Dot by dot (1:1)');
            Object.defineProperties(viewport, { clientWidth: { value: 801 }, clientHeight: { value: 601 } });
            Object.assign(viewport, { scrollWidth: 1920, scrollHeight: 1104, clientLeft: 0, clientTop: 0, scrollLeft: 0, scrollTop: 0 });
            viewport.getBoundingClientRect = () => ({ left: 10, top: 20 });
            canvas.getBoundingClientRect = () => ({ left: 10 - viewport.scrollLeft, top: 44 - viewport.scrollTop, width: 1920, height: 1080 });
            const start = f.socket.sent.length, event = { clientX: 410, clientY: 320, pointerType: 'mouse', pointerId: 1, button: 0,

                /** 既定動作の抑止だけを受け付けます。 */
                preventDefault()
                {} };
            canvas.onpointermove({ ...event, clientX: 50 });
            canvas.onpointermove(event);
            assert.equal(viewport.scrollLeft, 0);

            // Animation and the timer must both pan before resolving annotation coordinates.
            f.advance(50);
            assert.equal(viewport.scrollLeft, 559.5);
            assert.ok(0 < viewport.scrollTop);
            if (annotations)
            {
                assert.deepEqual(Array.from(f.socket.sent.findLast(packet => packet[0] === 13)).slice(0, 5), [13, 0, 959, Math.floor(276 + viewport.scrollTop), 1]);
            }
            const scroll = [viewport.scrollLeft, viewport.scrollTop];
            await f.click('Follow pointer');
            canvas.onpointermove({ ...event, clientX: 810 });
            f.advance(50);
            assert.deepEqual([viewport.scrollLeft, viewport.scrollTop], scroll);
            await f.click('Follow pointer');
            canvas.onpointermove({ ...event, pointerType: 'touch', clientX: 20 });
            f.advance(50);
            assert.deepEqual([viewport.scrollLeft, viewport.scrollTop], scroll);
            f.socket.bufferedAmount = 2048;
            canvas.onpointermove({ ...event, clientX: 210 });
            f.advance(50);
            assert.equal(viewport.scrollLeft, 279.75);
            f.socket.bufferedAmount = 0;
            await f.click('Dot by dot (1:1)');
            canvas.onpointermove(event);
            f.advance(50);
            assert.equal(viewport.scrollLeft, 0);
            assert.equal(viewport.scrollTop, 0);
            assert.equal(f.socket.sent.slice(start).some(packet => [1, 2, 3, 4, 5, 10].includes(packet[0])), false);
            canvas.onpointermove(event);
            await f.click('View-only mode');
            const resumed = f.socket.sent.length;
            f.advance(50);
            assert.equal(f.socket.sent.slice(resumed).some(packet => [1, 2, 3, 4, 5, 10].includes(packet[0])), false);
        });
    }
});

test('video-only fullscreen is single-monitor only and exits on disconnect or all-screen selection', async t =>
{
    const f = await fixtureAsync(t), viewport = descendants(f.root).find(item => item.className === 'desktop-viewport');
    const button = descendants(f.root).find(item => item.getAttribute('aria-label') === 'Video fullscreen');
    assert.equal(button.disabled, false);
    await f.click('Video fullscreen');
    assert.equal(document.fullscreenElement, viewport);
    let handled = 0;
    f.documentEvents.keydown({ key: 'Escape',

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {
            handled++;
        },

        /** イベント伝搬を止めたことを記録します。 */
        stopPropagation()
        {
            handled++;
        } });
    assert.equal(handled, 2);
    assert.equal(document.fullscreenElement, null);
    assert.equal(f.socket.sent.at(-1)[0], 5);
    const sent = f.socket.sent.length;
    f.documentEvents.keydown({ key: 'Escape', repeat: true,

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {
            handled++;
        },

        /** イベント伝搬を止めたことを記録します。 */
        stopPropagation()
        {
            handled++;
        } });
    assert.equal(f.socket.sent.length, sent);
    f.documentEvents.keyup({ key: 'Escape',

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {
            handled++;
        },

        /** イベント伝搬を止めたことを記録します。 */
        stopPropagation()
        {
            handled++;
        } });
    assert.equal(handled, 6);
    await f.click('Video fullscreen');
    await f.click('All screens side by side');
    assert.equal(document.fullscreenElement, null);
    assert.equal(button.disabled, true);
    await f.click('Display 1: Screen A');
    assert.equal(button.disabled, false);
    await f.click('Video fullscreen');
    await f.click('Disconnect');
    assert.equal(document.fullscreenElement, null);
    assert.equal(button.disabled, true);
});

test('workspace fullscreen survives starting and stopping a connection without capturing keys before connect', async t =>
{
    const f = await fixtureAsync(t, 'h264', { connect: false });
    let locks = 0;
    navigator.keyboard = {
        /** 接続後にだけシステムキーが捕捉されることを記録します。
         * @returns 捕捉完了です。
         */
        async lock()
        {
            locks++;
        },

        /** 捕捉終了の呼出しを受け付けます。 */
        unlock()
        {} };
    await f.click('Fullscreen');
    assert.equal(document.fullscreenElement, f.root);
    assert.equal(locks, 0);
    assert.equal(f.sockets.length, 0);
    await f.click('Connect');
    assert.equal(document.fullscreenElement, f.root);
    assert.equal(locks, 1);
    await f.click('Disconnect');
    assert.equal(document.fullscreenElement, f.root);
    await f.click('Exit fullscreen');
    assert.equal(document.fullscreenElement, null);
});

test('disposed views cannot enter fullscreen after an outstanding browser request finishes', async t =>
{
    const f = await fixtureAsync(t, 'h264', { connect: false });
    let finish;
    f.root.requestFullscreen = () => new Promise(resolve =>
    {
        finish = () =>
        {
            document.fullscreenElement = f.root;
            resolve();
        };
    });
    const entering = f.click('Fullscreen');
    await f.view.dispose();
    finish();
    await entering;
    assert.equal(document.fullscreenElement, null);
});

test('fullscreen keyboard capture forwards Windows keys and releases capture on exit', async t =>
{
    const f = await fixtureAsync(t);
    let locks = 0, unlocks = 0;
    navigator.keyboard = {
        /** 試験用キーボードの捕捉状態を記録します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async lock()
        {
            locks++;
        },

        /** 試験用キーボードの捕捉状態を解除します。 */
        unlock()
        {
            unlocks++;
        } };
    await f.click('Fullscreen');
    assert.equal(locks, 1);
    const viewport = descendants(f.root).find(item => item.className === 'desktop-viewport');
    viewport.onkeydown({ code: 'MetaLeft',

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} });
    viewport.onkeyup({ code: 'MetaLeft',

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} });
    assert.deepEqual(f.socket.sent.slice(-2).map(message => Array.from(message)), [[4, 0, 0, 0, 0x5b, 1], [4, 0, 0, 0, 0x5b, 0]]);
    await f.click('Exit fullscreen');
    assert.equal(unlocks, 1);
    assert.equal(f.socket.sent.at(-1)[0], 5);
});

test('denied keyboard capture preserves the connection and the Win toggle remains available', async t =>
{
    const f = await fixtureAsync(t);
    let unlocks = 0;
    navigator.keyboard = {
        /** 試験用キーボードの捕捉状態を記録します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async lock()
        {
            throw new Error('Denied');
        },

        /** 試験用キーボードの捕捉状態を解除します。 */
        unlock()
        {
            unlocks++;
        } };
    await f.click('Fullscreen');
    assert.equal(f.socket.readyState, 1);
    assert.equal(unlocks, 1);
    assert.match(f.notices.at(-1), /not allowed/);
    await f.click('Win');
    await f.click('Win');
    assert.deepEqual(f.socket.sent.slice(-2).map(message => Array.from(message)), [[4, 0, 0, 0, 0x5b, 1], [4, 0, 0, 0, 0x5b, 0]]);
});

test('late keyboard permission completion cannot retain capture after disconnect', async t =>
{
    const f = await fixtureAsync(t);
    let complete, unlocks = 0;
    navigator.keyboard = { lock: () => new Promise(resolve =>
    {
        complete = resolve;
    }),

    /** 試験用キーボードの捕捉状態を解除します。 */
    unlock()
    {
        unlocks++;
    } };
    const entering = f.click('Video fullscreen');
    await Promise.resolve();
    assert.equal(typeof complete, 'function');
    await f.click('Disconnect');
    complete();
    await entering;
    assert.ok((1) <= (unlocks));
    assert.equal(document.fullscreenElement, null);
    assert.equal(f.socket.readyState, 3);
});

test('hiding the desktop still discards stale buffered audio', async t =>
{
    const f = await fixtureAsync(t);
    document.hidden = true;
    f.documentEvents.visibilitychange();
    assert.equal(f.audioNodes[0].messages.filter(message => message?.type === 'reset').length, 1);
});

test('decoder waits for an SPS and keyframe and bounds pending VideoFrames', async t =>
{
    const f = await fixtureAsync(t, 'h264'), decoder = f.decoders[0];
    f.socket.onmessage(frame(2, 0, new Uint8Array([0, 0, 0, 1, 0x41]), false));
    assert.equal(decoder.decoded.length, 0);
    const key = new Uint8Array([0, 0, 0, 1, 0x67, 0x42, 0, 0x2a, 0, 0, 0, 1, 0x65]);
    f.socket.onmessage(frame(2, 0, key, true));
    assert.equal(decoder.configuration.codec, 'avc1.42002a');
    assert.equal(decoder.decoded.length, 1);
    let released = 0;
    for (let i = 0; i < 9; i++)
    {
        decoder.callbacks.output({ timestamp: i,

            /** 対象を閉じ、関連する状態を整理します。 */
            close()
            {
                released++;
            } });
    }
    assert.equal(released, 1);
    await f.click('Disconnect');
    assert.equal(released, 9);
    assert.equal(decoder.state, 'closed');
});

test('slow input closes once without recursive disconnect or retained decoders', async t =>
{
    const f = await fixtureAsync(t);
    f.socket.bufferedAmount = 5000;
    const viewport = descendants(f.root).find(item => item.className === 'desktop-viewport');
    viewport.onkeydown({ keyCode: 65,

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} });
    assert.equal(f.socket.readyState, 3);
    assert.equal(f.decoders[0].state, 'closed');
    assert.equal(f.notices.filter(Boolean).length, 1);
});

test('mouse movement backpressure keeps only the latest position without disconnecting', async t =>
{
    const f = await fixtureAsync(t), canvas = descendants(f.root).find(item => item.tag === 'canvas');
    canvas.getBoundingClientRect = () => ({ left: 0, top: 0, width: 1920, height: 1080 });
    f.socket.bufferedAmount = 5000;
    for (let i = 0;i < 100;i++)
    {
        canvas.onpointermove({ clientX: 100 + i, clientY: 200 + i });
        f.callbacks.shift()?.();
        f.advance(16);
        assert.equal(f.socket.readyState, 1);
    }
    assert.equal(f.socket.sent.filter(input => input[0] === 1).length, 0);
    f.socket.bufferedAmount = 0;
    f.advance(25);
    f.callbacks.shift()?.();
    const movements = f.socket.sent.filter(input => input[0] === 1);
    assert.equal(movements.length, 1);
    assert.deepEqual(Array.from(movements[0]).slice(1, 4), [0, 199, 299]);
});

test('fine wheel motion is accumulated into one proportional send and flushes without animation callbacks', async t =>
{
    const f = await fixtureAsync(t), all = descendants(f.root), canvas = all.find(item => item.tag === 'canvas'), viewport = all.find(item => item.className === 'desktop-viewport');
    viewport.clientLeft = viewport.clientTop = 0;
    viewport.getBoundingClientRect = () => ({ left: 0, top: 0 });
    canvas.getBoundingClientRect = () => ({ left: 0, top: 0, width: 1920, height: 1080 });
    const event = { clientX: 100, clientY: 200, deltaMode: 0, deltaY: 1,

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} };
    for (let i = 0;i < 100;i++)
    {
        canvas.events.wheel(event);
    }
    f.advance(25);
    const wheels = f.socket.sent.filter(input => input[0] === 3);
    assert.equal(wheels.length, 1);
    assert.ok(Math.abs(wheels[0][4] + 120) <= 1);
    canvas.events.wheel({ ...event, deltaY: 0, deltaX: 100 });
    f.advance(25);
    assert.equal(f.socket.sent.filter(input => input[0] === 3).length, 1);
    canvas.events.wheel({ ...event, deltaY: 3, deltaMode: 1 });
    f.advance(25);
    assert.equal(f.socket.sent.at(-1)[4], -120);
    canvas.events.wheel(event);
    f.windowEvents.blur();
    f.advance(25);
    assert.equal(f.socket.sent.at(-1)[0], 5, 'losing focus cancels queued scroll');
});

test('native in-flight decoding remains bounded when the API queue empties before output', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    for (let i = 0;i < 60;i++)
    {
        f.socket.onmessage(frame(2, 0, i === 0 ? keyPicture : new Uint8Array([0, 0, 1, 0x41]), i === 0, i * 33333));
    }
    assert.equal(decoder.decoded.length, 16);
    decoder.callbacks.output({ timestamp: 0,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    f.callbacks.shift()?.();
    assert.equal(decoder.decoded.length, 17);
    assert.equal(f.socket.readyState, 1);
});

test('a release that cannot be delivered closes the session instead of leaving remote keys held', async t =>
{
    const f = await fixtureAsync(t);
    f.socket.bufferedAmount = 5000;
    f.windowEvents.blur();
    assert.equal(f.socket.readyState, 3);
    assert.equal(f.decoders[0].state, 'closed');
    assert.equal(f.notices.filter(Boolean).length, 1);
});

test('a new audio-gated frame after a long static period gets its own decode deadline', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    decoder.callbacks.output({ timestamp: 1000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    f.callbacks.shift()?.();
    f.advance(5000);
    f.audioNodes[0].port.onmessage({ data: { source: 1000000, context: 0, rate: 1 } });
    f.socket.onmessage(frame(2, 0, new Uint8Array([0, 0, 1, 0x41]), false, 1100000));
    f.advance(25);
    assert.equal(f.decoders.length, 1, 'a normal wait for audio must not restart capture');
    f.contexts[0].currentTime = 0.2;
    f.callbacks.shift()?.();
    assert.equal(decoder.decoded.at(-1).timestamp, 1100000);
});

test('successful recovery clears failure history even when presentation latency exceeds 150 ms', async t =>
{
    const f = await fixtureAsync(t);
    let timestamp = 0;
    for (let cycle = 0;cycle < 4;cycle++)
    {
        const stalled = f.decoders.at(-1);
        f.socket.onmessage(frame(2, 0, keyPicture, true, ++timestamp));
        stalled.decodeQueueSize = 1;
        f.socket.onmessage(frame(9, 0, new Uint8Array(8)));
        f.advance(1600);
        assert.equal(f.socket.readyState, 1);
        f.describe();
        const restored = f.decoders.at(-1);
        for (let i = 0;i < 30;i++)
        {
            const stamp = ++timestamp;
            f.socket.onmessage(frame(2, 0, keyPicture, true, stamp));
            f.socket.onmessage(frame(9, 0, new Uint8Array(8)));
            f.advance(200);
            restored.callbacks.output({ timestamp: stamp,

                /** 対象を閉じ、関連する状態を整理します。 */
                close()
                {} });
            f.callbacks.shift()?.();
        }
        assert.equal(f.socket.readyState, 1);
    }
});

test('video decodes ahead while presentation waits for the audible source clock', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    f.audioNodes[0].port.onmessage({ data: { source: 1_000_000, context: 0, rate: 1 } });
    const key = new Uint8Array([0, 0, 0, 1, 0x67, 0x42, 0, 0x2a, 0, 0, 0, 1, 0x65]);
    f.socket.onmessage(frame(2, 0, key, true, 1_010_000));
    assert.equal(decoder.decoded.length, 1);
    decoder.callbacks.output({ timestamp: 1_010_000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    f.callbacks.shift()();
    const canvas = descendants(f.root).find(item => item.tag === 'canvas');
    assert.equal(canvas.drawn.length, 0);
    f.contexts[0].currentTime = 0.1;
    f.callbacks.shift()();
    assert.equal(decoder.decoded.length, 1);
    assert.equal(decoder.decoded[0].timestamp, 1_010_000);
    assert.deepEqual(canvas.drawn, [1_010_000]);
});

test('screen switching preserves the audio context and disconnect closes it', async t =>
{
    const f = await fixtureAsync(t);
    await f.click('All screens side by side');
    await f.click('Display 2: Screen B');
    assert.equal(f.contexts.length, 1);
    assert.equal(f.contexts[0].state, 'running');
    await f.click('Disconnect');
    assert.equal(f.contexts[0].state, 'closed');
});

test('toolbar labels disconnect and audio state while keeping both fullscreen actions accessible', async t =>
{
    const f = await fixtureAsync(t), toolbar = descendants(f.root).find(item => item.className === 'desktop-toolbar');

    /** 操作の実行と処理中表示を持つボタンを構成します。
     * @returns 呼出し元が使用する値です。
     */
    const button = name => descendants(f.root).find(item => item.tag === 'button' && item.getAttribute('aria-label') === name);

    for (const name of ['Disconnect', 'Mute audio', 'Fullscreen', 'Video fullscreen'])
    {
        assert.equal(button(name).parentElement, toolbar);
    }
    assert.equal(button('Disconnect').textContent, 'Disconnect');
    assert.match(button('Disconnect').className, /desktop-labeled/);
    const sound = button('Mute audio');
    assert.equal(sound.textContent, '');
    assert.equal(sound.getAttribute('aria-pressed'), 'false');
    await f.click('Mute audio');
    assert.equal(sound.textContent, '');
    assert.equal(sound.getAttribute('aria-pressed'), 'true');
    assert.equal(sound.dataset.muted, 'true');
    assert.equal(f.contexts[0].gains.at(-1), 0);
    await f.click('Unmute audio');
    assert.equal(sound.textContent, '');
    assert.equal(sound.getAttribute('aria-pressed'), 'false');
    assert.equal(sound.dataset.muted, 'false');
    assert.equal(f.contexts[0].gains.at(-1), 1);
    assert.equal(f.socket.readyState, 1);
});

test('remote viewers preserve every held-key repeat and are not automatically muted', async t =>
{
    const f = await fixtureAsync(t), viewport = descendants(f.root).find(item => item.className === 'desktop-viewport');
    f.socket.onmessage(frame(7, 0, { clusterId: 'cluster', hostId: 'host', localAudioMuted: false }));
    for (const [code, key] of [['KeyA', 65], ['ArrowLeft', 37], ['Backspace', 8], ['Minus', 0x10c]])
    {
        const before = f.socket.sent.length;
        for (let index = 0; index < 5; index++)
        {
            viewport.onkeydown({ code, repeat: index !== 0,

                /** ブラウザー側の既定入力を取り消します。 */
                preventDefault()
                {} });
        }
        viewport.onkeyup({ code,

            /** ブラウザー側の既定入力を取り消します。 */
            preventDefault()
            {} });
        assert.deepEqual(f.socket.sent.slice(before).map(message => Array.from(message).slice(4)), [...Array.from({ length: 5 }, () => [key, 1]), [key, 0]]);
    }
    assert.equal(descendants(f.root).find(item => item.getAttribute('aria-label') === 'Mute audio').dataset.muted, 'false');
    f.windowEvents.blur();
    assert.equal(f.socket.sent.at(-1)[0], 5);
});

test('local connections mute playback before PCM can feed back into loopback capture', async t =>
{
    const f = await fixtureAsync(t);
    f.socket.onmessage(frame(7, 0, { clusterId: 'cluster', hostId: 'host', localAudioMuted: true }));
    f.socket.onmessage(frame(4, 0, { sampleRate: 48000, channels: 2, format: 'f32le' }));
    assert.equal(f.contexts[0].gains.at(-1), 0);
    const sound = descendants(f.root).find(item => item.tag === 'button' && item.getAttribute('aria-label') === 'Unmute audio');
    assert.equal(sound.textContent, '');
    assert.equal(sound.dataset.muted, 'true');
    assert.match(sound.title, /feedback/);
    const viewport = descendants(f.root).find(item => item.className === 'desktop-viewport');
    const before = f.socket.sent.length;
    viewport.onkeydown({ code: 'KeyA', keyCode: 65,

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} });
    viewport.onkeydown({ code: 'KeyA', keyCode: 65, repeat: true,

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} });
    viewport.onkeyup({ code: 'KeyA', keyCode: 65,

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} });
    viewport.onkeyup({ code: 'KeyA', keyCode: 65,

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} });
    assert.equal(f.socket.sent.length - before, 2);
    assert.ok(descendants(f.root).some(item => item.textContent?.includes('initially muted to prevent feedback')));
    await f.click('Unmute audio');
    assert.equal(f.contexts[0].gains.at(-1), 1);
    assert.equal(sound.textContent, '');
    assert.equal(sound.dataset.muted, 'false');
    assert.equal(descendants(f.root).some(item => item.textContent?.includes('initially muted to prevent feedback')), false);
});

test('a Core fault reports its stage and releases the media resources', async t =>
{
    const f = await fixtureAsync(t);
    f.socket.onmessage(frame(8, 0, { stage: 'audio', type: 'COMException', code: '88890004' }));
    assert.equal(f.socket.readyState, 3);
    assert.ok(f.decoders.every(decoder => decoder.state === 'closed'));
    assert.equal(f.contexts[0].state, 'closed');
    assert.match(f.notices.at(-1), /audio \/ COMException \/ 88890004/);
});

const keyPicture = new Uint8Array([0, 0, 0, 1, 0x67, 0x42, 0, 0x2a, 0, 0, 0, 1, 0x65]);

test('a flush that emits its tail but never settles recovers video while preserving audio and transport', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    decoder.flush = () =>
    {
        decoder.callbacks.output({ timestamp: 1000,

            /** 対象を閉じ、関連する状態を整理します。 */
            close()
            {} });
        return new Promise(() =>
        {});
    };
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    f.advance(75);
    f.callbacks.shift()?.();
    f.advance(1600);
    assert.equal(f.decoders.length, 2);
    assert.equal(decoder.state, 'closed');
    assert.equal(f.socket.readyState, 1);
    assert.equal(f.contexts.length, 1);
    assert.equal(f.audioNodes[0].messages.filter(value => value?.type === 'reset').length, 0);
});

test('a lost capture reference followed by silence requests recovery instead of staying connected and frozen', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    decoder.callbacks.output({ timestamp: 1000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    f.callbacks.shift()?.();
    f.socket.onmessage(frame(9, 0, new Uint8Array(8), true));
    f.advance(1600);
    assert.equal(f.decoders.length, 2);
    assert.equal(f.socket.readyState, 1);
    assert.equal(f.contexts.length, 1);
});

test('a flush that resolves without the newest picture cannot silently leave the previous picture forever', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    decoder.callbacks.output({ timestamp: 1000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    f.callbacks.shift()?.();
    decoder.flush = async() =>
    {};
    f.socket.onmessage(frame(2, 0, new Uint8Array([0, 0, 1, 0x41]), false, 2000));
    f.advance(75);
    await Promise.resolve();
    f.advance(1600);
    assert.equal(f.decoders.length, 2);
    assert.equal(f.socket.readyState, 1);
    assert.equal(f.contexts.length, 1);
});

test('a natural key cancels reference recovery and healthy static intervals need no refresh', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    decoder.decode = chunk => decoder.callbacks.output({ timestamp: chunk.timestamp,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    f.callbacks.shift()?.();
    f.socket.onmessage(frame(9, 0, new Uint8Array(8), true));
    f.advance(1000);
    f.socket.onmessage(frame(2, 0, keyPicture, true, 2000));
    f.callbacks.shift()?.();
    for (let i = 0;i < 120;i++)
    {
        f.socket.onmessage(frame(9, 0, new Uint8Array(8)));
        f.advance(1000);
    }
    assert.equal(f.decoders.length, 1);
    assert.equal(f.socket.readyState, 1);
});

test('bursty 30 fps delivery does not flush and replay a GOP between short arrival gaps', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    let flushes = 0;
    decoder.flush = async() =>
    {
        flushes++;
    };
    for (let i = 0;i < 30;i++)
    {
        f.socket.onmessage(frame(2, 0, i === 0 ? keyPicture : new Uint8Array([0, 0, 1, 0x41]), i === 0, i * 33333));
        if (i)
        {
            decoder.callbacks.output({ timestamp: (i - 1) * 33333,

                /** 対象を閉じ、関連する状態を整理します。 */
                close()
                {} });
        }
        f.advance(i % 2 ? 17 : 50);
        f.callbacks.shift()?.();
    }
    assert.equal(flushes, 0);
    assert.equal(decoder.decoded.length, 30);
    decoder.flush = async() =>
    {
        flushes++;
        decoder.callbacks.output({ timestamp: 29 * 33333,

            /** 対象を閉じ、関連する状態を整理します。 */
            close()
            {} });
    };
    f.advance(75);
    await Promise.resolve();
    f.callbacks.shift()?.();
    assert.equal(flushes, 1);
    assert.equal(descendants(f.root).find(item => item.tag === 'canvas').drawn.at(-1), 29 * 33333);
});

for (const fps of [30, 60, 120])
{
    test(`VFR arrival gaps do not become repeated pauses at a ${fps} fps ceiling`, async t =>
    {
        const f = await fixtureAsync(t, 'h264', { fps }), decoder = f.decoders[0];
        let pending, needsKey = true, flushes = 0;
        decoder.decode = chunk =>
        {
            if (needsKey)
            {
                assert.equal(chunk.type, 'key');
            }
            needsKey = false;
            decoder.decoded.push(chunk);
            if (pending != null)
            {
                decoder.callbacks.output({ timestamp: pending,

                    /** 対象を閉じ、関連する状態を整理します。 */
                    close()
                    {} });
            }
            pending = chunk.timestamp;
        };
        decoder.flush = async() =>
        {
            flushes++;
            needsKey = true;
            if (pending != null)
            {
                decoder.callbacks.output({ timestamp: pending,

                    /** 対象を閉じ、関連する状態を整理します。 */
                    close()
                    {} });
            }
            pending = null;
        };
        for (let i = 0;i < 60;i++)
        {
            const key = i % 8 === 0;
            f.socket.onmessage(frame(2, 0, key ? keyPicture : new Uint8Array([0, 0, 1, 0x41]), key, i * 33333));
            f.advance(i % 2 ? 17 : 50);
            await Promise.resolve();
            f.callbacks.shift()?.();
        }
        assert.equal(flushes, 0, 'an identical arrival trace must not trigger more flushes at a higher capture ceiling');
        assert.equal(decoder.decoded.length, 60, 'no compressed reference replay is needed while these updates continue');
        f.advance(75);
        await Promise.resolve();
        f.callbacks.shift()?.();
        assert.equal(flushes, 1);
        assert.equal(descendants(f.root).find(item => item.tag === 'canvas').drawn.at(-1), 59 * 33333);
    });
}

test('a full decoder drains before a queued independent frame instead of waiting for recovery', async t =>
{
    const f = await fixtureAsync(t, 'h264', { fps: 120 }), decoder = f.decoders[0], pending = [];
    let released = 0, needsKey = true;
    decoder.decode = chunk =>
    {
        if (needsKey)
        {
            assert.equal(chunk.type, 'key');
        }
        needsKey = false;
        decoder.decoded.push(chunk);
        pending.push(chunk.timestamp);
    };
    decoder.flush = async() =>
    {
        needsKey = true;
        while (pending.length)
        {
            decoder.callbacks.output({ timestamp: pending.shift(),

                /** 対象を閉じ、関連する状態を整理します。 */
                close()
                {
                    released++;
                } });
        }
    };
    for (let i = 0;i < 17;i++)
    {
        f.socket.onmessage(frame(2, 0, i % 16 === 0 ? keyPicture : new Uint8Array([0, 0, 1, 0x41]), i % 16 === 0, 1_000_000 + i * 8333));
    }
    f.advance(50);
    f.socket.onmessage(frame(2, 0, new Uint8Array([0, 0, 1, 0x41]), false, 1_000_000 + 17 * 8333));
    f.advance(25);
    await Promise.resolve();
    f.callbacks.shift()?.();
    assert.equal(decoder.decoded.length, 18, 'fresh arrivals must not trap the independent queued frame behind the in-flight limit');
    f.advance(75);
    await Promise.resolve();
    f.callbacks.shift()?.();
    assert.equal(descendants(f.root).find(item => item.tag === 'canvas').drawn.at(-1), 1_000_000 + 17 * 8333);
    assert.equal(released, 18);
    assert.equal(f.socket.sent.filter(input => input[0] === 6).length, 1);
});

test('a full decoder does not flush away references needed by a queued delta', async t =>
{
    const f = await fixtureAsync(t, 'h264', { fps: 120 }), decoder = f.decoders[0];
    let flushes = 0;
    decoder.decode = chunk => decoder.decoded.push(chunk);
    decoder.flush = async() =>
    {
        flushes++;
    };
    for (let i = 0;i < 17;i++)
    {
        f.socket.onmessage(frame(2, 0, i === 0 ? keyPicture : new Uint8Array([0, 0, 1, 0x41]), i === 0, 1_000_000 + i * 8333));
    }
    f.advance(75);
    await Promise.resolve();
    assert.equal(flushes, 0);
    assert.equal(decoder.decoded.length, 16);
    decoder.callbacks.output({ timestamp: 1_000_000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    decoder.ondequeue();
    assert.equal(decoder.decoded.length, 17);
    assert.equal(decoder.decoded.at(-1).type, 'delta');
});

test('backpressure resumes at the next queued key even when deltas precede it', async t =>
{
    const f = await fixtureAsync(t, 'h264', { fps: 120 }), decoder = f.decoders[0], pending = [];
    let needsKey = true, released = 0;
    decoder.decode = chunk =>
    {
        if (needsKey)
        {
            assert.equal(chunk.type, 'key');
        }
        needsKey = false;
        decoder.decoded.push(chunk);
        if (decoder.decoded.length <= 8)
        {
            decoder.callbacks.output({ timestamp: chunk.timestamp,

                /** 対象を閉じ、関連する状態を整理します。 */
                close()
                {
                    released++;
                } });
        }
        else
        {
            pending.push(chunk.timestamp);
        }
    };
    decoder.flush = async() =>
    {
        needsKey = true;
        while (pending.length)
        {
            decoder.callbacks.output({ timestamp: pending.shift(),

                /** 対象を閉じ、関連する状態を整理します。 */
                close()
                {
                    released++;
                } });
        }
    };
    for (let i = 0;i < 34;i++)
    {
        f.socket.onmessage(frame(2, 0, i % 16 === 0 ? keyPicture : new Uint8Array([0, 0, 1, 0x41]), i % 16 === 0, 1_000_000 + i * 8333));
    }
    f.advance(75);
    await Promise.resolve();
    f.callbacks.shift()?.();
    assert.deepEqual(decoder.decoded.slice(-2).map(chunk => [chunk.type, chunk.timestamp]), [['key', 1_000_000 + 32 * 8333], ['delta', 1_000_000 + 33 * 8333]]);
    assert.equal(decoder.decoded.length, 26, 'only the eight unsubmitted deltas before the independent key are skipped');
    f.advance(75);
    await Promise.resolve();
    f.callbacks.shift()?.();
    assert.equal(descendants(f.root).find(item => item.tag === 'canvas').drawn.at(-1), 1_000_000 + 33 * 8333);
    assert.equal(released, 26);
    assert.equal(f.socket.sent.filter(input => input[0] === 6).length, 1);
});

test('high-rate mouse events share one coordinate calculation and the stopped tail sends without animation frames', async t =>
{
    const f = await fixtureAsync(t), all = descendants(f.root), canvas = all.find(item => item.tag === 'canvas'), viewport = all.find(item => item.className === 'desktop-viewport');
    let reads = 0;
    viewport.clientLeft = viewport.clientTop = 0;
    viewport.getBoundingClientRect = () => ({ left: 0, top: 0 });
    canvas.getBoundingClientRect = () =>
    {
        reads++;
        return { left: 0, top: 0, width: 1920, height: 1080 };
    };
    for (let i = 0;i < 1000;i++)
    {
        canvas.onpointermove({ clientX: 100 + i, clientY: 200 });
    }
    assert.equal(reads, 0);
    f.callbacks.shift()?.();
    assert.equal(reads, 1);
    canvas.onpointermove({ clientX: 1100, clientY: 201 });
    f.advance(25);
    assert.equal(reads, 2);
    assert.deepEqual(Array.from(f.socket.sent.at(-1)).slice(0, 4), [1, 0, 1100, 201]);
    f.advance(1000);
    assert.equal(reads, 2);
});

test('a burst across a static timestamp gap preserves queued decode references', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    decoder.decodeQueueSize = 2;
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    f.socket.onmessage(frame(2, 0, new Uint8Array([0, 0, 1, 0x41]), false, 10_000_000));
    decoder.decodeQueueSize = 0;
    decoder.ondequeue();
    assert.deepEqual(decoder.decoded.map(chunk => chunk.timestamp), [1000, 10_000_000]);
    assert.equal(f.decoders.length, 1);
    assert.equal(f.socket.readyState, 1);
});

test('repeated buffered VFR pauses resume deltas without restarting capture or disconnecting', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    let flushed = 0, released = 0, outputs = 0, requiresKey = true;
    const buffered = [];
    decoder.decode = chunk =>
    {
        if (requiresKey)
        {
            assert.equal(chunk.type, 'key', 'WebCodecs requires a real key after flush');
        }
        requiresKey = false;
        decoder.decoded.push(chunk);
        buffered.push(chunk.timestamp);
    };
    decoder.flush = async () =>
    {
        flushed++;
        requiresKey = true;
        while (buffered.length)
        {
            outputs++;
            decoder.callbacks.output({ timestamp: buffered.shift(),

                /** 対象を閉じ、関連する状態を整理します。 */
                close()
                {
                    released++;
                } });
        }
    };
    const canvas = descendants(f.root).find(item => item.tag === 'canvas');
    for (let i = 0; i < 100; i++)
    {
        const key = i % 10 === 0;
        f.socket.onmessage(frame(2, 0, key ? keyPicture : new Uint8Array([0, 0, 1, 0x41]), key, (i + 1) * 100000));
        f.socket.onmessage(frame(9, 0, new Uint8Array(8)));
        f.advance(100);
        await Promise.resolve();
        f.callbacks.shift()?.();
        assert.equal(f.socket.readyState, 1);
        assert.equal(descendants(f.root).find(item => item.tag === 'canvas'), canvas);
        assert.equal(canvas.drawn.at(-1), (i + 1) * 100000);
    }
    assert.equal(flushed, 100);
    assert.equal(released, outputs);
    assert.equal(f.decoders.length, 1);
    assert.equal(f.socket.sent.filter(input => input[0] === 6).length, 1);
    assert.equal(canvas.drawn.length, 100, 'replayed images must not be presented twice');
});

test('decode dequeue wakes the pending stream even before any decoded output exists', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    decoder.decodeQueueSize = 2;
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    assert.equal(decoder.decoded.length, 0);
    decoder.decodeQueueSize = 0;
    decoder.ondequeue();
    assert.equal(decoder.decoded.length, 1);
});

test('short independent intervals bound decode work across repeated interaction pauses', async t =>
{
    const work = [];
    for (const keyInterval of [120, 3])
    {
        await t.test('key interval ' + keyInterval, async child =>
        {
            const f = await fixtureAsync(child), decoder = f.decoders[0];
            let pending, needsKey = true, released = 0;
            decoder.decode = chunk =>
            {
                if (needsKey)
                {
                    assert.equal(chunk.type, 'key');
                }
                needsKey = false;
                decoder.decoded.push(chunk);
                if (pending != null)
                {
                    decoder.callbacks.output({ timestamp: pending,

                        /** 対象を閉じ、関連する状態を整理します。 */
                        close()
                        {
                            released++;
                        } });
                }
                pending = chunk.timestamp;
            };
            decoder.flush = async () =>
            {
                needsKey = true;
                if (pending != null)
                {
                    decoder.callbacks.output({ timestamp: pending,

                        /** 対象を閉じ、関連する状態を整理します。 */
                        close()
                        {
                            released++;
                        } });
                }
                pending = null;
            };
            const canvas = descendants(f.root).find(item => item.tag === 'canvas');
            for (let i = 0; i < 100; i++)
            {
                const key = i % keyInterval === 0;
                f.socket.onmessage(frame(2, 0, key ? keyPicture : new Uint8Array([0, 0, 1, 0x41]), key, (i + 1) * 100000));
                f.socket.onmessage(frame(9, 0, new Uint8Array(8)));
                f.advance(100);
                await Promise.resolve();
                f.callbacks.shift()?.();
                assert.equal(canvas.drawn.at(-1), (i + 1) * 100000);
            }
            assert.equal(canvas.drawn.length, 100);
            assert.equal(released, decoder.decoded.length);
            assert.equal(f.socket.sent.filter(input => input[0] === 6).length, 1);
            work.push(decoder.decoded.length);
        });
    }

    // At 100 ms between updates, a 250 ms reference interval yields a key
    // every third arriving frame, without manufacturing frames while idle.
    assert.deepEqual(work, [5050, 199]);
});

test('a delta arriving during flush is replayed after its promise settles', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    let finish;
    decoder.flush = () => new Promise(resolve =>
    {
        finish = resolve;
    });
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    f.advance(75);
    f.socket.onmessage(frame(2, 0, new Uint8Array([0, 0, 1, 0x41]), false, 2000));
    assert.equal(decoder.decoded.length, 1);
    decoder.callbacks.output({ timestamp: 1000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    finish();
    await Promise.resolve();
    assert.deepEqual(decoder.decoded.map(chunk => [chunk.type, chunk.timestamp]), [['key', 1000], ['key', 1000], ['delta', 2000]]);
    assert.equal(f.decoders.length, 1);
});

test('slow replayed reference output counts as decoder progress without displaying duplicates', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0], pending = [];
    let released = 0;
    decoder.decode = chunk =>
    {
        decoder.decoded.push(chunk);
        pending.push(chunk.timestamp);
    };
    decoder.flush = async() =>
    {
        while (pending.length)
        {
            decoder.callbacks.output({ timestamp: pending.shift(),

                /** 対象を閉じ、関連する状態を整理します。 */
                close()
                {
                    released++;
                } });
        }
    };
    for (let i = 0;i < 8;i++)
    {
        f.socket.onmessage(frame(2, 0, i === 0 ? keyPicture : new Uint8Array([0, 0, 1, 0x41]), i === 0, 1_000_000 + i * 10000));
    }
    f.advance(75);
    await Promise.resolve();
    f.callbacks.shift()?.();
    const canvas = descendants(f.root).find(item => item.tag === 'canvas');
    assert.equal(canvas.drawn.at(-1), 1_070_000);
    f.socket.onmessage(frame(2, 0, new Uint8Array([0, 0, 1, 0x41]), false, 1_100_000));
    decoder.decodeQueueSize = 1;
    while (pending.length)
    {
        decoder.callbacks.output({ timestamp: pending.shift(),

            /** 対象を閉じ、関連する状態を整理します。 */
            close()
            {
                released++;
            } });
        f.socket.onmessage(frame(9, 0, new Uint8Array(8)));
        f.advance(400);
        f.callbacks.shift()?.();
        assert.equal(f.decoders.length, 1);
        assert.equal(f.socket.readyState, 1);
    }
    assert.deepEqual(canvas.drawn, [1_070_000, 1_100_000]);
    assert.equal(released, 17);
});

test('bounded GOP overflow waits for a natural key without restarting capture', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    decoder.decode = chunk =>
    {
        decoder.decoded.push(chunk);
        decoder.callbacks.output({ timestamp: chunk.timestamp,

            /** 対象を閉じ、関連する状態を整理します。 */
            close()
            {} });
    };
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    const largeDelta = new Uint8Array(1024 * 1024);
    largeDelta.set([0, 0, 1, 0x41]);
    for (let i = 2;i <= 10;i++)
    {
        f.socket.onmessage(frame(2, 0, largeDelta, false, i * 1000));
    }
    decoder.decode = chunk => decoder.decoded.push(chunk);
    decoder.flush = async () => decoder.callbacks.output({ timestamp: 11000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    f.socket.onmessage(frame(2, 0, largeDelta, false, 11000));
    f.advance(75);
    await Promise.resolve();
    f.socket.onmessage(frame(2, 0, largeDelta, false, 12000));
    assert.equal(decoder.decoded.length, 11);
    f.socket.onmessage(frame(2, 0, keyPicture, true, 13000));
    assert.equal(decoder.decoded.at(-1).timestamp, 13000);
    assert.equal(f.socket.sent.filter(input => input[0] === 6).length, 1);
    assert.equal(f.socket.readyState, 1);
});

test('a flush completing after disconnect releases frames and cannot restart capture', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    let finish, released = 0;
    decoder.flush = () => new Promise(resolve =>
    {
        finish = resolve;
    });
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    f.advance(75);
    await f.click('Disconnect');
    decoder.callbacks.output({ timestamp: 1000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {
            released++;
        } });
    finish();
    await Promise.resolve();
    assert.equal(released, 1);
    assert.equal(f.socket.readyState, 3);
    assert.equal(f.decoders.length, 1);
    assert.equal(f.notices.filter(Boolean).length, 0);
});

test('hidden frames are released and resumption requires a new selection and keyframe', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    let released = 0;
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    decoder.callbacks.output({ timestamp: 1000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {
            released++;
        } });
    document.hidden = true;
    f.documentEvents.visibilitychange();
    assert.equal(released, 1);
    assert.equal(decoder.state, 'closed');
    f.socket.onmessage(frame(2, 0, keyPicture, true, 2000));
    document.hidden = false;
    f.documentEvents.visibilitychange();
    const next = f.decoders.at(-1);
    assert.notEqual(next, decoder);
    f.socket.onmessage(frame(2, 0, keyPicture, true, 3000));
    assert.equal(next.decoded.length, 0);
    f.describe();
    f.socket.onmessage(frame(2, 0, keyPicture, true, 4000));
    assert.equal(next.decoded.length, 1);
});

test('blur releases held input without stopping visible video and focus resumes audio', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    let resumed = 0;
    f.contexts[0].state = 'suspended';
    f.contexts[0].resume = async () =>
    {
        resumed++;
        f.contexts[0].state = 'running';
    };
    f.windowEvents.blur();
    assert.equal(f.socket.sent.at(-1)[0], 5);
    assert.notEqual(decoder.state, 'closed');
    f.windowEvents.focus();
    await Promise.resolve();
    assert.equal(resumed, 1);
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    assert.equal(decoder.decoded.length, 1);
});

test('stale audio clock cannot freeze a decoded image indefinitely', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    f.audioNodes[0].port.onmessage({ data: { source: 1000000, context: 0, rate: 1 } });
    f.socket.onmessage(frame(2, 0, keyPicture, true, 2000000));
    decoder.callbacks.output({ timestamp: 2000000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    f.callbacks.shift()();
    const canvas = descendants(f.root).find(item => item.tag === 'canvas');
    assert.equal(canvas.drawn.length, 0);
    f.advance(300);
    assert.deepEqual(canvas.drawn, [2000000]);
});

test('audio underruns during delayed video do not restart capture or disconnect the session', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    decoder.decode = chunk =>
    {
        decoder.decoded.push(chunk);
        decoder.callbacks.output({ timestamp: chunk.timestamp,

            /** 対象を閉じ、関連する状態を整理します。 */
            close()
            {} });
    };
    let held = 1_000_000;
    for (let i = 0;i < 800;i++)
    {
        const elapsed = i * 0.025, playing = Math.floor(elapsed / 4) % 2 === 0;
        f.contexts[0].currentTime = elapsed;
        if (playing)
        {
            held = 1_000_000 + elapsed * 1e6 - 200_000;
        }
        f.audioNodes[0].port.onmessage({ data: { source: held, context: elapsed, rate: playing ? 1 : 0 } });

        // Sparse VFR input allows the old held clock to trigger its output timeout
        // instead of continuously hiding it behind compressed-queue overflows.
        if (i % 80 === 0)
        {
            f.socket.onmessage(frame(2, 0, keyPicture, true, 1_000_000 + elapsed * 1e6));
        }
        f.callbacks.shift()?.();
        f.advance(25);
        assert.equal(f.socket.readyState, 1);
        assert.equal(f.decoders.length, 1, 'silence is not a video decoder fault');
    }
    const canvas = descendants(f.root).find(item => item.tag === 'canvas');
    assert.ok((8) <= (canvas.drawn.length));
    assert.equal(f.audioNodes[0].messages.filter(message => message?.type === 'reset').length, 0);
});

test('buffered silence advances the audio clock and still gates video presentation', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    f.audioNodes[0].port.onmessage({ data: { source: 1_000_000, context: 0, rate: 1 } });
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1_010_000));
    decoder.callbacks.output({ timestamp: 1_010_000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    f.callbacks.shift()();
    const canvas = descendants(f.root).find(item => item.tag === 'canvas');
    assert.equal(canvas.drawn.length, 0);
    f.contexts[0].currentTime = 0.06;
    f.audioNodes[0].port.onmessage({ data: { source: 1_060_000, context: 0.06, rate: 1 } });
    f.callbacks.shift()();
    assert.deepEqual(canvas.drawn, [1_010_000]);
});

test('intentional audio synchronization wait is not a decoder or first-frame timeout', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    decoder.decode = chunk => decoder.callbacks.output({ timestamp: chunk.timestamp,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    f.audioNodes[0].port.onmessage({ data: { source: 1_000_000, context: 0, rate: 1 } });
    f.socket.onmessage(frame(2, 0, keyPicture, true, 5_000_000));
    for (let i = 0;i < 200;i++)
    {
        const elapsed = i * 0.025;
        f.contexts[0].currentTime = elapsed;
        f.audioNodes[0].port.onmessage({ data: { source: 1_000_000 + elapsed * 1e6, context: elapsed, rate: 1 } });
        f.callbacks.shift()?.();
        f.advance(25);
        assert.equal(f.decoders.length, 1);
        assert.equal(f.socket.readyState, 1);
    }
    assert.deepEqual(descendants(f.root).find(item => item.tag === 'canvas').drawn, [5_000_000]);
});

test('desktop starts at 30 fps and 4096 kbps without changing the supported limits', async t =>
{
    const f = await fixtureAsync(t), fields = descendants(f.root).filter(item => item.tag === 'input');
    assert.equal(fields[0].value, '30');
    assert.equal(fields[1].value, '4096');
    assert.equal(fields[0].max, '120');
    assert.equal(fields[1].max, '100000');
    assert.equal(f.socket.sent.find(input => input[0] === 6)[5], (30 << 20) | 4096 | 0x10000000);
    assert.equal(fields[2].value, '12000');
    assert.equal(f.initialCodec, 'vp9');
});

test('statistics overlay reports displayed frames and can be hidden without reconnecting', async t =>
{
    const f = await fixtureAsync(t), overlay = descendants(f.root).find(item => item.className === 'desktop-stats');
    const button = descendants(f.root).find(item => item.tag === 'button' && item.getAttribute('aria-label') === 'Statistics overlay');
    assert.equal(overlay.hidden, false);
    assert.equal(button.getAttribute('aria-pressed'), 'true');
    for (const timestamp of [1000, 2000])
    {
        f.socket.onmessage(frame(2, 0, keyPicture, true, timestamp));
        f.decoders[0].callbacks.output({ timestamp,

            /** 対象を閉じ、関連する状態を整理します。 */
            close()
            {} });
        f.callbacks.shift()();
    }
    f.advance(1000);
    assert.equal(overlay.children[1].children[1].textContent, '2');
    assert.equal(overlay.children[2].children[0].textContent, 'Send → receive');
    assert.equal(overlay.children[2].children[1].textContent, '—');
    assert.equal(overlay.children[3].children[0].textContent, 'Receive → display');
    assert.equal(f.socket.sent.some(input => input[0] === 14), false);
    assert.equal(overlay.children[4].children[1].textContent, '4096 kbps');
    assert.equal(overlay.children[5].children[1].textContent, 'VP9');
    assert.ok((0) < (parseFloat(overlay.children[0].children[1].textContent)));
    assert.equal(overlay.parentElement.className, 'desktop-viewport');
    await f.click('Statistics overlay');
    assert.equal(overlay.hidden, true);
    assert.equal(button.getAttribute('aria-pressed'), 'false');
    assert.equal(f.decoders.length, 1);
    await f.click('Statistics overlay');
    assert.equal(overlay.hidden, false);
    assert.equal(button.getAttribute('aria-pressed'), 'true');
    assert.equal(f.decoders.length, 1);
});

test('the statistics overlay measures the complete video transfer after Core clock calibration', async t =>
{
    const f = await fixtureAsync(t, 'h264', { timing: true, experimental: true, viewOnly: true, viewOnlySupported: true });
    const probe = f.socket.sent.find(input => input[0] === 14);
    assert(probe);
    f.advance(11);
    const reply = new Uint8Array(24), data = new DataView(reply.buffer);
    data.setInt32(0, probe[1]);
    data.setBigInt64(8, 0n);
    data.setBigInt64(16, 1000005000n);
    f.socket.onmessage(frame(13, 0, reply, false, 1000006000));
    const marker = new Uint8Array(8);
    new DataView(marker.buffer).setBigInt64(0, 1234n);
    f.socket.onmessage(frame(14, 0, marker, false, 1000020000));
    f.advance(46);
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1234));
    f.advance(1000);
    const overlay = descendants(f.root).find(item => item.className === 'desktop-stats');
    assert.equal(overlay.children[2].children[1].textContent, '37 ms');
    assert.equal(f.socket.readyState, 1);
    await f.click('Disconnect');
    assert.equal(overlay.children[2].children[1].textContent, '—');
});

test('Escape closes an open controls panel before affecting fullscreen or remote keys', async t =>
{
    const f = await fixtureAsync(t), menu = descendants(f.root).find(item => item.className === 'desktop-menu');
    menu.open = true;
    const before = f.socket.sent.length;
    let handled = 0;
    f.documentEvents.keydown({ key: 'Escape',

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {
            handled++;
        },

        /** イベント伝搬を止めたことを記録します。 */
        stopPropagation()
        {
            handled++;
        } });
    assert.equal(menu.open, false);
    assert.equal(handled, 2);
    assert.equal(f.socket.sent.length, before);
});

const hevcPicture = new Uint8Array([0, 0, 0, 1, 0x42, 1, 1, 1, 0x60, 0, 0, 3, 0, 0xb0, 0, 0, 3, 0, 0, 3, 0, 120, 0, 0, 0, 1, 0x26, 1, 0xaa]);
test('explicit experimental H.265 selection configures from the actual SPS', async t =>
{
    const f = await fixtureAsync(t, 'h265');
    assert.equal(f.socket.sent.find(input => input[0] === 6)[2], 2);
    f.socket.onmessage(frame(2, 0, hevcPicture, true, 1000));
    assert.equal(f.decoders[0].configuration.codec, 'hvc1.1.6.L120.B0');
    assert.equal(f.decoders[0].decoded.length, 1);
});

test('unsupported H.265 switches to H.264 once while preserving audio and transport', async t =>
{
    const f = await fixtureAsync(t, 'h265');
    VideoDecoder.isConfigSupported = async config => ({ supported: config.codec.startsWith('avc1'), config });
    f.socket.onmessage(frame(2, 0, hevcPicture, true, 1000));
    for (let i = 0;i < 5;i++)
    {
        await Promise.resolve();
    }
    assert.equal(f.socket.readyState, 1);
    assert.equal(f.contexts.length, 1);
    assert.equal(f.contexts[0].state, 'running');
    assert.equal(f.socket.sent.filter(input => input[0] === 6).length, 2);
    assert.equal(f.socket.sent.at(-1)[2], 0);
    assert.match(f.notices.at(-1), /Using H.264/);
    f.describe();
    f.socket.onmessage(frame(2, 0, keyPicture, true, 2000));
    for (let i = 0;i < 5;i++)
    {
        await Promise.resolve();
    }
    assert.equal(f.decoders.at(-1).decoded.length, 1);
});

test('a positive hardware support probe followed by configure rejection retries the standard path', async t =>
{
    const f = await fixtureAsync(t, 'h265'), decoder = f.decoders[0], attempts = [];
    VideoDecoder.isConfigSupported = async config => ({ supported: true, config: { ...config } });
    const configure = decoder.configure.bind(decoder);
    decoder.configure = config =>
    {
        attempts.push(config.hardwareAcceleration);
        if (config.hardwareAcceleration === 'prefer-hardware')
        {
            throw new DOMException('Unsupported configuration. Check isConfigSupported() prior to calling configure().', 'NotSupportedError');
        }
        configure(config);
    };
    f.socket.onmessage(frame(2, 0, hevcPicture, true, 1000));
    for (let i = 0;i < 6;i++)
    {
        await Promise.resolve();
    }
    assert.deepEqual(attempts, ['prefer-hardware', 'no-preference']);
    assert.equal(f.decoders.length, 1);
    assert.equal(decoder.decoded.length, 1);
    assert.equal(f.socket.readyState, 1);
});

test('configure rejection on both HEVC paths falls back once and retains the diagnostic', async t =>
{
    const f = await fixtureAsync(t, 'h265'), decoder = f.decoders[0];
    VideoDecoder.isConfigSupported = async config => ({ supported: true, config: { ...config } });
    decoder.configure = () =>
    {
        throw new DOMException('Unsupported configuration. Check isConfigSupported() prior to calling configure().', 'NotSupportedError');
    };
    f.socket.onmessage(frame(2, 0, hevcPicture, true, 1000));
    for (let i = 0;i < 6;i++)
    {
        await Promise.resolve();
    }
    assert.equal(f.socket.readyState, 1);
    assert.equal(f.contexts[0].state, 'running');
    assert.equal(f.decoders.length, 2);
    assert.equal(f.socket.sent.at(-1)[2], 0);
    assert.ok(descendants(f.root).some(item => item.tag === 'p' && item.textContent.includes('Unsupported configuration')));
    decoder.callbacks.error(new Error('late failure'));
    assert.equal(f.decoders.length, 2);
});

for (const asynchronous of [false, true])
{
    test(`HEVC decode failure (${asynchronous ? 'callback' : 'synchronous'}) preserves the connection`, async t =>
    {
        const f = await fixtureAsync(t, 'h265'), decoder = f.decoders[0];
        const error = new DOMException('Decoding error', 'EncodingError');
        if (!asynchronous)
        {
            decoder.decode = () =>
            {
                throw error;
            };
        }
        f.socket.onmessage(frame(2, 0, hevcPicture, true, 1000));
        if (asynchronous)
        {
            decoder.callbacks.error(error);
        }
        assert.equal(f.socket.readyState, 1);
        assert.equal(f.contexts[0].state, 'running');
        assert.equal(f.decoders.length, 2);
        assert.equal(f.socket.sent.at(-1)[2], 0);
        assert.equal(f.audioNodes[0].messages.filter(message => message?.type === 'reset').length, 0);
    });
}

test('quiet healthy desktop stays connected while a silent broken connection times out', async t =>
{
    const f = await fixtureAsync(t);
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    f.decoders[0].callbacks.output({ timestamp: 1000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    for (let i = 0;i < 20;i++)
    {
        f.socket.onmessage(frame(9, 0, new Uint8Array(8)));
        f.advance(1000);
    }
    assert.equal(f.socket.readyState, 1);
    f.advance(9000);
    assert.equal(f.socket.readyState, 3);
    assert.match(f.notices.at(-1), /timed out/);
});

test('VP9 selection reaches the Core and uses a VP9 hardware-preferred decoder', async t =>
{
    const f = await fixtureAsync(t, 'vp9');
    assert.equal(f.socket.sent.find(input => input[0] === 6)[2], 1);
    f.socket.onmessage(frame(2, 0, new Uint8Array([0x82, 1, 2, 3]), true, 1000));
    assert.equal(f.decoders[0].configuration.codec, 'vp09.00.40.08');
    assert.equal(f.decoders[0].configuration.hardwareAcceleration, 'prefer-hardware');
});

test('fps, bitrate and audio capacity are applied and acknowledged for the connection', async t =>
{
    const f = await fixtureAsync(t, 'vp9', { fps: 30, bitrate: 2500, audioSamples: 24000 });
    const selection = f.socket.sent.find(input => input[0] === 6);
    assert.equal(selection[5], (30 << 20) | 2500 | 0x10000000);
    assert.equal(f.audioNodes[0].configuration.processorOptions.maxBufferSamples, 24000);
    f.socket.onmessage(frame(2, 0, new Uint8Array([0x82, 1, 2, 3]), true, 1000));
    assert.equal(f.decoders[0].configuration.codec, 'vp09.00.40.08');
    assert.ok(descendants(f.root).some(item => item.textContent?.includes('30 fps · 2500 kbps')));
    assert.ok(descendants(f.root).some(item => item.textContent?.includes('Administrator input')));
});

test('an older Core cannot silently ignore requested video settings', async t =>
{
    const f = await fixtureAsync(t);
    f.socket.onmessage(frame(3, 0, [{ id: 0, codec: 0, encoder: 'h264_nvenc' }]));
    assert.equal(f.socket.readyState, 3);
    assert.match(f.notices.at(-1), /Update the Core/);
});

test('unsupported hardware preference falls back only after a capability query', async t =>
{
    const f = await fixtureAsync(t);
    const queries = [];
    VideoDecoder.isConfigSupported = async config =>
    {
        queries.push({ ...config });
        return { supported: config.hardwareAcceleration === 'no-preference', config };
    };
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    for (let i = 0;i < 4;i++)
    {
        await Promise.resolve();
    }
    assert.deepEqual(queries.map(value => value.hardwareAcceleration), ['prefer-hardware', 'no-preference']);
    assert.equal(f.decoders[0].decoded.length, 1);
    assert.equal(f.decoders[0].configuration.hardwareAcceleration, 'no-preference');
});

test('an unresponsive decoder is bounded to three resynchronizations, then reports disconnect', async t =>
{
    const f = await fixtureAsync(t);
    for (let i = 0;i < 4;i++)
    {
        f.socket.onmessage(frame(2, 0, keyPicture, true, (i + 1) * 1000));
        f.decoders.at(-1).decodeQueueSize = 1;
        f.socket.onmessage(frame(9, 0, new Uint8Array(8)));
        f.advance(1600);
        if (f.socket.readyState === 1)
        {
            f.describe();
        }
    }
    assert.equal(f.socket.readyState, 3);
    assert.match(f.notices.at(-1), /Video stopped responding/);
    assert.equal(f.audioNodes[0].messages.filter(message => message?.type === 'reset').length, 0);
});

test('a lost animation callback cannot leave decoded video frozen while visible', async t =>
{
    const f = await fixtureAsync(t);
    f.socket.onmessage(frame(2, 0, keyPicture, true, 1000));
    f.decoders[0].callbacks.output({ timestamp: 1000,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {} });
    f.advance(300);
    const canvas = descendants(f.root).find(item => item.tag === 'canvas');
    assert.deepEqual(canvas.drawn, [1000]);
});

test('real audio device latency preserves AV timing without filling decoded frames forever', async t =>
{
    const f = await fixtureAsync(t), decoder = f.decoders[0];
    f.contexts[0].baseLatency = 0.15;
    f.contexts[0].outputLatency = 0.01;
    decoder.decode = chunk =>
    {
        decoder.decoded.push(chunk);
        decoder.callbacks.output({ timestamp: chunk.timestamp,

            /** 対象を閉じ、関連する状態を整理します。 */
            close()
            {} });
    };
    for (let i = 0;i < 60;i++)
    {
        f.contexts[0].currentTime = i / 60;
        f.audioNodes[0].port.onmessage({ data: { source: 1000000 + i * 1000000 / 60, context: i / 60, rate: 1 } });
        f.socket.onmessage(frame(2, 0, i === 0 ? keyPicture : new Uint8Array([0, 0, 1, 0x41]), i === 0, 1000000 + i * 16667));
        f.callbacks.shift()?.();
        f.advance(16.667);
    }
    const canvas = descendants(f.root).find(item => item.tag === 'canvas');
    assert.ok((30) < (canvas.drawn.length));
    assert.equal(f.decoders.length, 1);
    assert.equal(f.socket.readyState, 1);
    assert.ok(canvas.drawn.at(-1) < 1000000 + 50 * 16667);
});

test('Opus selection waits for capability and bitrate confirmation before playing', async t =>
{
    const f = await fixtureAsync(t, 'h264', { audioCodec: 'opus' });
    f.socket.onmessage(frame(4, 0, { sampleRate: 48000, channels: 2, format: 'f32le', codecs: ['f32le', 'opus'], opusBitrates: [64] }));
    assert.deepEqual(Array.from(f.socket.sent.find(input => input[0] === 7)), [7, 1, 64, 0, 0, 0]);
    assert.ok(f.socket.sent.findIndex(input => input[0] === 7) < f.socket.sent.findIndex(input => input[0] === 6));
    f.socket.onmessage(frame(4, 0, { sampleRate: 48000, channels: 2, format: 'opus', bitrateKbps: 64 }));
    assert.match(f.root.textContent, /Opus 64 kbps/);
    assert.equal(f.socket.readyState, 1);
    const fields = descendants(f.root).filter(item => item.tag === 'select' && item.options.some(option => option.value === 'opus' || option.textContent === '64 kbps'));
    assert.ok(fields.every(item => item.disabled));
});

test('the untouched audio defaults negotiate Opus at 128 kbps before video and cursor subscription', async t =>
{
    const f = await fixtureAsync(t, null, { defaultAudio: true, cursorRelay: true, cursorCore: true, mousePresent: true });
    assert.deepEqual(f.initialAudio, { codec: 'opus', bitrate: '128' });
    assert.equal(f.socket.sent.filter(input => input[0] === 6).length, 0);
    f.socket.onmessage(frame(4, 0, { sampleRate: 48000, channels: 2, format: 'f32le', codecs: ['f32le', 'opus'], opusBitrates: [64, 128] }));
    assert.deepEqual(Array.from(f.socket.sent.find(input => input[0] === 7)), [7, 1, 128, 0, 0, 0]);
    assert.ok(f.socket.sent.findIndex(input => input[0] === 7) < f.socket.sent.findIndex(input => input[0] === 6));
    f.socket.onmessage(frame(4, 0, { sampleRate: 48000, channels: 2, format: 'opus', bitrateKbps: 128 }));
    f.describe();
    assert.match(f.root.textContent, /Opus 128 kbps/);
    assert.equal(f.socket.sent.filter(input => input[0] === 8).length, 1);
    assert.equal(f.socket.readyState, 1);
});

test('Opus never silently falls back when the Core lacks support', async t =>
{
    const f = await fixtureAsync(t, 'h264', { audioCodec: 'opus' });
    f.socket.onmessage(frame(4, 0, { sampleRate: 48000, channels: 2, format: 'f32le' }));
    assert.equal(f.socket.readyState, 3);
    assert.match(f.notices.at(-1), /Update the Core/);
    assert.equal(f.socket.sent.filter(value => value[0] === 7).length, 0);
});

test('unconfirmed Opus selection times out without leaving a silent connected session', async t =>
{
    const f = await fixtureAsync(t, 'h264', { audioCodec: 'opus' });
    f.socket.onmessage(frame(4, 0, { sampleRate: 48000, channels: 2, format: 'f32le', codecs: ['f32le', 'opus'], opusBitrates: [64] }));
    f.advance(5100);
    assert.equal(f.socket.readyState, 3);
    assert.match(f.notices.at(-1), /Audio selection/);
});

test('a browser without Opus decoding offers PCM only and defaults to it', async t =>
{
    const f = await fixtureAsync(t, 'h264', { defaultAudio: true, opusSupported: false });
    assert.deepEqual(f.initialAudio, { codec: 'f32le', bitrate: '128' });
    const selector = descendants(f.root).find(element => element.getAttribute('aria-label') === 'Audio codec');
    assert.equal(selector.children.some(option => option.value === 'opus'), false);
});

test('experimental features are absent by default and VP9 is the initial codec', async t =>
{
    const f = await fixtureAsync(t, null), all = descendants(f.root);
    assert.equal(f.initialCodec, 'vp9');
    assert.equal(f.socket.sent.findLast(input => input[0] === 6)[2], 1);
    assert.equal(all.some(item => item.getAttribute('aria-label') === 'Cluster'), false);
    assert.equal(all.some(item => item.tag === 'option' && item.value === 'h264'), false);
    assert.equal(all.some(item => item.tag === 'option' && item.value === 'h265'), false);
    assert.equal(all.some(item => item.getAttribute('aria-label') === 'Hide host cursor in video'), false);
    assert.equal(all.some(item => item.getAttribute('aria-label') === 'Codec details'), false);
    const toolbar = all.find(item => item.className === 'desktop-toolbar'), labels = toolbar.children.map(item => item.getAttribute('aria-label'));
    assert.equal(labels.indexOf('Follow pointer'), labels.indexOf('Dot by dot (1:1)') + 1);
});

test('one normal cursor control negotiates synchronization and captured visibility together', async t =>
{
    const f = await fixtureAsync(t, null, { cursorRelay: true, cursorCore: true, cursorVisibility: true }), all = descendants(f.root);
    assert.equal(f.initialCodec, 'vp9');
    assert.equal(all.some(item => item.getAttribute('aria-label') === 'Hide host cursor in video'), false);
    const button = f.cursorSync;
    f.describe();
    assert.equal(button.disabled, false);
    assert.equal(button.getAttribute('aria-pressed'), 'false');
    await button.onclick();
    assert.equal(button.disabled, true);
    assert.equal(f.cursorSync.getAttribute('aria-pressed'), 'true');
    assert.ok(f.socket.sent.findLast(input => input[0] === 6)[5] & 0x08000000);
    f.describe();
    assert.equal(button.disabled, false);
    assert.equal(button.getAttribute('aria-pressed'), 'true');
    await button.onclick();
    assert.equal(f.socket.sent.findLast(input => input[0] === 6)[5] & 0x08000000, 0);
    f.describe();
    assert.equal(f.cursorSync.getAttribute('aria-pressed'), 'false');
    await f.click('Disconnect');
    assert.equal(button.getAttribute('aria-pressed'), 'false');
    assert.equal(button.disabled, true);
});

test('unified cursor control is disabled for an older Core', async t =>
{
    const f = await fixtureAsync(t, null), button = f.cursorSync;
    f.describe();
    assert.equal(button.disabled, true);
    const before = f.socket.sent.length;
    await button.onclick();
    assert.equal(f.socket.sent.length, before);
});

test('cursor defaults on only when host mouse presence is explicitly confirmed', async t =>
{
    for (const mousePresent of [true, false, null, undefined, 'true'])
    {
        await t.test(String(mousePresent), async t =>
        {
            const f = await fixtureAsync(t, null, { mousePresent, cursorRelay: true, cursorCore: true });
            const expected = mousePresent === true;
            assert.equal(f.cursorSync.getAttribute('aria-pressed'), String(expected));
            assert.equal(!!(f.socket.sent.findLast(input => input[0] === 6)[5] & 0x08000000), expected);
            assert.equal(f.socket.sent.filter(input => input[0] === 8).length, expected ? 1 : 0);
        });
    }
});

test('confirmed host mouse does not hide the captured cursor through an unsupported relay', async t =>
{
    const f = await fixtureAsync(t, null, { mousePresent: true, cursorRelay: false, cursorCore: true });
    assert.equal(f.cursorSync.getAttribute('aria-pressed'), 'false');
    assert.equal(f.cursorSync.disabled, true);
    assert.equal(f.socket.sent.findLast(input => input[0] === 6)[5] & 0x08000000, 0);
});

test('automatic reconnect preserves an explicit off choice on a host with a mouse', async t =>
{
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const f = await fixtureAsync(t, null, { mousePresent: true, cursorRelay: true, cursorCore: true });
    f.cursorSync.onclick();
    f.describe();
    f.socket.onclose({ code: 1006, reason: '' });
    t.mock.timers.tick(0);
    await new Promise(setImmediate);
    const recovered = f.sockets.at(-1);
    assert.notEqual(recovered, f.socket);
    recovered.onmessage(frame(7, 0, { clusterId: 'cluster', hostId: 'host', codecDetails: true, variableBitrate: true, cursorShapes: true }));
    recovered.onmessage(frame(1, 0, [{ id: 0, name: 'Screen A', width: 1920, height: 1080, primary: true, mousePresent: true }]));
    assert.equal(recovered.sent.findLast(input => input[0] === 6)[5] & 0x08000000, 0);
    assert.equal(f.cursorSync.getAttribute('aria-pressed'), 'false');
});

test('icon controls and display tabs replace the old sidebar screen menu', async t =>
{
    const f = await fixtureAsync(t), all = descendants(f.root), toolbar = all.find(e => e.className === 'desktop-toolbar'), rail = all.find(e => e.className === 'desktop-rail');
    for (const name of ['Cursor synchronization', 'Dot by dot (1:1)', 'Follow pointer', 'Mute audio'])
    {
        const button = all.find(e => e.getAttribute('aria-label') === name);
        assert.equal(button.tag, 'button');
        assert.equal(button.parentElement, toolbar);
        assert.equal(button.textContent, '');
        assert.ok(button.title);
    }
    assert.equal(descendants(rail).some(e => e.getAttribute('aria-label') === 'Displays & scale'), false);
    const tabs = all.find(e => e.className === 'desktop-screen-tabs'), bar = all.find(e => e.className === 'desktop-keybar');
    assert.equal(tabs.parentElement, toolbar);
    assert.equal(toolbar.children[toolbar.children.indexOf(bar) - 1].className, 'desktop-key-separator');
    const keyboard = toolbar.children[toolbar.children.indexOf(bar) - 2];
    assert.equal(keyboard.getAttribute('aria-label'), 'Show software keyboard');
    assert.equal(toolbar.children[toolbar.children.indexOf(keyboard) - 1].getAttribute('aria-label'), 'View-only mode');
    assert.equal(toolbar.children[toolbar.children.indexOf(keyboard) - 2].getAttribute('aria-label'), 'Automatic reconnect');
    assert.equal(toolbar.children[toolbar.children.indexOf(keyboard) - 3].className, 'desktop-key-separator');
    assert.equal(toolbar.children[toolbar.children.indexOf(keyboard) - 4], tabs);
    const first = tabs.children.find(e => e.getAttribute('aria-label') === 'Display 1: Screen A');
    assert.equal(first.getAttribute('aria-pressed'), 'true');
    assert.equal(first.children[0].className, 'desktop-screen-symbol');
    assert.equal(first.children[0].children[1].textContent, '1');
    await f.click('Display 2: Screen B');
    assert.equal(tabs.children.find(e => e.getAttribute('aria-label') === 'Display 2: Screen B').getAttribute('aria-pressed'), 'true');
    await f.click('Disconnect');
    assert.equal(tabs.children.length, 0);
});

test('software keyboard focuses synchronously, commits IME once and sends Unicode scalars', async t =>
{
    const f = await fixtureAsync(t), input = descendants(f.root).find(e => e.tag === 'textarea');
    await f.click('Show software keyboard');
    assert.equal(document.activeElement, input);
    assert.equal(input.hidden, false);
    const before = f.socket.sent.length;
    input.events.compositionstart({});
    input.value = '\u200b日本🚀';
    input.events.input({ inputType: 'insertCompositionText', isComposing: true });
    assert.equal(f.socket.sent.length, before);
    input.events.compositionend({ data: '日本🚀' });
    input.value = '\u200b日本🚀';
    input.events.input({ inputType: 'insertFromComposition', data: '日本🚀' });
    for (let i = 0;i < f.callbacks.length;i++)
    {
        f.callbacks[i]();
    }
    assert.deepEqual(f.socket.sent.slice(before).filter(item => item[0] === 10).map(item => item[4]), [0x65e5, 0x672c, 0x1f680]);
    await f.click('Hide software keyboard');
    assert.equal(input.hidden, true);
    assert.equal(document.activeElement, null);
});

test('software keyboard supports deletion, enter and virtual control without changing ordinary text into physical keys', async t =>
{
    const f = await fixtureAsync(t), input = descendants(f.root).find(e => e.tag === 'textarea');
    await f.click('Show software keyboard');
    let prevented = 0;
    input.events.beforeinput({ inputType: 'deleteContentBackward', cancelable: true,

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {
            prevented++;
        } });
    input.events.beforeinput({ inputType: 'insertLineBreak', cancelable: true,

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {
            prevented++;
        } });
    for (let i = 0;i < f.callbacks.length;i++)
    {
        f.callbacks[i]();
    }
    assert.equal(prevented, 2);
    assert.deepEqual(f.socket.sent.filter(item => item[0] === 4).map(item => [item[4], item[5]]), [[8, 1], [8, 0], [13, 1], [13, 0]]);
    await f.click('Ctrl');
    input.value = '\u200ba';
    input.events.input({ inputType: 'insertText' });
    for (let i = 0;i < f.callbacks.length;i++)
    {
        f.callbacks[i]();
    }
    assert.deepEqual(Array.from(f.socket.sent.at(-2)).slice(4), [65, 1]);
    assert.deepEqual(Array.from(f.socket.sent.at(-1)).slice(4), [65, 0]);
    assert.equal(descendants(f.root).find(e => e.textContent === 'Ctrl').getAttribute('aria-pressed'), 'true');
});

test('cancelled composition and hidden or disconnected keyboard never replay queued text', async t =>
{
    const f = await fixtureAsync(t), input = descendants(f.root).find(e => e.tag === 'textarea');
    await f.click('Show software keyboard');
    input.events.compositionstart({});
    input.value = '\u200b未確定';
    input.events.compositionend({ data: '' });
    assert.equal(f.socket.sent.some(item => item[0] === 10), false);
    input.value = '\u200b' + 'x'.repeat(64);
    input.events.input({ inputType: 'insertText' });
    await f.click('Hide software keyboard');
    for (let i = 0;i < f.callbacks.length;i++)
    {
        f.callbacks[i]();
    }
    assert.equal(f.socket.sent.filter(item => item[0] === 10).length, 32);
    await f.click('Show software keyboard');
    input.value = '\u200b' + 'y'.repeat(64);
    input.events.input({ inputType: 'insertText' });
    await f.click('Disconnect');
    for (let i = 0;i < f.callbacks.length;i++)
    {
        f.callbacks[i]();
    }
    assert.equal(f.socket.sent.filter(item => item[0] === 10).length, 64);
    assert.equal(input.hidden, true);
});

test('normal text is ordered before modifier changes and an interrupted paste is never sent with new modifiers', async t =>
{
    const f = await fixtureAsync(t), input = descendants(f.root).find(e => e.tag === 'textarea');
    await f.click('Show software keyboard');
    input.value = '\u200ba';
    input.events.input({ inputType: 'insertText' });
    await f.click('Ctrl');
    assert.equal(f.socket.sent.at(-2)[0], 10);
    assert.deepEqual(Array.from(f.socket.sent.at(-1)).slice(4), [0xa2, 1]);
    await f.click('Ctrl');
    input.value = '\u200b' + 'x'.repeat(64);
    input.events.input({ inputType: 'insertFromPaste' });
    await f.click('Alt');
    const count = f.socket.sent.length;
    for (let i = 0;i < f.callbacks.length;i++)
    {
        f.callbacks[i]();
    }
    assert.equal(f.socket.sent.length, count);
    assert.ok(f.notices.some(text => text.includes('Remaining text was cancelled')));
});

test('software keyboard negotiates host support and rejects oversized input without disconnecting', async t =>
{
    await t.test('old Core', async t =>
    {
        const f = await fixtureAsync(t, 'h264', { unicodeInput: false }), button = descendants(f.root).find(e => e.getAttribute('aria-label') === 'Show software keyboard');
        assert.equal(button.disabled, true);
        await f.click('Show software keyboard');
        assert.equal(button.getAttribute('aria-pressed'), 'false');
    });
    await t.test('bounded text', async t =>
    {
        const f = await fixtureAsync(t), input = descendants(f.root).find(e => e.tag === 'textarea');
        await f.click('Show software keyboard');
        input.value = '\u200b' + 'x'.repeat(4097);
        input.events.input({ inputType: 'insertFromPaste' });
        for (let i = 0;i < f.callbacks.length;i++)
        {
            f.callbacks[i]();
        }
        assert.equal(f.socket.sent.some(item => item[0] === 10), false);
        assert.equal(f.socket.readyState, 1);
        assert.ok(f.notices.some(text => text.includes('too large')));
    });
});

test('Firefox JIS punctuation sends physical key down and up despite Shift changing first', async t =>
{
    const f = await fixtureAsync(t), viewport = descendants(f.root).find(e => e.className === 'desktop-viewport');
    const before = f.socket.sent.length;
    for (const [code, keyCode, scan] of [['Minus', 173, 0x0c], ['Equal', 160, 0x0d], ['Semicolon', 59, 0x27], ['Quote', 58, 0x28], ['BracketLeft', 64, 0x1a], ['IntlRo', 220, 0x73], ['IntlYen', 220, 0x7d]])
    {
        viewport.onkeydown({ code, keyCode, shiftKey: true,

            /** 既定動作を取り消したことを記録します。 */
            preventDefault()
            {} });
        viewport.onkeyup({ code, keyCode, shiftKey: false,

            /** 既定動作を取り消したことを記録します。 */
            preventDefault()
            {} });
        assert.deepEqual(Array.from(f.socket.sent.at(-2)).slice(4), [0x100 | scan, 1]);
        assert.deepEqual(Array.from(f.socket.sent.at(-1)).slice(4), [0x100 | scan, 0]);
    }
    assert.equal(f.socket.sent.slice(before).some(v => v[4] === 173), false);
    viewport.onkeydown({ code: 'AudioVolumeMute', keyCode: 173,

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {} });
    assert.equal(f.socket.sent.at(-1)[4], 173);
});

test('old Cores reject punctuation locally without sending ambiguous volume or Shift codes', async t =>
{
    const f = await fixtureAsync(t, 'h264', { scanCodeInput: false }), viewport = descendants(f.root).find(e => e.className === 'desktop-viewport');
    const before = f.socket.sent.length;
    for (const [code, keyCode] of [['Minus', 173], ['Equal', 160], ['Semicolon', 59]])
    {
        viewport.onkeydown({ code, keyCode,

            /** 既定動作を取り消したことを記録します。 */
            preventDefault()
            {} });
        viewport.onkeyup({ code, keyCode,

            /** 既定動作を取り消したことを記録します。 */
            preventDefault()
            {} });
    }
    assert.equal(f.socket.sent.length, before);
    assert.equal(f.notices.filter(x => x.includes('punctuation')).length, 1);
    assert.equal(f.socket.readyState, 1);
});

test('fullscreen selection follows browser fullscreen changes for both targets', async t =>
{
    /** 試験対象を起動し、操作可能な状態を返します。 */
    const f = await fixtureAsync(t);

    /** 試験対象から指定された条件に対応する値を取得します。
     * @returns 検証で使用する値です。
     */
    const button = name => descendants(f.root).find(e => e.getAttribute('aria-label') === name);

    const full = button('Fullscreen'), video = button('Video fullscreen');
    assert.equal(full.getAttribute('aria-pressed'), 'false');
    assert.equal(video.getAttribute('aria-pressed'), 'false');
    await full.onclick();
    assert.equal(full.getAttribute('aria-pressed'), 'true');
    assert.equal(video.getAttribute('aria-pressed'), 'false');
    await document.exitFullscreen();
    assert.equal(full.getAttribute('aria-pressed'), 'false');
    await video.onclick();
    assert.equal(video.getAttribute('aria-pressed'), 'true');
    assert.equal(full.getAttribute('aria-pressed'), 'false');
    await document.exitFullscreen();
    assert.equal(video.getAttribute('aria-pressed'), 'false');
});

test('codec details apply draft values, keep P automatic and B disabled by default, and reset explicitly', async t =>
{
    /** 試験対象を起動し、操作可能な状態を返します。 */
    const f = await fixtureAsync(t, 'h264', { experimental: true, codecDetails: true });

    /** 試験対象から指定された条件に対応する値を取得します。
     * @returns 検証で使用する値です。
     */
    const element = name => descendants(f.root).find(e => e.getAttribute('aria-label') === name);

    const p = element('Maximum P frames per keyframe interval'), b = element('Maximum consecutive B frames'), q = element('Quality value (lower is better)');
    assert.equal(element('Custom codec settings').getAttribute('aria-pressed'), 'true');
    assert.equal(p.disabled, true);
    assert.equal(b.disabled, true);
    assert.equal(q.disabled, true);
    const count = f.socket.sent.length;
    await f.click('Enable B frames');
    assert.equal(b.disabled, false);
    assert.equal(f.socket.sent.length, count);
    await f.click('Enable B frames');
    assert.equal(b.disabled, true);
    await f.click('Apply codec settings');
    assert.deepEqual(Array.from(f.socket.sent.findLast(v => v[0] === 9)), [9, 1, 60, -1, 0, -1]);
    f.describe();
    assert.equal(f.socket.readyState, 1);
    assert.equal(f.contexts.length, 1);
    await f.click('Automatic P frames');
    p.value = '4';
    await f.click('Enable B frames');
    b.value = '2';
    await f.click('Quality target mode');
    q.value = '25';
    assert.equal(p.disabled, false);
    assert.equal(b.disabled, false);
    assert.equal(q.disabled, false);
    await f.click('Apply codec settings');
    assert.deepEqual(Array.from(f.socket.sent.findLast(v => v[0] === 9)), [9, 1, 60, 4, 2, 25]);
    f.describe();
    assert.equal(f.socket.readyState, 1);
    assert.equal(f.contexts.length, 1);
    await f.click('Custom codec settings');
    await f.click('Apply codec settings');
    assert.deepEqual(Array.from(f.socket.sent.findLast(v => v[0] === 9)), [9, 0, 0, 0, 0, 0]);
    f.describe();
    assert.equal(f.socket.readyState, 1);
});

test('codec controls reject invalid input before sending and disable B for VP9', async t =>
{
    /** 試験対象を起動し、操作可能な状態を返します。 */
    const f = await fixtureAsync(t, 'vp9', { experimental: true, codecDetails: true });

    /** 試験対象から指定された条件に対応する値を取得します。
     * @returns 検証で使用する値です。
     */
    const element = name => descendants(f.root).find(e => e.getAttribute('aria-label') === name);

    assert.equal(element('Enable B frames').disabled, true);
    assert.equal(element('Maximum consecutive B frames').disabled, true);
    await f.click('Quality target mode');
    const quality = element('Quality value (lower is better)');
    assert.equal(quality.max, '63');
    quality.value = '63';
    await f.click('Apply codec settings');
    assert.deepEqual(Array.from(f.socket.sent.findLast(v => v[0] === 9)), [9, 1, 60, -1, 0, 63]);
    f.describe();
    const count = f.socket.sent.length;
    quality.value = '64';
    assert.throws(() => f.click('Apply codec settings'), /integer ranges/);
    assert.equal(f.socket.sent.length, count);
    quality.value = '';
    assert.throws(() => f.click('Apply codec settings'), /integer ranges/);
    assert.equal(f.socket.readyState, 1);
});

test('old peers receive no codec extension and cannot apply unsupported custom values', async t =>
{
    /** 試験対象を起動し、操作可能な状態を返します。 */
    const f = await fixtureAsync(t, 'h264', { experimental: true, codecDetails: false, customCodec: false });

    /** 試験対象から指定された条件に対応する値を取得します。
     * @returns 検証で使用する値です。
     */
    const element = name => descendants(f.root).find(e => e.getAttribute('aria-label') === name);

    assert.equal(element('Custom codec settings').disabled, true);
    assert.equal(descendants(f.root).find(e => e.tag === 'button' && e.textContent === 'Apply codec settings').disabled, true);
    assert.equal(f.socket.sent.some(v => v[0] === 9), false);
    assert.throws(() => f.click('Apply codec settings'), /Update the Core/);
    assert.equal(f.socket.readyState, 1);
});

test('a peer that does not acknowledge requested codec settings cannot silently continue', async t =>
{
    const f = await fixtureAsync(t, 'h264', { experimental: true, codecDetails: true });
    await f.click('Apply codec settings');
    f.socket.onmessage(frame(3, 0, [{ id: 0, codec: 0, framesPerSecond: 30, maximumBitrateKbps: 4096, selection: f.socket.sent.findLast(v => v[0] === 6)[3] }]));
    assert.equal(f.socket.readyState, 3);
    assert.match(f.notices.at(-1), /Codec settings were not applied/);
});

test('quality display uses applied values and returning to bitrate mode restores the saved target', async t =>
{
    const f = await fixtureAsync(t, 'h264', { experimental: true, codecDetails: true, bitrate: 100 });
    await f.click('Quality target mode');
    await f.click('Apply codec settings');
    f.describe();
    f.advance(600);

    /** 試験用の処理統計を集計します。 */
    const stats = descendants(f.root).find(e => e.className === 'desktop-stats');

    /** 試験対象から指定された条件に対応する値を取得します。
     * @returns 検証で使用する値です。
     */
    const status = () => descendants(f.root).filter(e => e.getAttribute('role') === 'status').map(e => e.textContent).join(' ');

    assert.match(stats.textContent, /Quality targetCRF\/CQ 23/);
    assert.doesNotMatch(status(), /100 kbps/);
    assert.match(status(), /no bitrate cap/);
    const quality = descendants(f.root).find(e => e.getAttribute('aria-label') === 'Quality value (lower is better)');
    quality.value = '42';
    f.advance(600);
    assert.match(stats.textContent, /CRF\/CQ 23/);
    await f.click('Apply codec settings');
    f.describe();
    f.advance(600);
    assert.match(stats.textContent, /CRF\/CQ 42/);
    await f.click('Quality target mode');
    await f.click('Apply codec settings');
    f.describe();
    f.advance(600);
    assert.match(stats.textContent, /CBR target100 kbps/);
    assert.equal(f.socket.readyState, 1);
    assert.equal(f.contexts.length, 1);
});

test('quality drafts disable the unused bitrate input before connection', async t =>
{
    const f = await fixtureAsync(t, 'h264', { experimental: true, codecDetails: true });
    await f.click('Disconnect');
    const bitrate = descendants(f.root).find(e => e.getAttribute('aria-label') === 'Maximum video kbps');
    assert.equal(bitrate.disabled, false);
    await f.click('Quality target mode');
    assert.equal(bitrate.disabled, true);
    await f.click('Custom codec settings');
    assert.equal(bitrate.disabled, false);
    await f.click('Custom codec settings');
    assert.equal(bitrate.disabled, true);
    bitrate.value = '';
    await f.click('Connect');
    assert.equal(f.contexts.length, 2);
});

test('older Cores that still cap quality encoding are rejected instead of claiming pure quality mode', async t =>
{
    const f = await fixtureAsync(t, 'h264', { experimental: true, codecDetails: true });
    await f.click('Quality target mode');
    await f.click('Apply codec settings');
    f.socket.onmessage(frame(3, 0, [{ id: 0, codec: 0, framesPerSecond: 30, maximumBitrateKbps: 4096, selection: f.socket.sent.findLast(v => v[0] === 6)[3], codecDetails: { maximumKeyFrameInterval: 8, maximumPFrames: null, maximumBFrames: 0, quality: 23 } }]));
    assert.equal(f.socket.readyState, 3);
    assert.match(f.notices.at(-1), /Update the Core/);
});

for (const query of ['?extra=true', '?experimeltal=true', '?experimental=True', '?experimental=false'])
{
    test(`experimental controls require the exact opted-in query: ${query}`, async t =>
    {
        const f = await fixtureAsync(t, null, { query });
        assert.equal(descendants(f.root).some(item => item.getAttribute('aria-label') === 'Hide host cursor in video'), false);
        const codec = descendants(f.root).find(item => item.getAttribute('aria-label') === 'Video codec');
        assert.deepEqual(codec.options.map(option => option.value), ['vp9']);
        assert.equal(f.socket.sent.findLast(input => input[0] === 6)[2], 1);
    });
}

test('browser preference enables desktop controls even with a false URL value', async t =>
{
    const f = await fixtureAsync(t, null, { query: '?experimental=false', alwaysExperimental: true });
    const codec = descendants(f.root).find(item => item.getAttribute('aria-label') === 'Video codec');
    assert.deepEqual(codec.options.map(option => option.value), ['vp9', 'h264', 'h265']);
    assert.equal(f.initialCodec, 'vp9');
    assert(descendants(f.root).some(item => item.getAttribute('aria-label') === 'Codec details'));
});

test('experimental mode keeps VP9 as default and permits H264 and H265 explicitly', async t =>
{
    for (const [codec, wireValue] of [[null, 1], ['h264', 0], ['h265', 2]])
    {
        await t.test(codec ?? 'default', async t =>
        {
            const f = await fixtureAsync(t, codec, { experimental: true });
            const selector = descendants(f.root).find(item => item.getAttribute('aria-label') === 'Video codec');
            assert.deepEqual(selector.options.map(option => option.value), ['vp9', 'h264', 'h265']);
            assert.equal(f.initialCodec, 'vp9');
            assert.equal(f.socket.sent.findLast(input => input[0] === 6)[2], wireValue);
            f.describe();
            assert.equal(f.socket.readyState, 1);
        });
    }
});

for (const query of ['', '?experimental=false', '?experimental=true'])
{
    test(`VBR is the default bitrate mode independently of experimental features: ${query || 'normal page'}`, async t =>
    {
        const f = await fixtureAsync(t, null, { query });
        const toggle = descendants(f.root).find(e => e.getAttribute('aria-label') === 'Variable bitrate (VBR)');
        assert.equal(f.initialVbr, 'true');
        assert.equal(toggle.getAttribute('aria-pressed'), 'true');
        assert.deepEqual(toggle.children.map(item => item.textContent), ['CBR', 'VBR']);
        assert.equal(toggle.disabled, true);
        assert.ok(f.socket.sent.findLast(v => v[0] === 6)[5] & 0x10000000);
        assert.match(f.root.textContent, /VBR target/);
    });
}

test('VBR fails visibly without sending a selection to an unsupported Core', async t =>
{
    const f = await fixtureAsync(t, null, { query: '?experimental=false', vbrSupported: false });
    assert.equal(f.socket.sent.some(v => v[0] === 6), false);
    assert.ok(f.notices.some(text => text.includes('use VBR')));
    assert.equal(f.socket.readyState, 3);
});

test('VBR rejects a Core echo that silently ignores the requested rate mode', async t =>
{
    const f = await fixtureAsync(t);
    const selection = f.socket.sent.findLast(v => v[0] === 6)[3];
    f.socket.onmessage(frame(3, 0, [{ id: 0, codec: 1, framesPerSecond: 30, maximumBitrateKbps: 4096, selection, codecDetails: f.codecEcho() }]));
    assert.ok(f.notices.some(text => text.includes('VBR setting was not applied')));
    assert.equal(f.socket.readyState, 3);
});

test('quality target disables VBR without changing the running stream until apply', async t =>
{
    const f = await fixtureAsync(t, 'h264', { experimental: true, codecDetails: true, vbr: true, vbrSupported: true });
    await f.click('Quality target mode');
    const vbr = descendants(f.root).find(e => e.getAttribute('aria-label') === 'Variable bitrate (VBR)');
    assert.equal(vbr.disabled, true);
    assert.equal(vbr.getAttribute('aria-pressed'), 'false');
    assert.ok(f.socket.sent.findLast(v => v[0] === 6)[5] & 0x10000000);
    await f.click('Apply codec settings');
    assert.equal(f.socket.sent.findLast(v => v[0] === 6)[5] & 0x10000000, 0);
    f.describe();
    assert.equal(f.socket.readyState, 1);
});

test('CBR can be selected on the normal page for a Core without VBR capability', async t =>
{
    const f = await fixtureAsync(t, null, { query: '?experimental=false', vbr: false, vbrSupported: false });
    const toggle = descendants(f.root).find(e => e.getAttribute('aria-label') === 'Variable bitrate (VBR)');
    assert.equal(f.initialVbr, 'true');
    assert.equal(toggle.getAttribute('aria-pressed'), 'false');
    assert.equal(f.socket.sent.findLast(v => v[0] === 6)[5], (30 << 20) | 4096);
    assert.match(f.root.textContent, /CBR target/);
    assert.equal(f.socket.readyState, 1);
});

test('explicit CBR preserves compatibility when the peer omits the connection description', async t =>
{
    const f = await fixtureAsync(t, 'h264', { experimental: true, vbr: false, customCodec: false, connectionDescription: false });
    assert.equal(f.socket.sent.findLast(v => v[0] === 6)[5] & 0x10000000, 0);
    assert.equal(f.socket.readyState, 1);
});

test('A preset without a capability description is rejected before selecting a stream', async t =>
{
    const f = await fixtureAsync(t, 'h264', { connectionDescription: false });
    assert.equal(f.socket.sent.some(v => v[0] === 6), false);
    assert.ok(f.notices.some(text => text.includes('use codec details')));
    assert.equal(f.socket.readyState, 3);
});

test('bitrate guidance lives in hover tips instead of persistent paragraphs', async t =>
{
    const f = await fixtureAsync(t), all = descendants(f.root);
    const toggle = all.find(e => e.getAttribute('aria-label') === 'Variable bitrate (VBR)');
    const bitrate = all.find(e => e.getAttribute('aria-label') === 'Maximum video kbps');
    const stats = all.find(e => e.className === 'desktop-stats');
    assert.match(toggle.title, /instantaneous bandwidth limit/);
    assert.match(toggle.title, /Quality targets/);
    assert.match(toggle.title, /while disconnected/);
    assert.equal(toggle.getAttribute('aria-description'), toggle.title);
    assert.equal(toggle.parentElement.title, toggle.title);
    assert.equal(bitrate.parentElement.title, toggle.title);
    assert.equal(stats.children[4].children[0].title, toggle.title);
    assert.doesNotMatch(f.root.textContent, /instantaneous bandwidth limit|Quality targets disable|Reconnect to change VBR/);
});

test('CBR and VBR choices persist across disconnect and are transmitted on the next connection', async t =>
{
    const f = await fixtureAsync(t), toggle = descendants(f.root).find(e => e.getAttribute('aria-label') === 'Variable bitrate (VBR)');
    for (const variable of [false, true])
    {
        await f.click('Disconnect');
        assert.equal(toggle.disabled, false);
        await f.click('Variable bitrate (VBR)');
        assert.equal(toggle.getAttribute('aria-pressed'), String(variable));
        await f.click('Connect');
        const socket = f.sockets.at(-1);
        socket.onmessage(frame(7, 0, { variableBitrate: true, codecDetails: true }));
        socket.onmessage(frame(1, 0, [{ id: 0, name: 'Screen A', left: 0, top: 0, width: 1920, height: 1080, primary: true }]));
        assert.equal(!!(socket.sent.findLast(v => v[0] === 6)[5] & 0x10000000), variable);
        assert.equal(toggle.disabled, true);
    }
});

for (const [id, fps, bitrate, audioCodec, audioBitrate, samples, interval] of [
    ['mobile-stability', 8, 1024, 'opus', 64, 36000, 8],
    ['mobile-quality', 30, 3072, 'opus', 96, 24000, 30],
    ['fixed-stability', 30, 2048, 'opus', 96, 18000, 30],
    ['fixed-balanced', 30, 4096, 'opus', 128, 12000, 60],
    ['fixed-quality', 30, 7168, 'opus', 192, 12000, 120],
    ['lan', 60, 10240, 'f32le', 192, 4800, 30]
])
{
    for (const experimental of [false, true])
    {
        test(`transmission preset ${id} configures the stream and audio buffer (experimental=${experimental})`, async t =>
        {
            const f = await fixtureAsync(t, null, { preset: id, defaultAudio: true, experimental });
            assert.equal(f.initialPreset, 'fixed-balanced');
            assert.equal(f.presetSelector.value, id);
            assert.equal(f.presetSelector.disabled, true);
            if (audioCodec === 'opus')
            {
                f.socket.onmessage(frame(4, 0, { sampleRate: 48000, channels: 2, format: 'f32le', codecs: ['f32le', 'opus'], opusBitrates: [audioBitrate] }));
                assert.deepEqual(Array.from(f.socket.sent.find(input => input[0] === 7)), [7, 1, audioBitrate, 0, 0, 0]);
                f.socket.onmessage(frame(4, 0, { sampleRate: 48000, channels: 2, format: 'opus', bitrateKbps: audioBitrate }));
            }
            else
            {
                assert.equal(f.socket.sent.some(input => input[0] === 7), false);
            }
            assert.equal(f.socket.sent.findLast(input => input[0] === 6)[2], 1);
            assert.equal(f.socket.sent.findLast(input => input[0] === 6)[5], (fps << 20) | bitrate | 0x10000000);
            assert.deepEqual(Array.from(f.socket.sent.findLast(input => input[0] === 9)), [9, 1, interval, -1, 0, -1]);
            assert.equal(f.audioNodes[0].configuration.processorOptions.maxBufferSamples, samples);
            assert.deepEqual(f.initialAudio, { codec: audioCodec, bitrate: String(audioBitrate) });
            f.describe();
            assert.equal(f.socket.readyState, 1);
        });
    }
}

test('the transmission preset sits immediately left of the host and shows Custom only for unmatched values', async t =>
{
    const f = await fixtureAsync(t, null, { defaultAudio: true });
    await f.click('Disconnect');
    const all = descendants(f.root), host = all.find(item => item.getAttribute('aria-label') === 'Host');
    const preset = f.presetSelector, custom = preset.options.find(item => item.value === 'custom');
    assert.equal(preset.parentElement, host.parentElement);
    assert.equal(preset.parentElement.children.indexOf(preset) + 1, preset.parentElement.children.indexOf(host));
    assert.equal(custom.hidden, true);
    assert.equal(custom.disabled, true);
    for (const [name, changed, original] of [
        ['Target fps', '31', '30.0'],
        ['Maximum video kbps', '5000', '4096'],
        ['Audio buffer samples/ch', '18000', '12000'],
        ['Audio bitrate', '192', '128']
    ])
    {
        const field = all.find(item => item.getAttribute('aria-label') === name);
        field.value = changed;
        field.oninput();
        assert.equal(preset.value, 'custom');
        assert.equal(custom.hidden, false);
        field.value = original;
        field.onchange();
        assert.equal(preset.value, 'fixed-balanced');
        assert.equal(custom.hidden, true);
    }
    await f.click('Variable bitrate (VBR)');
    assert.equal(preset.value, 'custom');
    await f.click('Variable bitrate (VBR)');
    assert.equal(preset.value, 'fixed-balanced');
});

test('advanced codec edits become Custom and choosing a preset restores all transmission values', async t =>
{
    const f = await fixtureAsync(t, null, { defaultAudio: true, experimental: true });
    await f.click('Disconnect');
    const all = descendants(f.root), interval = all.find(item => item.getAttribute('aria-label') === 'Maximum keyframe interval (frames)');
    interval.value = '';
    assert.doesNotThrow(() => interval.oninput());
    assert.equal(f.presetSelector.value, 'custom');
    interval.value = '60';
    interval.oninput();
    assert.equal(f.presetSelector.value, 'fixed-balanced');
    const codec = all.find(item => item.getAttribute('aria-label') === 'Video codec');
    codec.value = 'h264';
    codec.onchange();
    await f.click('Enable B frames');
    assert.equal(f.presetSelector.value, 'custom');
    await f.click('Automatic P frames');
    await f.click('Quality target mode');
    f.presetSelector.value = 'lan';
    f.presetSelector.onchange();
    assert.equal(f.presetSelector.value, 'lan');
    assert.equal(codec.value, 'vp9');
    assert.equal(all.find(item => item.getAttribute('aria-label') === 'Enable B frames').disabled, true);
    assert.equal(interval.value, '30');
    assert.equal(all.find(item => item.getAttribute('aria-label') === 'Automatic P frames').getAttribute('aria-pressed'), 'true');
    assert.equal(all.find(item => item.getAttribute('aria-label') === 'Enable B frames').getAttribute('aria-pressed'), 'false');
    assert.equal(all.find(item => item.getAttribute('aria-label') === 'Quality target mode').getAttribute('aria-pressed'), 'false');
    assert.equal(all.find(item => item.getAttribute('aria-label') === 'Variable bitrate (VBR)').getAttribute('aria-pressed'), 'true');
    const audioRate = all.find(item => item.getAttribute('aria-label') === 'Audio bitrate');
    assert.equal(audioRate.disabled, true);
    audioRate.value = '64';
    audioRate.onchange();
    assert.equal(f.presetSelector.value, 'lan');
});

test('a connected stream cannot be changed by selecting another transmission preset', async t =>
{
    const f = await fixtureAsync(t, null, { defaultAudio: true });
    const messages = f.socket.sent.length;
    f.presetSelector.value = 'lan';
    f.presetSelector.onchange();
    assert.equal(f.presetSelector.value, 'fixed-balanced');
    assert.equal(f.socket.sent.length, messages);
    assert.equal(descendants(f.root).find(item => item.getAttribute('aria-label') === 'Maximum video kbps').value, '4096');
});

test('a browser without Opus disables unavailable profiles and labels the PCM fallback Custom', async t =>
{
    const f = await fixtureAsync(t, 'h264', { defaultAudio: true, opusSupported: false });
    assert.equal(f.initialPreset, 'custom');
    for (const option of f.presetSelector.options.filter(item => item.value !== 'custom'))
    {
        assert.equal(option.disabled, option.value !== 'lan');
    }
    await f.click('Disconnect');
    f.presetSelector.value = 'lan';
    f.presetSelector.onchange();
    assert.equal(f.presetSelector.value, 'lan');
});

test('a normal preset cannot silently lose its keyframe settings on an older Core', async t =>
{
    const f = await fixtureAsync(t, 'h264', { codecDetails: false });
    assert.equal(f.socket.sent.some(input => input[0] === 6 || input[0] === 9), false);
    assert.match(f.notices.at(-1), /Update the Core and ClientHost to use codec details/);
    assert.equal(f.socket.readyState, 3);
});
