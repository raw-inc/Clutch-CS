import { createDesktopCodecSettings, sameDesktopCodecSettings } from './remote-desktop-codec-settings.js';
import { desktopTransmissionPresets, defaultDesktopTransmissionPreset, desktopPresetCodecSettings, findDesktopTransmissionPreset } from './remote-desktop-presets.js';
import { supportsDesktopOpus } from './remote-desktop-opus.js';
import { mapDesktopPoint, desktopKey, desktopWheelDelta } from './remote-desktop-input.js';
import { createDesktopAudio } from './remote-desktop-audio.js';
import { createDesktopVideo } from './remote-desktop-video.js';
import { createDesktopVideoWorker, supportsDesktopVideoWorker } from './remote-desktop-video-worker.js';
import { createDesktopLayout } from './remote-desktop-layout.js';
import { createDesktopKeyboard } from './remote-desktop-keyboard.js';
import { createAnnotationState, createAnnotationPointer, drawAnnotations } from './remote-desktop-annotations.js';
import { createDesktopReconnect, parseDesktopReconnectDelays, desktopDisconnectReason } from './remote-desktop-reconnect.js';
import { createDesktopLatency } from './remote-desktop-latency.js';
import { createDesktopClipboard, readDesktopClipboardTextAsync } from './remote-desktop-clipboard.js';

/** リモートデスクトップの接続・表示・入力操作を構成します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function desktopView(root, ctx)
{
    root.className = 'desktop-workspace';
    const experimental = new URLSearchParams(location.search).get('experimental') === 'true';
    let viewOnly = false, viewOnlySupported = false;
    let clipboard = null, clipboardSupported = false, clipboardPointerInside = false;
    let annotationsSupported = false, annotationFrame = 0;
    const annotationState = createAnnotationState();
    const annotationPointer = createAnnotationPointer(send, point, () => viewOnly && annotationsSupported && !hidden && socket?.readyState === WebSocket.OPEN);

    /** 指示表示を再描画し、必要な間だけ次の描画を予約します。 */
    function paintAnnotations()
    {
        annotationFrame = 0;
        let active = false;
        for (const view of views)
        {
            if (view?.annotationContext)
            {
                active = drawAnnotations(view.annotationContext, view.monitor, annotationState, performance.now()) || active;
            }
        }
        if (active && !hidden)
        {
            annotationFrame = requestAnimationFrame(paintAnnotations);
        }
    }

    /** 指示表示の再描画を一回にまとめて予約します。 */
    function scheduleAnnotations()
    {
        if (annotationsSupported && !annotationFrame && !hidden)
        {
            annotationFrame = requestAnimationFrame(paintAnnotations);
        }
    }

    const toolbar = document.createElement('div');
    toolbar.className = 'desktop-toolbar';
    const rail = document.createElement('aside');
    rail.className = 'desktop-rail';
    rail.setAttribute('aria-label', ctx.t('Desktop controls', 'デスクトップ操作'));
    const menus = [];

    /** 設定項目を開閉するメニューを構成します。
     * @returns element・body・triggerを含む結果です。
     */
    function menu(label, icon)
    {
        const element = document.createElement('details');
        element.className = 'desktop-menu';
        const trigger = document.createElement('summary');
        trigger.textContent = label;
        trigger.title = label;
        trigger.setAttribute('aria-label', label);
        trigger.append(ctx.icon(icon));
        const body = document.createElement('div');
        body.className = 'desktop-popover';
        const heading = document.createElement('h2');
        heading.textContent = label;
        body.append(heading);
        element.append(trigger, body);
        rail.append(element);
        menus.push(element);
        element.addEventListener('toggle', () =>
        {
            if (element.open)
            {
                for (const other of menus)
                {
                    if (other !== element)
                    {
                        other.open = false;
                    }
                }
            }
        }, { signal: ctx.signal });
        return { element, body, trigger };
    }

    /** 入力欄に対応するラベルを付けます。
     * @returns 呼出し元が使用する値です。
     */
    function labeled(label, field)
    {
        const wrapper = document.createElement('label');
        wrapper.textContent = label;
        wrapper.append(field);
        return wrapper;
    }

    /** 表示名とイベントを持つ操作ボタンを構成します。 */
    function action(button, label, icon, caption = null)
    {
        button.textContent = caption ?? '';
        button.append(ctx.icon(icon));
        button.title = label;
        button.setAttribute('aria-label', label);
        button.className = 'desktop-action quiet ' + (caption === null ? 'icon-button' : 'desktop-labeled');
    }

    const settingsMenu = menu(ctx.t('Connection settings', '接続設定'), 'settings');
    const codecMenu = experimental ? menu(ctx.t('Codec details', 'コーデック詳細設定'), 'settings') : null;
    const detailsMenu = menu(ctx.t('Connection details', '接続の詳細'), 'info');
    const cluster = document.createElement('select'), host = document.createElement('select');
    const transmissionPreset = document.createElement('select');
    transmissionPreset.className = 'desktop-transmission-preset';
    transmissionPreset.setAttribute('aria-label', ctx.t('Transmission preset', '伝送プリセット'));
    transmissionPreset.title = ctx.t('Choose a transmission preset while disconnected. Manual changes that do not match a preset are shown as Custom.', '切断中に伝送設定をまとめて変更します。手動変更した値がプリセットと一致しない場合は「カスタム」と表示します。');
    const customPreset = option('custom', ctx.t('Custom', 'カスタム'));
    customPreset.disabled = true;
    cluster.setAttribute('aria-label', ctx.t('Cluster', 'クラスタ'));
    host.setAttribute('aria-label', ctx.t('Host', 'ホスト'));
    const codec = document.createElement('select'), acceleration = document.createElement('select');
    let scaleMode = 'fit', cursorEnabled = false;
    const rendering = document.createElement('select');
    rendering.setAttribute('aria-label', ctx.t('Video rendering', '映像描画方式'));
    const cursorSync = ctx.button(ctx.t('Cursor synchronization', 'カーソル同期'), () =>
    {
        if (!canControlCursor())
        {
            return;
        }
        hideCursor = cursorEnabled = !cursorEnabled;
        updateCursorSync();
        choose(selected);
    });
    const clipboardSync = ctx.button(ctx.t('Clipboard synchronization', 'クリップボード同期'), () =>
    {
        const pending = clipboard.enableAsync(!clipboard.enabled);
        updateControls();
        return pending;
    });
    const sizing = ctx.button(ctx.t('Dot by dot (1:1)', 'Dot by dot（1:1）'), () =>
    {
        const expectedFit = 'fit';

        scaleMode = scaleMode === expectedFit ? 'native' : 'fit';
        sizing.setAttribute('aria-pressed', String(scaleMode === 'native'));
        updateControls();
        layout.update();
    });
    const audioCodec = document.createElement('select'), audioBitrate = document.createElement('select');
    audioCodec.setAttribute('aria-label', ctx.t('Audio codec', '音声コーデック'));
    audioBitrate.setAttribute('aria-label', ctx.t('Audio bitrate', '音声ビットレート'));
    const chromium = /(?:Chrome|Chromium|Edg)\//.test(globalThis.navigator?.userAgent ?? '');
    let followPointer = true, hideCursor = false, cursorVisibilitySupported = false, selecting = false;

    /** 同期と映像内非表示の両方を、現在の接続で適用できるか確認します。
     * @returns 両端の対応を確認し、映像選択の完了後だけtrueです。
     */
    function canControlCursor()
    {
        return cursorVisibilitySupported && cursorRelay && cursorSupported && !selecting && socket?.readyState === WebSocket.OPEN;
    }

    /** 範囲と既定値を持つ数値設定欄を構成します。
     * @returns wrapper・fieldを含む結果です。
     */
    function numberSetting(label, value, min, max)
    {
        const wrapper = document.createElement('label');
        wrapper.className = 'desktop-setting';
        const caption = document.createElement('span');
        caption.textContent = label;
        const field = document.createElement('input');
        field.type = 'number';
        field.value = String(value);
        field.min = String(min);
        field.max = String(max);
        field.step = '1';
        field.setAttribute('aria-label', label);
        wrapper.append(caption, field);
        return { wrapper, field };
    }

    const fps = numberSetting(ctx.t('Target fps', '目標fps'), defaultDesktopTransmissionPreset.fps, 1, 120);
    const bitrate = numberSetting(ctx.t('Maximum video kbps', '映像最大kbps'), defaultDesktopTransmissionPreset.bitrate, 100, 100000);
    const audioBuffer = numberSetting(ctx.t('Audio buffer samples/ch', '音声バッファ samples/ch'), defaultDesktopTransmissionPreset.audioSamples, 960, 48000);
    let appliedCodecSettings = null, codecDetailsSupported = false;
    let presetCodecSettings = desktopPresetCodecSettings(defaultDesktopTransmissionPreset);
    let videoSettings = { fps: defaultDesktopTransmissionPreset.fps, bitrate: defaultDesktopTransmissionPreset.bitrate, variableBitrate: true }, audioSamples = defaultDesktopTransmissionPreset.audioSamples;
    let variableBitrate = true, variableBitrateSupported = false;
    const vbr = ctx.button(ctx.t('Variable bitrate (VBR)', '可変ビットレート（VBR）'), () =>
    {
        variableBitrate = !variableBitrate;
        updateControls();
    });
    vbr.setAttribute('aria-label', ctx.t('Variable bitrate (VBR)', '可変ビットレート（VBR）'));
    vbr.className = 'desktop-bitrate-mode';
    const cbrLabel = document.createElement('span'), vbrLabel = document.createElement('span');
    cbrLabel.textContent = 'CBR';
    vbrLabel.textContent = 'VBR';
    vbr.replaceChildren(cbrLabel, vbrLabel);
    vbr.title = ctx.t('On selects VBR; off selects CBR. VBR uses maximum video kbps as a target maximum, not an instantaneous bandwidth limit including audio and protocol overhead. Quality targets disable bitrate control. Switch CBR/VBR while disconnected; the next connection uses the selected mode.',
        'ONはVBR、OFFはCBRです。VBRでは映像最大kbpsを目標最大値として扱い、瞬間的な通信量や音声・通信ヘッダー込みの総帯域の上限は保証しません。品質指定中はビットレート制御を使用しません。CBR/VBRの切替は切断中に行い、次の接続に反映します。');
    vbr.setAttribute('aria-description', vbr.title);
    const bitrateMode = labeled(ctx.t('Video bitrate mode', '映像ビットレート方式'), vbr);
    bitrateMode.title = bitrate.wrapper.title = vbr.title;
    let audioSelection = { codec: 'opus', bitrate: defaultDesktopTransmissionPreset.audioBitrate }, audioRequested = false, audioSelectionAt = 0;
    let keyboardCapture = null, keyboardRequest = 0;
    codec.setAttribute('aria-label', ctx.t('Video codec', '映像コーデック'));
    acceleration.setAttribute('aria-label', ctx.t('Video decoder', '映像デコーダー'));
    const screens = document.createElement('div');
    screens.className = 'desktop-screen-tabs';
    screens.setAttribute('role', 'group');
    screens.setAttribute('aria-label', ctx.t('Displays', 'ディスプレイ選択'));
    const connection = document.createElement('span');
    connection.className = 'desktop-connection';
    connection.setAttribute('role', 'status');
    const empty = document.createElement('div');
    empty.className = 'desktop-empty';
    const emptyLabel = document.createElement('p');
    emptyLabel.textContent = ctx.t('Select a host to connect', 'ホストを選択して接続');
    empty.append(ctx.icon('monitor'), emptyLabel);
    const viewport = document.createElement('div');
    viewport.className = 'desktop-viewport';
    viewport.tabIndex = 0;
    viewport.setAttribute('aria-label', ctx.t('Remote desktop. Click to send keyboard input.', 'リモートデスクトップ。クリックしてキーボード操作を開始します。'));
    const overlay = document.createElement('dl');
    overlay.className = 'desktop-stats';
    overlay.hidden = false;
    overlay.setAttribute('aria-label', ctx.t('Stream statistics', '配信の統計'));
    const metrics = {}, metricLabels = {};
    for (const [key, label] of [['speed', ctx.t('Receive', '受信')], ['fps', ctx.t('Display FPS', '表示FPS')], ['delivery', ctx.t('Send → receive', '送信→受信')], ['delay', ctx.t('Receive → display', '受信→表示')], ['bitrate', ctx.t('Target bitrate', '設定ビットレート')], ['codec', ctx.t('Codec', 'コーデック')]])
    {
        const row = document.createElement('div'), term = document.createElement('dt'), value = document.createElement('dd');
        term.textContent = label;
        value.textContent = '—';
        row.append(term, value);
        overlay.append(row);
        metrics[key] = value;
        metricLabels[key] = term;
    }
    metricLabels.bitrate.title = vbr.title;
    metricLabels.delivery.title = ctx.t('Estimated time from Core video transmission to receipt of the complete frame, including ClientHost relay. Clock offsets are corrected using round trips; asymmetric routes introduce uncertainty. Unavailable before calibration or with older peers.', 'Coreの映像送信開始から、ClientHost中継を経てフレーム全体を受信するまでの推定時間です。往復通信で時計差を補正するため、往復経路が非対称な場合は誤差があります。時刻補正前や旧版との接続では表示しません。');

    /** 画面のスクロール位置に合わせて統計表示を配置します。 */
    function positionOverlay()
    {
        overlay.style.transform = `translate(${viewport.scrollLeft}px, ${viewport.scrollTop}px)`;
    }

    viewport.addEventListener('scroll', positionOverlay, { signal: ctx.signal });
    const status = document.createElement('p');
    status.setAttribute('role', 'status');
    const audioStatus = document.createElement('p');
    audioStatus.setAttribute('role', 'status');
    const codecNote = document.createElement('p');
    settingsMenu.body.append(labeled(ctx.t('Video codec', '映像コーデック'), codec), labeled(ctx.t('Video decoder', '映像デコーダー'), acceleration), fps.wrapper, bitrateMode, bitrate.wrapper, audioBuffer.wrapper);
    if (chromium)
    {
        settingsMenu.body.append(labeled(ctx.t('Video rendering', '映像描画方式'), rendering));
    }
    settingsMenu.body.append(labeled(ctx.t('Audio codec', '音声コーデック'), audioCodec), labeled(ctx.t('Audio bitrate', '音声ビットレート'), audioBitrate));
    const reconnectDelays = document.createElement('input');
    reconnectDelays.type = 'text';
    reconnectDelays.value = '0, 3000, 10000';
    reconnectDelays.maxLength = 159;
    reconnectDelays.setAttribute('aria-label', ctx.t('Reconnect delays (ms)', '再接続の待機時間（ms）'));
    reconnectDelays.title = ctx.t('Comma-separated delays after each failed connection, up to 20 attempts and 300000 ms per attempt. These settings last only while this page is open.', '失敗確定後の待機時間をカンマ区切りで指定します。最大20回、各0～300000msです。この画面を離れると設定はリセットされます。');
    if (experimental)
    {
        settingsMenu.body.append(labeled(reconnectDelays.getAttribute('aria-label'), reconnectDelays));
    }
    const codecPanel = codecMenu ? createDesktopCodecSettings(codecMenu.body, ctx, numberSetting, () =>
    {
        if (socket && !codecDetailsSupported)
        {
            throw new Error(ctx.t('Update the Core and ClientHost to use codec details.', 'コーデック詳細設定を使用するにはCoreとClientHostを更新してください。'));
        }
        const next = codecPanel.read(codec.value), nextBitrate = selectedBitrate(next);
        appliedCodecSettings = next;
        videoSettings.bitrate = nextBitrate;
        videoSettings.variableBitrate = variableBitrate && next?.quality == null;
        if (socket)
        {
            choose(selected);
        }
    }, updateControls) : null;

    /** 伝送設定の草稿を照合して、プリセット名またはカスタムを表示します。 */
    function updateTransmissionPreset()
    {
        const matched = findDesktopTransmissionPreset({ codec: codec.value, variableBitrate, fps: Number(fps.field.value), bitrate: Number(bitrate.field.value),
            audioCodec: audioCodec.value, audioBitrate: Number(audioBitrate.value), audioSamples: Number(audioBuffer.field.value),
            codecDetails: codecPanel ? codecPanel.draft() : presetCodecSettings });
        customPreset.hidden = matched !== null;
        transmissionPreset.value = matched?.id ?? 'custom';
    }

    /** 選択したプリセットを次の接続に使う入力値へ反映します。 */
    function applyTransmissionPreset(preset)
    {
        const opus = 'opus';
        codec.value = preset.codec;
        variableBitrate = preset.variableBitrate;
        fps.field.value = String(preset.fps);
        bitrate.field.value = String(preset.bitrate);
        audioBuffer.field.value = String(preset.audioSamples);
        audioCodec.value = preset.audioCodec === opus && !opusSupported ? 'f32le' : preset.audioCodec;
        audioBitrate.value = String(preset.audioBitrate);
        presetCodecSettings = desktopPresetCodecSettings(preset);
        codecPanel?.applyPreset(preset.keyFrameInterval);
        updateControls();
    }

    for (const field of [fps.field, bitrate.field, audioBuffer.field, audioBitrate])
    {
        field.oninput = field.onchange = updateTransmissionPreset;
    }

    /** 詳細設定画面または通常画面のプリセットから、次の接続の設定を確定します。
     * @returns 接続開始時に固定する追加コーデック設定です。
     */
    function readCodecSettings()
    {
        return codecPanel ? codecPanel.read(codec.value) : { ...presetCodecSettings };
    }

    /** ブラウザーの音声対応を反映してプリセットの選択肢と初期値を用意します。 */
    function initializeTransmissionPresets()
    {
        for (const preset of desktopTransmissionPresets)
        {
            const opus = 'opus';
            const item = option(preset.id, ctx.t(...preset.name));
            item.disabled = preset.audioCodec === opus && !opusSupported;
            transmissionPreset.append(item);
        }
        transmissionPreset.append(customPreset);
        transmissionPreset.onchange = () =>
        {
            const preset = desktopTransmissionPresets.find(item => item.id === transmissionPreset.value);
            const opus = 'opus';
            if (!preset || socket || (preset.audioCodec === opus && !opusSupported))
            {
                updateTransmissionPreset();
                return;
            }
            applyTransmissionPreset(preset);
        };
        applyTransmissionPreset(defaultDesktopTransmissionPreset);
    }

    /** 現在の入力から適用するビットレートを検証します。
     * @returns 呼出し元が使用する値です。
     */
    function selectedBitrate(details)
    {
        if (details?.quality != null)
        {
            return 6000;
        }

        // Reserved wire field; quality encoding does not use it.
        const value = Number(bitrate.field.value);
        if (!Number.isInteger(value) || value < Number(bitrate.field.min) || (Number(bitrate.field.max)) < (value))
        {
            throw new Error(ctx.t('Enter a valid maximum bitrate.', '有効な最大ビットレートを入力してください。'));
        }
        return value;
    }

    detailsMenu.body.append(status, audioStatus, codecNote);
    root.append(toolbar, rail, viewport);
    viewport.append(empty, overlay);
    let peers = [], socket, monitors = [], views = [], selected = 0, disposed = false, generation = 0, render = 0, pointer;
    const input = new ArrayBuffer(24), inputView = new DataView(input);
    const text = new TextDecoder();
    const pointerPosition = new Int32Array(3);
    const pointerEvent = { clientX: 0, clientY: 0, pointerType: 'mouse' };
    let lastPointer = -Infinity;
    const wheelPosition = new Int32Array(3);
    let wheel = 0, lastWheel = -Infinity;
    let audio, closing = Promise.resolve(), muted = false, autoMuted = false, audioReady = false, localConnection = false, targetLabel = '';
    let selection = 0, watch = 0, lastMessage = 0, heartbeat = false, lastRefresh = -Infinity, refreshes = 0, lastStats = 0, lastDraw = 0, hidden = false, fullscreenEscape = false;
    const videoStatus = document.createElement('p');
    detailsMenu.body.append(videoStatus);
    let receivedBytes = 0;
    let workerAllowed = true;
    const delivery = createDesktopLatency();
    let connecting = false, connectionStarted = 0, reconnectSelection = null;
    let reconnectState = { enabled: false, waiting: false, trying: false, pending: false, attempt: 0, maximum: 3 };
    const reconnect = createDesktopReconnect(() => connectAsync(true), state =>
    {
        reconnectState = state;
        updateControls();
        if (state.waiting || state.trying)
        {
            const label = state.waiting ? ctx.t('Reconnect in ', '再接続まで ') + state.delay + ' ms' : ctx.t('Reconnecting', '再接続中');
            connectionState(`${label} · ${state.attempt}/${state.maximum}`, 'pending');
        }
        else if (!socket && !connecting)
        {
            const exhausted = state.enabled && state.attempt === state.maximum;
            connectionState(exhausted ? ctx.t('Reconnect attempts exhausted', '再接続の試行が終了しました') : ctx.t('Disconnected', '未接続'));
        }
    }, error =>
    {
        disconnect();
        ctx.say(error.message);
    });

    /** 不正な待機列で接続を始めず、直前の有効な設定も暗黙適用しません。 */
    function configureReconnect()
    {
        try
        {
            parseDesktopReconnectDelays(reconnectDelays.value);
            reconnect.configure(reconnectDelays.value);
            reconnectDelays.setAttribute('aria-invalid', 'false');
        }
        catch
        {
            reconnectDelays.setAttribute('aria-invalid', 'true');
            throw new Error(ctx.t('Enter 1–20 comma-separated delays from 0 to 300000 milliseconds.', '0～300000の整数をカンマ区切りで1～20個指定してください。'));
        }
    }

    reconnectDelays.onchange = () =>
    {
        try
        {
            configureReconnect();
        }
        catch (error)
        {
            ctx.say(error.message);
        }
    };

    /** 入力を再送せず、現在の接続だけを解放して復帰を予約します。 */
    function recoverConnection(message)
    {
        reconnectSelection = { monitor: selected, hideCursor };
        disconnectTransport();
        ctx.say(message);
        if (!disposed && !ctx.signal.aborted)
        {
            reconnect.failed();
        }
    }

    /** 現在の接続状態を表示へ反映します。 */
    function connectionState(label, state = 'idle')
    {
        connection.textContent = label;
        connection.dataset.state = state;
    }

    connectionState(ctx.t('Disconnected', '未接続'));
    const heldKeys = new Uint8Array(384), heldButtons = new Uint8Array(3);
    const virtualKeys = new Set(), physicalKeys = new Set(), keyButtons = [];
    const cursorNames = ['default', 'text', 'wait', 'progress', 'crosshair', 'nwse-resize', 'nesw-resize', 'ew-resize', 'ns-resize', 'move', 'not-allowed', 'pointer', 'help', 'none'];
    let scanCodesSupported = false, scanCodeWarning = false, unicodeInputSupported = false, softwareKeyboard = null;
    let cursorRelay = false, cursorSupported = false, cursorRequested = false, cursorMonitor = -1, cursorName = 'default';

    /** 保持しているキーとボタンの入力状態を解除します。 */
    function clearKeys()
    {
        heldKeys.fill(0);
        heldButtons.fill(0);
        virtualKeys.clear();
        physicalKeys.clear();
        wheel = 0;
        for (const button of keyButtons)
        {
            button.setAttribute('aria-pressed', 'false');
        }
    }

    /** 受信したカーソルの位置と形状を反映します。 */
    function applyCursor()
    {
        for (const view of views)
        {
            if (view)
            {
                view.canvas.style.cursor = cursorFor(view);
            }
        }
    }

    /** 指示表示とリモートカーソルの優先順位から表示形状を選びます。
     * @returns canvasに指定するカーソル名です。
     */
    function cursorFor(view)
    {
        if (viewOnly && annotationsSupported)
        {
            return 'none';
        }
        if (!viewOnly && cursorEnabled && view.monitor.id === cursorMonitor)
        {
            return cursorName;
        }
        return 'default';
    }

    /** カーソル同期の選択状態を接続先へ通知します。 */
    function updateCursorSync()
    {
        // Subscribe at most once per connection. A disabled receiver still accepts
        // in-flight notifications, but never lets them hide the local cursor.
        if (cursorEnabled && cursorRelay && cursorSupported && !cursorRequested && socket?.readyState === WebSocket.OPEN)
        {
            cursorRequested = true;
            send(8, 1);
        }
        applyCursor();
    }

    /** 選択肢の値と表示名を作成します。
     * @returns 呼出し元が使用する値です。
     */
    function option(value, label)
    {
        const item = document.createElement('option');
        item.value = value;
        item.textContent = label;
        return item;
    }

    /** 固定された通信形式で入力を送信します。 */
    function send(kind, monitor = 0, x = 0, y = 0, code = 0, state = 0)
    {
        if (clipboard?.enabled && clipboard.hold(kind, monitor, x, y, code, state))
        {
            return;
        }
        if (kind === 5)
        {
            clearKeys();
        }

        // In view-only mode only stream settings/subscriptions, mode changes and heartbeats may leave the browser.
        // Gate the shared sender as well as the controls so delayed events cannot replay input.
        if (viewOnly && ![0, 6, 7, 8, 9, 11, 12, 13, 14, 15].includes(kind))
        {
            return;
        }
        if (localConnection && (kind === 4 || kind === 2))
        {
            const held = kind === 4 ? heldKeys : heldButtons;
            if (held[code] === state)
            {
                return;
            }
            held[code] = state;
        }
        if (socket?.readyState !== WebSocket.OPEN)
        {
            return;
        }
        if ((4096) < (socket.bufferedAmount))
        {
            recoverConnection(ctx.t('Input communication stalled; the connection was closed and pending input was discarded.', '入力通信が滞留したため切断し、保留中の入力を破棄しました。'));
            return;
        }
        inputView.setInt32(0, kind);
        inputView.setInt32(4, monitor);
        inputView.setInt32(8, x);
        inputView.setInt32(12, y);
        inputView.setInt32(16, code);
        inputView.setInt32(20, state);
        socket.send(input);
    }

    /** 確認済みのテキストを貼り付け、ユーザーが現在保持している修飾キーへ戻します。 */
    function pasteClipboard()
    {
        const modifiers = [0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0x5b, 0x5c];
        const held = modifiers.filter(key => physicalKeys.has(key) || virtualKeys.has(key));
        for (const key of modifiers)
        {
            send(4, 0, 0, 0, key, 0);
        }
        send(4, 0, 0, 0, 0xa2, 1);
        send(4, 0, 0, 0, 86, 1);
        send(4, 0, 0, 0, 86, 0);
        send(4, 0, 0, 0, 0xa2, 0);
        for (const key of held)
        {
            send(4, 0, 0, 0, key, 1);
        }
    }

    clipboard = createDesktopClipboard({

        focused: () => !document.hidden && document.hasFocus(),

        read: () => readDesktopClipboardTextAsync(navigator.clipboard),

        write: value => navigator.clipboard?.writeText(value) ?? Promise.reject(new Error('Clipboard.Permission')),

        control: value => send(15, value ? 1 : 0),

        input: send,

        release: () => send(5),

        paste: pasteClipboard,

        packet: message =>
        {
            if (viewOnly || localConnection || socket?.readyState !== WebSocket.OPEN || (4096) < (socket.bufferedAmount))
            {
                return false;
            }
            socket.send(message);
            return true;
        },

        error: code =>
        {
            if (document.hidden || !document.hasFocus())
            {
                return;
            }
            const permission = 'Clipboard.Permission', writePermission = 'Clipboard.WritePermission', large = 'Clipboard.InvalidOrLargeText', textOnly = 'Clipboard.TextOnly';
            switch (code)
            {
                case permission:
                    ctx.say(ctx.t('Clipboard access was not allowed. Use HTTPS or localhost and allow clipboard access. Ctrl+V in the desktop area can transfer pasted text.', 'クリップボードへのアクセスが許可されませんでした。HTTPSまたはlocalhostで開き、権限を確認してください。操作領域でCtrl+Vを押すと、貼り付けたテキストを転送できます。'));
                    break;
                case writePermission:
                    ctx.say(ctx.t('The browser requires a user action to receive copied host text. Click inside the desktop area to retry.', 'ホストでコピーしたテキストの反映にはブラウザー上の操作が必要です。操作領域をクリックすると再試行します。'));
                    break;
                case textOnly:
                    ctx.say(ctx.t('Only clipboard text can be synchronized.', '同期できるのはクリップボードのテキストだけです。'));
                    break;
                case large:
                    ctx.say(ctx.t('Clipboard synchronization accepts text up to 64 KiB. NUL characters are not supported.', 'クリップボード同期は64 KiBまでのテキストに対応します。NUL文字は使用できません。'));
                    break;
                default:
                    ctx.say(ctx.t('Clipboard synchronization could not be confirmed. Pending paste and input were discarded.', 'クリップボード同期を確認できなかったため、保留中の貼り付けと入力を破棄しました。'));
                    break;
            }
        }
    });
    await clipboard.enableAsync(true);

    /** 画面ごとの描画資源と一時表示を解放します。 */
    function clearViews()
    {
        annotationPointer.leave();
        cancelAnimationFrame(annotationFrame);
        annotationFrame = 0;
        cancelAnimationFrame(render);
        render = 0;
        pointer = null;
        for (const view of views)
        {
            view?.events?.abort();
            view?.video.close();
        }
        views = [];
        viewport.replaceChildren(overlay);
        if (softwareKeyboard)
        {
            viewport.append(softwareKeyboard.input);
        }
    }

    /** 映像の全画面表示を終了します。 */
    function exitVideoFullscreen()
    {
        if (document.fullscreenElement === viewport)
        {
            document.exitFullscreen().catch(() =>
            { /* The browser may already be leaving fullscreen. */ });
        }
    }

    /** 保持しているシステムキーの捕捉を解除します。 */
    function releaseKeyboard()
    {
        keyboardRequest++;
        keyboardCapture?.unlock();
        keyboardCapture = null;
    }

    /** ワークスペースは接続前にも全画面にし、映像だけの表示は現在の接続に限定します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function enterFullscreenAsync(target)
    {
        const run = generation;
        await target.requestFullscreen();
        if (disposed || target === viewport && (!socket || run !== generation))
        {
            if (document.fullscreenElement === target)
            {
                await document.exitFullscreen();
            }
            return;
        }
        if (!socket || run !== generation || document.fullscreenElement !== target)
        {
            return;
        }
        viewport.focus();
        await captureKeyboardAsync();
    }

    /** 全画面を先に開いた場合も、接続の開始後だけシステムキーを捕捉します。
     * @returns 現在の入力寿命に限定した捕捉要求です。
     */
    async function captureKeyboardAsync()
    {
        if (viewOnly || !socket || disposed || document.fullscreenElement !== root && document.fullscreenElement !== viewport)
        {
            return;
        }
        const keyboard = globalThis.navigator?.keyboard;
        if (!keyboard?.lock)
        {
            ctx.say(ctx.t('System-key capture is unavailable in this browser. Send Win remains available in the toolbar.', 'このブラウザーではシステムキーを捕捉できません。操作欄の「Winを送信」は利用できます。'));
            return;
        }
        releaseKeyboard();
        keyboardCapture = keyboard;
        const request = keyboardRequest;
        try
        {
            await keyboard.lock();
            if (request !== keyboardRequest && !keyboardCapture)
            {
                keyboard.unlock();
            }
        }
        catch
        {
            if (request !== keyboardRequest)
            {
                return;
            }
            releaseKeyboard();
            ctx.say(ctx.t('System-key capture was not allowed. Normal input and Send Win remain available.', 'システムキーの捕捉が許可されませんでした。通常の入力と「Winを送信」は利用できます。'));
        }
    }

    /** 接続と描画を終了し、保留中の解放処理を保持します。
     * @returns 呼出し元が使用する値です。
     */
    function disconnectTransport()
    {
        const previous = socket;
        socket = null;
        clipboard.connect(false);
        clipboard.leave();
        clipboardSupported = false;
        annotationPointer.leave();
        annotationsSupported = false;
        annotationState.clear();
        softwareKeyboard?.hide();
        unicodeInputSupported = false;
        releaseKeyboard();
        exitVideoFullscreen();
        generation++;
        delivery.reset();
        connecting = false;
        clearKeys();
        localConnection = false;
        targetLabel = '';
        audioRequested = false;
        cursorEnabled = hideCursor = cursorVisibilitySupported = selecting = false;
        codecDetailsSupported = viewOnlySupported = false;
        scanCodesSupported = scanCodeWarning = false;
        cursorRelay = cursorSupported = cursorRequested = false;
        cursorMonitor = -1;
        cursorName = 'default';
        clearInterval(watch);
        watch = 0;
        heartbeat = false;
        videoStatus.textContent = '';
        refreshes = 0;
        receivedBytes = 0;
        for (const value of Object.values(metrics))
        {
            value.textContent = '—';
        }
        if (autoMuted)
        {
            muted = autoMuted = false;
            updateSound();
        }
        if (previous)
        {
            previous.onmessage = previous.onclose = previous.onerror = null;
            try
            {
                if (!viewOnly && previous.readyState === WebSocket.OPEN && previous.bufferedAmount <= 4096)
                {
                    inputView.setInt32(0, 5);
                    previous.send(input);
                }
            }
            catch
            { /* 接続終了時にもホストが保持入力を解放するため、入力を再送しません。 */ }
            previous.close();
        }
        clearViews();
        viewport.append(empty);
        screens.replaceChildren();
        monitors = [];
        audioReady = false;
        audioStatus.textContent = '';
        if (audio)
        {
            closing = audio.close().catch(error =>
            {
                if (!disposed)
                {
                    ctx.say(error.message);
                }
            });
            audio = null;
        }
        cluster.disabled = host.disabled = false;
        updateControls();
        status.textContent = ctx.t('Disconnected', '未接続');
        connectionState(status.textContent);
    }

    /** 手動切断と復帰不能なエラーでは、待機中の再接続も取り消します。 */
    function disconnect()
    {
        reconnect.cancel();
        reconnectSelection = null;
        disconnectTransport();
    }

    /** 表示と接続を終了し、音声の解放が完了するまで待ちます。
     * @returns 所有資源の解放完了です。
     */
    async function disconnectAsync()
    {
        disconnect();
        await closing;
    }

    /** 準備された値を現在の描画領域へ反映します。 */
    function draw()
    {
        render = 0;
        lastDraw = performance.now();
        try
        {
            // Motion is replaceable. Leave room for ordered key/button/release
            // messages and retain only the latest unsent pointer position.
            flushPointer();
            flushWheel();
            const audible = muted ? null : audio?.playhead();
            for (const view of views)
            {
                if (view?.video.draw(audible))
                {
                    schedule();
                }
            }
        }
        catch (error)
        {
            disconnect();
            ctx.say(error.message);
        }
    }

    /** 重複した描画要求を次の一回にまとめます。 */
    function schedule()
    {
        if (!render)
        {
            render = requestAnimationFrame(draw);
        }
    }

    /** 画面座標を対象の座標系へ変換します。
     * @returns 呼出し元が使用する値です。
     */
    function point(event)
    {
        const bounds = viewport.getBoundingClientRect();
        const x = event.clientX - bounds.left - viewport.clientLeft, y = event.clientY - bounds.top - viewport.clientTop;
        if (x < 0 || y < 0 || (viewport.clientWidth) <= (x) || (viewport.clientHeight) <= (y))
        {
            return null;
        }
        for (const candidate of views)
        {
            if (candidate && mapDesktopPoint(candidate.monitor, candidate.canvas.getBoundingClientRect(), event.clientX, event.clientY, pointerPosition))
            {
                return pointerPosition;
            }
        }
        return null;
    }

    /** 最新のポインター位置を、次の表示追従と操作入力へ保持します。 */
    function movePointer(event)
    {
        // Only retain the newest event. Layout reads and pointer following happen
        // once per send, not at the mouse device's (potentially 1000 Hz) rate.
        pointerEvent.clientX = event.clientX;
        pointerEvent.clientY = event.clientY;
        pointerEvent.pointerType = event.pointerType;
        pointer = pointerEvent;
        schedule();
    }

    /** 表示を追従させ、操作が許可されている場合だけポインター位置を送信します。 */
    function flushPointer()
    {
        const expectedNative = 'native';
        const expectedMouse = 'mouse';

        const now = performance.now();
        if (audioRequested && !audioReady && (5000) < (now - audioSelectionAt))
        {
            disconnect();
            ctx.say(ctx.t('Audio selection was not confirmed.', '音声設定の反映を確認できませんでした。'));
            return;
        }
        if (!pointer || !viewOnly && (1024) < (socket?.bufferedAmount) || now - lastPointer < 16)
        {
            return;
        }
        if (scaleMode === expectedNative && followPointer && (!pointer.pointerType || pointer.pointerType === expectedMouse))
        {
            if (layout.followPointer(pointer.clientX, pointer.clientY))
            {
                positionOverlay();
            }
        }

        // View-only follows the local pointer without entering the OS input path.
        if (viewOnly)
        {
            pointer = null;
            lastPointer = now;
            return;
        }

        // Scrolling changes canvas bounds; resolve remote coordinates afterwards.
        const position = point(pointer);
        pointer = null;
        if (position)
        {
            send(1, position[0], position[1], position[2]);
            lastPointer = now;
        }
    }

    /** まとめたホイールの移動量を送信します。 */
    function flushWheel()
    {
        const now = performance.now(), delta = Math.trunc(wheel);
        if (!delta || now - lastWheel < 16 || (1024) < (socket?.bufferedAmount))
        {
            return;
        }
        wheel -= delta;
        lastWheel = now;
        send(3, wheelPosition[0], wheelPosition[1], wheelPosition[2], delta);
    }

    viewport.onpointermove = event =>
    {
        const expectedCanvas = 'CANVAS';

        if (event.target?.tagName !== expectedCanvas)
        {
            movePointer(event);
        }
    };

    /** 一台のモニターの描画と入力の所有範囲を構成します。 */
    function createMonitorView(monitor, run)
    {
        const panel = document.createElement('div');
        panel.className = 'desktop-monitor';
        const name = document.createElement('span');
        const label = document.createElement('span');
        label.textContent = monitor.name;
        name.append(label);
        const canvas = document.createElement('canvas');
        canvas.width = monitor.width;
        canvas.height = monitor.height;
        canvas.setAttribute('aria-label', monitor.name);
        panel.append(name, canvas);
        viewport.append(panel);
        const view = { monitor, canvas, panel, name, label, events: new AbortController(), displayed: 0 };
        const createVideo = workerAllowed && supportsDesktopVideoWorker(canvas, chromium ? rendering.value : 'automatic') ? createDesktopVideoWorker : createDesktopVideo;
        view.video = createVideo(monitor, canvas, {
            acceleration: acceleration.value, frameRate: videoSettings.fps, maximumBitrateKbps: appliedCodecSettings?.quality != null ? null : videoSettings.bitrate,

            active: () => !disposed && run === generation && views[monitor.id] === view,

            schedule, refresh: refreshVideo,

            audible: () => muted ? null : audio?.playhead(),

            bufferBudget: () => muted ? 150 : audio?.bufferBudget(),

            fallback: reason =>
            {
                const expectedH265 = 'h265';

                if (run !== generation || codec.value !== expectedH265)
                {
                    return;
                }
                codecNote.textContent = 'H.265: ' + reason;
                codec.value = 'h264';
                ctx.say(ctx.t('H.265 decoding failed or is unavailable in this browser. Using H.264. See Connection details.', 'H.265を復号できなかったため、H.264へ切り替えました。理由は「接続の詳細」で確認できます。'));
                choose(selected);
            },

            backendFallback: reason =>
            {
                if (run !== generation || !workerAllowed)
                {
                    return;
                }
                workerAllowed = false;
                codecNote.textContent = ctx.t('Video rendering fallback: ', '映像描画の互換経路: ') + reason;
                choose(selected);
            },

            error: error =>
            {
                if (run === generation)
                {
                    disconnect();
                    ctx.say(error.message);
                }
            }
        });
        views[monitor.id] = view;
        if (annotationsSupported)
        {
            const annotationCanvas = document.createElement('canvas');
            annotationCanvas.className = 'desktop-annotations';
            annotationCanvas.width = monitor.width;
            annotationCanvas.height = monitor.height;
            annotationCanvas.setAttribute('aria-hidden', 'true');
            panel.append(annotationCanvas);
            view.annotationCanvas = annotationCanvas;
            view.annotationContext = annotationCanvas.getContext('2d');
        }
        canvas.onpointermove = event =>
        {
            movePointer(event);
            if (viewOnly)
            {
                annotationPointer.move(event);
            }
        };
        canvas.onpointerdown = event =>
        {
            if (viewOnly)
            {
                if (annotationPointer.down(event, performance.now()))
                {
                    event.preventDefault();
                    canvas.setPointerCapture(event.pointerId);
                }
                return;
            }
            const position = point(event);
            if (!position || (2) < (event.button))
            {
                return;
            }
            event.preventDefault();
            softwareKeyboard.interrupt();
            clipboard.retryHost();
            focusInput();
            canvas.setPointerCapture(event.pointerId);
            pointer = null;
            send(2, ...position, event.button, 1);
        };
        canvas.onpointerup = event =>
        {
            if (canvas.hasPointerCapture(event.pointerId))
            {
                canvas.releasePointerCapture(event.pointerId);
            }
            if (viewOnly)
            {
                annotationPointer.up(event, performance.now());
                return;
            }
            event.preventDefault();
            pointer = null;
            const position = point(event);
            if (position && event.button <= 2)
            {
                send(2, ...position, event.button, 0);
            }
            else
            {
                send(5);
            }
        };
        canvas.onpointercancel = () =>
        {
            annotationPointer.leave();
            send(5);
        };
        canvas.onpointerleave = event =>
        {
            if (!canvas.hasPointerCapture(event.pointerId))
            {
                annotationPointer.leave();
            }
        };
        canvas.oncontextmenu = event => event.preventDefault();
        canvas.addEventListener('wheel', event =>
        {
            if (viewOnly)
            {
                return;
            }
            event.preventDefault();
            const delta = desktopWheelDelta(event);
            if (!delta)
            {
                return;
            }
            const position = point(event);
            if (!position)
            {
                return;
            }
            if (wheel && wheelPosition[0] !== position[0])
            {
                wheel = 0;
            }
            wheelPosition.set(position);
            wheel = Math.max(-1200, Math.min(1200, wheel + delta));
            schedule();
        }, { passive: false, signal: view.events.signal });
    }

    /** 選択した画面と映像設定を接続先へ通知します。 */
    function choose(id)
    {
        const expectedVp9 = 'vp9';
        const expectedH2652 = 'h265';

        if (!monitors.length)
        {
            return;
        }
        if (appliedCodecSettings && !codecDetailsSupported)
        {
            disconnect();
            ctx.say(ctx.t('Update the Core and ClientHost to use codec details.', 'コーデック詳細設定を使用するにはCoreとClientHostを更新してください。'));
            return;
        }
        send(5);
        if (!socket)
        {
            return;
        }
        clearViews();
        selected = id;
        selection++;
        selecting = true;
        status.textContent = ctx.t('Starting video…', '映像を開始中…');
        connectionState(ctx.t('Starting…', '開始中…'), 'pending');
        const run = generation;
        for (const monitor of monitors.filter(value => id === -1 || value.id === id))
        {
            createMonitorView(monitor, run);
        }
        for (const button of screens.querySelectorAll('button[data-monitor]'))
        {
            button.setAttribute('aria-pressed', String(Number(button.dataset.monitor) === selected));
        }
        applyCursor();
        if (views.filter(Boolean).length !== 1)
        {
            exitVideoFullscreen();
        }
        updateControls();
        layout.update();
        scheduleAnnotations();
        if (codecDetailsSupported)
        {
            const details = appliedCodecSettings;
            if (details)
            {
                send(9, 1, details.maximumKeyFrameInterval, details.maximumPFrames ?? -1, details.maximumBFrames, details.quality ?? -1);
            }
            else
            {
                send(9);
            }
        }
        if (videoSettings.variableBitrate && !variableBitrateSupported)
        {
            throw new Error(ctx.t('Update the Core and ClientHost to use VBR.', 'VBRを使用するにはCoreとClientHostを更新してください。'));
        }
        send(6, id, codec.value === expectedVp9 ? 1 : codec.value === expectedH2652 ? 2 : 0, selection, 1, (videoSettings.fps << 20) | videoSettings.bitrate | (hideCursor ? 0x08000000 : 0) | (videoSettings.variableBitrate ? 0x10000000 : 0));
    }

    /** 映像の再開条件を確認して再取得を要求します。 */
    function refreshVideo(reason)
    {
        if (!socket || hidden || performance.now() - lastRefresh < 1500)
        {
            return;
        }
        lastRefresh = performance.now();
        if ((3) < (++refreshes))
        {
            disconnect();
            ctx.say(ctx.t('Video stopped responding. Reconnect or select another codec/decoder.', '映像の応答が停止しました。再接続、または別のコーデック・デコーダーを選択してください。') + ' (' + reason + ')');
            return;
        }
        choose(selected);
    }

    /** 受信量と描画統計を表示へ反映します。 */
    function updateRateDisplay()
    {
        metricLabels.bitrate.textContent = appliedCodecSettings?.quality != null ? ctx.t('Quality target', '設定品質') : videoSettings.variableBitrate ? ctx.t('VBR target', 'VBR設定値') : ctx.t('CBR target', 'CBR設定値');
        metrics.bitrate.textContent = appliedCodecSettings?.quality != null ? 'CRF/CQ ' + appliedCodecSettings.quality : videoSettings.bitrate + ' kbps';
    }

    /** 映像と接続の応答期限を確認します。 */
    function checkVideo()
    {
        if (!socket || hidden)
        {
            return;
        }
        const now = performance.now();
        probeDeliveryTime();
        if (!socket)
        {
            return;
        }
        if (heartbeat && (8000) < (now - lastMessage))
        {
            recoverConnection(ctx.t('Desktop connection timed out.', 'リモートデスクトップの通信がタイムアウトしました。'));
            return;
        }
        if (connecting && (45000) < (now - connectionStarted))
        {
            recoverConnection(ctx.t('Starting the desktop connection timed out.', 'リモートデスクトップの接続開始がタイムアウトしました。'));
            return;
        }
        for (const view of views)
        {
            view?.video.tick(now);
        }
        flushPointer();
        annotationPointer.tick(now);
        flushWheel();
        if ((100) < (now - lastDraw) && views.some(view => view?.video.hasPending))
        {
            cancelAnimationFrame(render);
            render = 0;
            draw();
        }
        if ((1000) <= (now - lastStats))
        {
            const expectedVp92 = 'vp9';
            const expectedH2653 = 'h265';

            const seconds = (now - lastStats) / 1000;
            metrics.speed.textContent = (receivedBytes / 1024 / seconds).toFixed(1) + ' KiB/s';
            receivedBytes = 0;
            metrics.fps.textContent = views.filter(Boolean).map(view =>
            {
                const frames = view.video.presented;
                const fps = Math.max(0, frames - view.displayed) / seconds;
                view.displayed = frames;
                return Math.round(fps);
            }).join(' / ') || '—';
            updateRateDisplay();
            metrics.codec.textContent = codec.value === expectedVp92 ? 'VP9' : codec.value === expectedH2653 ? 'H.265' : 'H.264';
            lastStats = now;
            const delay = Math.max(0, ...views.filter(Boolean).map(view => view.video.delay));
            metrics.delay.textContent = Math.round(delay) + ' ms';
            updateDeliveryDisplay(now);
            videoStatus.textContent = ctx.t('Receive-to-display: ', '受信→表示: ') + Math.round(delay) + ' ms · ' +
                ctx.t('Decoder preference: ', 'デコーダー指定: ') + acceleration.options[acceleration.selectedIndex]?.textContent +
                ' · ' + views.filter(Boolean).map(view => `${view.video.renderer} · Decode ${Math.round(view.video.decodeDelay)} ms · Draw ${view.video.drawCost.toFixed(1)} ms · Flush ${view.video.flushes} · Replay ${view.video.replays}`).join(' / ');

            // Recovery means images are displayed again; network/audio latency
            // is not evidence that the decoder still failed.
            if (views.some(Boolean) && views.every(view => !view || view.video.hasDisplayed) && (5000) < (now - lastRefresh))
            {
                refreshes = 0;
            }
        }
    }

    /** 音声の接続・再生状態を表示します。 */
    function updateAudioStatus()
    {
        const expectedOpus = 'opus';

        audioStatus.textContent = audioReady ? ctx.t('System audio · 48 kHz stereo', 'システム音声 · 48 kHz ステレオ') +
            (audioSelection.codec === expectedOpus ? ` · Opus ${audioSelection.bitrate} kbps` : ' · PCM') +
            ctx.t(' · Buffer up to ', ' · 最大バッファ ') + Math.round(audioSamples / 48) + ' ms' +
            ctx.t(' · Prebuffer ', ' · 再生前蓄積 ') + Math.round(audioSamples / 96) + ' ms' +
            (autoMuted ? ctx.t(' · Local connection: initially muted to prevent feedback', ' · 同じPCへの接続のため、フィードバックを避けて消音しています') : '') : '';
    }

    /** 共有指示のフレームを検証して表示へ反映します。 */
    function receiveAnnotationFrame(header, id, length, payload)
    {
        if (!annotationsSupported || (65536) < (length) || id !== 0 || header.getInt32(12) !== 0)
        {
            throw new Error('Invalid viewer annotation frame');
        }
        const annotations = JSON.parse(text.decode(payload));
        if (annotations.available === false)
        {
            annotationPointer.leave();
            annotationsSupported = false;
            annotationState.clear();
            cancelAnimationFrame(annotationFrame);
            paintAnnotations();
            applyCursor();
            ctx.say(ctx.t('Shared viewer pointers could not start on this host. Video and view-only mode remain available.', '接続先で共有指示表示を開始できませんでした。映像と未操作モードは引き続き利用できます。'));
            return;
        }
        annotationState.receive(annotations, performance.now());
        scheduleAnnotations();
        return;
    }

    /** カーソルの形状と対象画面を検証して反映します。 */
    function receiveCursorFrame(header, id, length)
    {
        if (!cursorRequested || length !== 4 || id < 0 || (monitors.length) <= (id) || header.getInt32(12) !== 0)
        {
            throw new Error('Invalid desktop cursor');
        }
        const shape = header.getInt32(24);
        if (shape < 0 || (cursorNames.length) <= (shape))
        {
            throw new Error('Invalid desktop cursor shape');
        }
        cursorMonitor = id;
        cursorName = cursorNames[shape];
        applyCursor();
        return;
    }

    /** 接続先が通知した対応機能と初期の音声状態を反映します。 */
    function receiveConnectionInfo(payload)
    {
        const connection = JSON.parse(text.decode(payload));
        delivery.enable(connection.timing === true);
        probeDeliveryTime();
        localConnection = !!connection.localAudioMuted;
        targetLabel = connection.clusterId + ' / ' + connection.hostId;
        cursorRelay = connection.cursorShapes === true;
        clipboardSupported = connection.clipboardText === true;
        variableBitrateSupported = connection.variableBitrate === true;
        viewOnlySupported = connection.viewOnly === true;

        // Negotiate before selecting video so a slow encoder probe cannot delay the initial mode.
        if (viewOnlySupported)
        {
            send(11, viewOnly ? 1 : 0);
        }
        annotationsSupported = connection.annotations === true;
        if (annotationsSupported)
        {
            send(12, 1);
        }
        codecDetailsSupported = connection.codecDetails === true;
        updateControls();
        if (connection.localAudioMuted)
        {
            muted = autoMuted = true;
            audio?.mute(true);
            updateSound();
        }
        return;
    }

    /** 画面一覧を更新し、音声方式に応じた映像選択を開始します。 */
    function receiveMonitors(payload)
    {
        const expectedOpus2 = 'opus';

        monitors = JSON.parse(text.decode(payload));
        cursorEnabled = hideCursor = reconnectSelection?.hideCursor ?? (cursorRelay && monitors.length !== 0 && monitors.every(monitor => monitor.mousePresent === true));
        screens.replaceChildren();
        for (const monitor of monitors)
        {
            const number = String(monitor.id + 1), button = ctx.button('', () => choose(monitor.id));
            const symbol = document.createElement('span');
            symbol.className = 'desktop-screen-symbol';
            symbol.setAttribute('aria-hidden', 'true');
            const caption = document.createElement('span');
            caption.className = 'desktop-screen-number';
            caption.textContent = number;
            symbol.append(ctx.icon('monitor'), caption);
            button.append(symbol);
            button.setAttribute('aria-label', ctx.t('Display ', 'ディスプレイ ') + number + ': ' + monitor.name);
            button.title = `${button.getAttribute('aria-label')} · ${monitor.width} × ${monitor.height}`;
            button.dataset.monitor = monitor.id;
            screens.append(button);
        }
        const all = ctx.button(ctx.t('All screens side by side', '全画面を横並び'), () => choose(-1));
        all.setAttribute('aria-label', ctx.t('All screens side by side', '全画面を横並び'));
        all.textContent = ctx.t('All', 'すべて');
        all.title = all.getAttribute('aria-label');
        all.dataset.monitor = -1;
        screens.append(all);
        const previousMonitor = reconnectSelection?.monitor;
        selected = previousMonitor === -1 || monitors.some(monitor => monitor.id === previousMonitor)
            ? previousMonitor : monitors.find(monitor => monitor.primary)?.id ?? monitors[0].id;

        // Configure audio before a potentially slow video encoder probe
        // occupies the worker's ordered control-message reader.
        if (audioSelection.codec !== expectedOpus2)
        {
            choose(selected);
        }
        status.textContent = ctx.t('Starting encoder…', 'エンコーダを開始中…');
        return;
    }

    /** 映像設定の応答を検証して各デコーダーへ適用します。 */
    function applyEncoderSettings(encoders)
    {
        for (const value of encoders)
        {
            const actual = value.codec === 1 ? 'vp9' : value.codec === 2 ? 'h265' : 'h264';
            if (actual !== codec.value)
            {
                throw new Error(ctx.t('Update the Core to use the selected codec.', '選択したコーデックを使うには Core を更新してください。'));
            }
            if (value.framesPerSecond !== videoSettings.fps || value.maximumBitrateKbps !== (appliedCodecSettings?.quality != null ? null : videoSettings.bitrate))
            {
                throw new Error(ctx.t('Update the Core to apply the requested fps and rate-control mode.', '指定したfps・品質／ビットレート方式を適用するには Core を更新してください。'));
            }
            if (!sameDesktopCodecSettings(appliedCodecSettings, value.codecDetails))
            {
                throw new Error(ctx.t('Codec settings were not applied.', 'コーデック設定を反映できませんでした。'));
            }
            if ((value.variableBitrate === true) !== videoSettings.variableBitrate)
            {
                throw new Error(ctx.t('VBR setting was not applied.', 'VBR設定を反映できませんでした。'));
            }
            views[value.id]?.video.setCodec(actual);
            heartbeat ||= !!value.heartbeat;
        }
    }

    /** 要求した映像設定と応答を照合して接続状態を更新します。 */
    function receiveEncoderInfo(payload)
    {
        const encoders = JSON.parse(text.decode(payload));
        if (encoders.some(value => value.selection != null && value.selection !== selection))
        {
            return;
        }
        applyEncoderSettings(encoders);

        // An older relay rejects kind 11, and an older Core rejects the
        // subscription. Both ends must advertise support before opting in.
        scanCodesSupported = (0) < (encoders.length) && encoders.every(value => value.scanCodeInput === true);
        unicodeInputSupported = (0) < (encoders.length) && encoders.every(value => value.unicodeInput === true);
        cursorVisibilitySupported = (0) < (encoders.length) && encoders.every(value => value.cursorVisibility === true);
        if (cursorVisibilitySupported && encoders.some(value => value.hideCursor !== hideCursor))
        {
            throw new Error(ctx.t('Host cursor visibility was not applied.', 'ホストカーソルの表示設定を反映できませんでした。'));
        }
        cursorSupported = (0) < (encoders.length) && encoders.every(value => value.cursorShapes === true);
        selecting = false;
        updateControls();
        updateCursorSync();
        if (!socket)
        {
            return;
        }
        status.textContent = targetLabel + ' · ' + encoders.map(value => `${monitors[value.id]?.name}: ${value.encoder}`).join(' · ') +
                    ` · ${videoSettings.fps} fps` + (appliedCodecSettings?.quality != null ? ctx.t(' · Quality target (no bitrate cap)', ' · 品質指定（ビットレート上限なし）') : ` · ${videoSettings.bitrate} kbps`) +
                    (encoders.every(value => value.administratorInput) ? ctx.t(' · Administrator input', ' · 管理者権限で操作') : ctx.t(' · User input: elevated apps require an administrator Core', ' · 通常権限で操作：昇格アプリの操作には管理者権限のCoreが必要です'));
        if (appliedCodecSettings)
        {
            status.textContent += ` · GOP ≤ ${encoders[0]?.effectiveKeyFrameInterval} · B ≤ ${appliedCodecSettings.maximumBFrames}` + (appliedCodecSettings.quality == null ? '' : ` · CRF/CQ ${appliedCodecSettings.quality}`);
        }
        if (videoSettings.variableBitrate)
        {
            status.textContent += ' · VBR';
        }
        updateRateDisplay();
        connecting = false;
        reconnect.connected();
        connectionState(ctx.t('Connected', '接続中'), 'connected');
        return;
    }

    /** 選択した音声形式を照合し、音声の準備状態を更新します。 */
    function receiveAudioFormat(payload)
    {
        const expectedOpus3 = 'opus';

        const format = JSON.parse(text.decode(payload));
        if (audioSelection.codec === expectedOpus3 && format.format !== expectedOpus3)
        {
            if (!format.codecs?.includes('opus') || !format.opusBitrates?.includes(audioSelection.bitrate))
            {
                throw new Error(ctx.t('Update the Core to use the selected Opus audio setting.', '選択したOpus音声設定を使うにはCoreを更新してください。'));
            }
            if (!audioRequested)
            {
                audioRequested = true;
                audioSelectionAt = performance.now();
                send(7, 1, audioSelection.bitrate);
                choose(selected);
            }
            return;
        }
        if (audioSelection.codec === expectedOpus3 && format.bitrateKbps !== audioSelection.bitrate)
        {
            throw new Error('Desktop audio bitrate differs from the requested setting.');
        }
        audio?.format(format);
        audioReady = true;
        updateAudioStatus();
        return;
    }

    /** 時計照会と映像送信の印を元の映像データから分けて処理します。 */
    function receiveDeliveryTime(header, kind, id, length)
    {
        if (kind === 13)
        {
            if (length !== 24 || id !== 0 || header.getInt32(12) !== 0 || header.getInt32(28) !== 0)
            {
                throw new Error('Invalid desktop clock reply');
            }
            delivery.reply(header.getInt32(24), Number(header.getBigInt64(32)), Number(header.getBigInt64(40)), Number(header.getBigInt64(16)), lastMessage);
            return;
        }
        if (length !== 8 || header.getInt32(12) !== 0)
        {
            throw new Error('Invalid desktop delivery time');
        }
        delivery.mark(id, Number(header.getBigInt64(24)), Number(header.getBigInt64(16)));
    }

    /** 未対応・未校正は数値にせず、直近の映像の到着時間を表示します。 */
    function updateDeliveryDisplay(now)
    {
        const transfer = delivery.value(views.filter(Boolean).map(view => view.monitor.id), now);
        metrics.delivery.textContent = transfer == null ? '—' : Math.round(transfer) + ' ms';
    }

    /** 交渉済みのテキスト通知だけを同期処理へ渡します。 */
    function receiveClipboardFrame(header, id, payload)
    {
        if (!clipboardSupported || id !== 0 || header.getInt32(12) !== 0)
        {
            throw new Error('Invalid desktop clipboard frame');
        }
        clipboard.receive(payload);
    }

    /** 受信データを検証し、現在の状態へ取り込みます。 */
    function receive(event)
    {
        try
        {
            if (!(event.data instanceof ArrayBuffer) || event.data.byteLength < 24)
            {
                throw new Error('Invalid desktop frame');
            }
            const header = new DataView(event.data), kind = header.getInt32(0), id = header.getInt32(4), length = header.getInt32(8);
            if (length < 1 || (8 * 1024 * 1024) < (length) || event.data.byteLength !== 24 + length)
            {
                throw new Error('Invalid desktop payload');
            }
            lastMessage = performance.now();
            receivedBytes += event.data.byteLength;
            const payload = new Uint8Array(event.data, 24);
            if (kind === 13 || kind === 14)
            {
                receiveDeliveryTime(header, kind, id, length);
                return;
            }
            switch (kind)
            {
                case 15:
                {
                    receiveClipboardFrame(header, id, payload);
                    return;
                }
                case 12:
                {
                    receiveAnnotationFrame(header, id, length, payload);
                    return;
                }
                case 11:
                {
                    receiveCursorFrame(header, id, length);
                    return;
                }
                case 9:
                {
                    heartbeat = true;

                    // Flagged heartbeats report a lost video reference. Ordinary
                    // static screens remain healthy and need no periodic restart.
                    if (header.getInt32(12) === 1)
                    {
                        views[id]?.video.discontinuity();
                    }
                    return;
                }
                case 8:
                {
                    const fault = JSON.parse(text.decode(payload));
                    const detail = [fault.stage, fault.type, fault.code].map(value => String(value).slice(0, 64)).join(' / ');
                    disconnect();
                    ctx.say(ctx.t('Desktop stopped: ', 'リモートデスクトップ停止: ') + detail);
                    return;
                }
                case 7:
                {
                    receiveConnectionInfo(payload);
                    return;
                }
                case 1:
                {
                    receiveMonitors(payload);
                    return;
                }
                case 3:
                {
                    receiveEncoderInfo(payload);
                    return;
                }
                case 4:
                {
                    receiveAudioFormat(payload);
                    return;
                }
            }
            if (hidden)
            {
                return;
            }
            if (kind === 5 || kind === 6 || kind === 10)
            {
                if (audioReady)
                {
                    audio?.receive(event.data);
                }
                return;
            }
            const view = views[id];
            if (kind !== 2 || !view)
            {
                return;
            }
            delivery.frame(id, Number(header.getBigInt64(16)), lastMessage);
            view.video.receive(payload, header.getInt32(12) === 1, header.getUint32(16) * 4294967296 + header.getUint32(20));
        }
        catch (error)
        {
            disconnect();
            ctx.say(error.message);
        }
    }

    /** 対応を確認したCoreにだけ、時計差を求める照会を一つ送ります。 */
    function probeDeliveryTime()
    {
        const probe = delivery.probe(performance.now());
        if (probe)
        {
            send(14, ...probe);
        }
    }

    /** 映像と音声の数値設定を接続開始前に検証します。 */
    function validateStreamSettings()
    {
        for (const setting of appliedCodecSettings?.quality != null ? [fps, audioBuffer] : [fps, bitrate, audioBuffer])
        {
            const value = Number(setting.field.value);
            if (!Number.isInteger(value) || value < Number(setting.field.min) || (Number(setting.field.max)) < (value))
            {
                throw new Error(ctx.t('Enter valid integer fps, bitrate and audio buffer values.', 'fps・ビットレート・音声バッファに範囲内の整数を入力してください。'));
            }
        }
    }

    /** 接続対象と応答を確認して通信を開始します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function connectAsync(retrying = false)
    {
        const expectedOpus4 = 'opus';

        if (!retrying)
        {
            reconnectSelection = null;
            if (reconnectState.enabled)
            {
                configureReconnect();
            }
            else
            {
                reconnect.cancel();
            }
        }
        disconnectTransport();
        const run = generation;
        connecting = true;
        updateControls();
        await closing;
        if (disposed || ctx.signal.aborted || run !== generation)
        {
            return;
        }
        cursorEnabled = hideCursor = false;
        workerAllowed = true;
        codecNote.textContent = '';
        ctx.say('');
        if (!host.value)
        {
            disconnect();
            ctx.say(ctx.t('Select a host.', 'ホストを選択してください。'));
            return;
        }
        if (!window.VideoDecoder || !window.isSecureContext)
        {
            disconnect();
            throw new Error(ctx.t('This browser needs WebCodecs in a secure context (localhost or HTTPS).', 'WebCodecs 対応ブラウザの localhost または HTTPS で開いてください。'));
        }
        try
        {
            appliedCodecSettings = readCodecSettings();
            validateStreamSettings();
        }
        catch (error)
        {
            disconnect();
            throw error;
        }
        videoSettings = { fps: Number(fps.field.value), bitrate: selectedBitrate(appliedCodecSettings), variableBitrate: variableBitrate && appliedCodecSettings?.quality == null };
        audioSamples = Number(audioBuffer.field.value);
        variableBitrateSupported = false;
        audioSelection = { codec: audioCodec.value, bitrate: audioCodec.value === expectedOpus4 ? Number(audioBitrate.value) : 0 };
        audio = createDesktopAudio(audioSamples, { codec: audioSelection.codec,

            error: error =>
            {
                if (run === generation)
                {
                    disconnect();
                    ctx.say(error.message);
                }
            } });
        try
        {
            await audio.start();
        }
        catch (error)
        {
            if (run === generation)
            {
                await disconnectAsync();
            }
            throw error;
        }
        if (disposed || run !== generation)
        {
            return;
        }
        audio.mute(muted);
        openSocket(run);
        await captureKeyboardAsync();
    }

    /** 一回の接続だけに受信処理と監視を結び付けます。 */
    function openSocket(run)
    {
        const expectedHttps = 'https:';
        const url = new URL('/client/desktop', location.href);
        url.protocol = location.protocol === expectedHttps ? 'wss:' : 'ws:';
        url.searchParams.set('host', host.value);
        url.searchParams.set('timing', '1');
        url.searchParams.set('clipboard', '1');
        if (cluster.value)
        {
            url.searchParams.set('cluster', cluster.value);
        }
        socket = new WebSocket(url);
        socket.binaryType = 'arraybuffer';
        hidden = !!document.hidden;
        lastMessage = lastStats = performance.now();
        connectionStarted = lastMessage;
        receivedBytes = 0;
        lastRefresh = -Infinity;
        refreshes = 0;
        watch = setInterval(checkVideo, 25);
        cluster.disabled = host.disabled = true;
        updateControls();
        status.textContent = ctx.t('Connecting…', '接続中…');
        connectionState(status.textContent, 'pending');
        const current = socket;
        socket.onmessage = event =>
        {
            if (run === generation && current === socket)
            {
                receive(event);
            }
        };
        socket.onclose = event =>
        {
            if (run !== generation || current !== socket)
            {
                return;
            }
            const reason = desktopDisconnectReason(event), message = ctx.t(...reason.message) + ' (' + event.code + ')';
            if (reason.retryable)
            {
                recoverConnection(message);
                return;
            }
            disconnect();
            ctx.say(message);
        };
        socket.onerror = () =>
        {
            if (run === generation && current === socket)
            {
                ctx.say(ctx.t('Desktop communication failed. Waiting for the connection result.', 'リモートデスクトップの通信に失敗しました。切断結果を確認中です。'));
            }
        };
    }

    /** 現在の接続候補をホストごとにまとめます。 */
    function hosts()
    {
        const previous = host.value;
        host.replaceChildren(option('', ctx.t('Select a host', 'ホストを選択')));
        for (const value of [...new Set(peers.filter(peer => !cluster.value || peer.clusterId === cluster.value).map(peer => peer.hostId))].sort())
        {
            host.append(option(value, value));
        }
        if ([...host.options].some(item => item.value === previous))
        {
            host.value = previous;
        }
    }

    /** 現在の状態を再確認して表示と操作を更新します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function refreshAsync()
    {
        peers = await ctx.peers();
        if (disposed)
        {
            return;
        }
        const previous = cluster.value;
        cluster.replaceChildren(option('', ctx.t('Automatic cluster', 'クラスタ自動選定')));
        for (const value of [...new Set(peers.map(peer => peer.clusterId))].sort())
        {
            cluster.append(option(value, value));
        }
        if ([...cluster.options].some(item => item.value === previous))
        {
            cluster.value = previous;
        }
        hosts();
    }

    const start = ctx.button(ctx.t('Connect', '接続'), () => connectAsync()), stop = ctx.button(ctx.t('Disconnect', '切断'), disconnect);
    const reconnectButton = ctx.button(ctx.t('Automatic reconnect', '自動再接続'), () =>
    {
        const enabling = !reconnectState.enabled, trying = reconnectState.trying;
        if (enabling)
        {
            configureReconnect();
        }
        reconnect.enable(enabling);
        if (!enabling && trying)
        {
            disconnect();
        }
    });
    action(reconnectButton, ctx.t('Automatic reconnect', '自動再接続'), 'refresh');
    reconnectButton.setAttribute('aria-pressed', 'false');
    reconnectButton.title = ctx.t('Automatically reconnect after communication failures. The default is three attempts: immediately, then 3 seconds after failure, then 10 seconds after failure. Success resets the attempts. Manual disconnection stops retries.', '通信切断時に自動再接続します。既定は即時、失敗後3秒、次の失敗後10秒の3回です。接続成功で試行回数をリセットし、手動切断で中止します。');
    const reload = ctx.button(ctx.t('Refresh hosts', 'ホスト一覧を更新'), refreshAsync);
    const fullscreen = ctx.button(ctx.t('Fullscreen', '全画面表示'), () => document.fullscreenElement ? document.exitFullscreen() : enterFullscreenAsync(root));
    const videoFullscreen = ctx.button(ctx.t('Video fullscreen', '映像のみ全画面'), () => views.filter(Boolean).length === 1 ? enterFullscreenAsync(viewport) : undefined);
    const follow = ctx.button(ctx.t('Follow pointer', 'マウス追従'), () =>
    {
        followPointer = !followPointer;
        follow.setAttribute('aria-pressed', String(followPointer));
    });
    const keybar = document.createElement('div');
    keybar.className = 'desktop-keybar';
    keybar.setAttribute('role', 'group');
    keybar.setAttribute('aria-label', ctx.t('Remote keyboard', 'リモートキー操作'));
    const keyboardButton = ctx.button('', () => softwareKeyboard.toggle());
    action(keyboardButton, ctx.t('Show software keyboard', 'ソフトウェアキーボードを表示'), 'keyboard');
    const viewOnlyButton = ctx.button(ctx.t('View-only mode', '未操作モード'), () =>
    {
        const next = !viewOnly;
        annotationPointer.leave();
        softwareKeyboard?.hide();
        releaseKeyboard();
        pointer = null;
        wheel = 0;
        if (next)
        {
            send(5);
        }

        // Release previously held input before disabling the sender.
        clearKeys();
        viewOnly = next;
        if (viewOnlySupported)
        {
            send(11, viewOnly ? 1 : 0);
        }
        updateControls();
        applyCursor();
    });
    action(viewOnlyButton, ctx.t('View-only mode', '未操作モード'), 'view-only', ctx.t('View only', '未操作'));

    /** リモート入力に対応する要素へフォーカスを移します。 */
    function focusInput()
    {
        if (softwareKeyboard?.active)
        {
            softwareKeyboard.focus();
        }
        else
        {
            viewport.focus();
        }
    }

    softwareKeyboard = createDesktopKeyboard(viewport, keyboardButton, ctx, {
        available: () => !viewOnly && unicodeInputSupported && socket?.readyState === WebSocket.OPEN && !selecting && views.some(Boolean),

        release: () => send(5),

        owns: target => target === keyboardButton || target === viewport || keybar.contains(target),

        key: key =>
        {
            send(4, 0, 0, 0, key, 1);
            if (!virtualKeys.has(key) && !physicalKeys.has(key))
            {
                send(4, 0, 0, 0, key, 0);
            }
        },

        text: code =>
        {
            const modified = [0xa2, 0xa3, 0xa4, 0xa5, 0x5b, 0x5c].some(key => virtualKeys.has(key) || physicalKeys.has(key));
            if (modified && ((65) <= (code) && code <= 90 || (97) <= (code) && code <= 122 || (48) <= (code) && code <= 57))
            {
                const key = (97) <= (code) ? code - 32 : code;
                send(4, 0, 0, 0, key, 1);
                if (!physicalKeys.has(key) && !virtualKeys.has(key))
                {
                    send(4, 0, 0, 0, key, 0);
                }
            }
            else
            {
                send(10, 0, 0, 0, code);
            }
        },

        physical: (event, down) =>
        {
            const key = physicalKey(event);
            if (!key || !(physicalKeys.has(key) || event.ctrlKey || event.altKey || event.metaKey || (0xa0) <= (key) && key <= 0xa5 || key === 0x5b || key === 0x5c))
            {
                return false;
            }
            softwareKeyboard.interrupt();
            if (down)
            {
                viewport.onkeydown(event);
            }
            else
            {
                viewport.onkeyup(event);
            }
            return true;
        }
    });
    for (const [label, key] of [['Win', 0x5b], ['Ctrl', 0xa2], ['Alt', 0xa4], ['Del', 0x2e], ['Esc', 0x1b], ['PrSc', 0x2c], ['Shift', 0xa0], ['Tab', 0x09]])
    {
        const button = ctx.button(label, () =>
        {
            if (viewOnly || socket?.readyState !== WebSocket.OPEN || !views.some(Boolean))
            {
                return;
            }
            softwareKeyboard.interrupt();
            const pressed = !virtualKeys.has(key);
            if (pressed)
            {
                virtualKeys.add(key);
            }
            else
            {
                virtualKeys.delete(key);
            }
            button.setAttribute('aria-pressed', String(pressed));
            if (!physicalKeys.has(key))
            {
                send(4, 0, 0, 0, key, pressed ? 1 : 0);
            }
            focusInput();
        });
        button.className = 'desktop-key quiet';
        button.setAttribute('aria-pressed', 'false');
        button.title = ctx.t('Toggle key down / up: ', 'キーの押下／解放を切り替え: ') + label;
        keyButtons.push(button);
        keybar.append(button);
    }
    const displaySeparator = document.createElement('span');
    displaySeparator.className = 'desktop-key-separator';
    displaySeparator.setAttribute('aria-hidden', 'true');
    const keyboardSeparator = document.createElement('span');
    keyboardSeparator.className = 'desktop-key-separator';
    keyboardSeparator.setAttribute('aria-hidden', 'true');
    const keySeparator = document.createElement('span');
    keySeparator.className = 'desktop-key-separator';
    keySeparator.setAttribute('aria-hidden', 'true');
    const statistics = ctx.button(ctx.t('Statistics overlay', '統計オーバーレイ'), () =>
    {
        overlay.hidden = !overlay.hidden;
        statistics.setAttribute('aria-pressed', String(!overlay.hidden));
    });
    document.addEventListener('fullscreenchange', () =>
    {
        fullscreen.setAttribute('aria-pressed', String(document.fullscreenElement === root));
        videoFullscreen.setAttribute('aria-pressed', String(document.fullscreenElement === viewport));
        action(fullscreen, document.fullscreenElement ? ctx.t('Exit fullscreen', '全画面表示を終了') : ctx.t('Fullscreen', '全画面表示'), 'fullscreen');
        if (document.fullscreenElement !== root && document.fullscreenElement !== viewport)
        {
            releaseKeyboard();
            send(5);
        }
    }, { signal: ctx.signal });

    /** 対応する操作可能な別PC接続だけで、明示した同期設定を有効にします。 */
    function updateClipboardControls()
    {
        const previous = clipboard?.available;
        clipboard?.connect(clipboardSupported && !viewOnly && !localConnection && socket?.readyState === WebSocket.OPEN);
        if (!previous && clipboard?.available && (clipboardPointerInside || viewport.contains(document.activeElement)))
        {
            activateClipboard();
        }
        clipboardSync.disabled = !clipboard?.available;
        clipboardSync.setAttribute('aria-pressed', String(clipboard?.enabled === true));
    }

    /** 現在の接続・選択・処理中の状態を操作欄へ反映します。 */
    function updateControls()
    {
        updateClipboardControls();
        const quality = codecPanel?.qualitySelected() === true;
        if (quality)
        {
            variableBitrate = false;
        }
        vbr.disabled = !!socket || quality;
        vbr.setAttribute('aria-pressed', String(variableBitrate));
        vbr.dataset.quality = String(quality);
        viewOnlyButton.setAttribute('aria-pressed', String(viewOnly));
        viewport.setAttribute('aria-label', viewOnly ? ctx.t('Remote desktop. View-only mode.', 'リモートデスクトップ。未操作モード。')
            : ctx.t('Remote desktop. Click to send keyboard input.', 'リモートデスクトップ。クリックしてキーボード操作を開始します。'));
        softwareKeyboard?.update();
        codecPanel?.update(codec.value, !!socket, codecDetailsSupported, selecting);
        follow.disabled = scaleMode !== 'native';
        cursorSync.disabled = !canControlCursor();
        cursorSync.setAttribute('aria-pressed', String(cursorEnabled));
        const busy = !!socket || connecting || reconnectState.pending;
        reconnectButton.setAttribute('aria-pressed', String(reconnectState.enabled));
        reconnectDelays.disabled = busy;
        start.disabled = busy;
        stop.disabled = !busy;
        cluster.disabled = host.disabled = busy;
        for (const button of keyButtons)
        {
            button.disabled = viewOnly || socket?.readyState !== WebSocket.OPEN || !views.some(Boolean);
        }
        reload.disabled = busy;
        videoFullscreen.disabled = views.filter(Boolean).length !== 1;
        codec.disabled = acceleration.disabled = rendering.disabled = fps.field.disabled = audioBuffer.field.disabled = audioCodec.disabled = busy;
        bitrate.field.disabled = busy || codecPanel?.qualitySelected() === true;
        audioBitrate.disabled = busy || audioCodec.value !== 'opus';
        transmissionPreset.disabled = busy;
        updateTransmissionPreset();
    }

    const sound = ctx.button(ctx.t('Mute audio', '音声をミュート'), () =>
    {
        autoMuted = false;
        muted = !muted;
        audio?.mute(muted);
        updateAudioStatus();
        updateSound();
    });

    /** ミュートと音声の選択状態を反映します。 */
    function updateSound()
    {
        action(sound, muted ? ctx.t('Unmute audio', 'ミュートを解除') : ctx.t('Mute audio', '音声をミュート'), muted ? 'volume-off' : 'volume');
        sound.setAttribute('aria-pressed', String(muted));
        sound.dataset.muted = String(muted);
        if (autoMuted)
        {
            sound.title = ctx.t('Local connection: muted to prevent feedback. Click to unmute.', '同じPCへの接続のため、ハウリングを防ぐ自動ミュート中です。クリックで解除します。');
        }
    }

    codec.append(option('vp9', 'VP9'));
    if (experimental)
    {
        codec.append(option('h264', 'H.264'), option('h265', 'H.265'));
    }
    codec.value = defaultDesktopTransmissionPreset.codec;
    codec.onchange = updateControls;
    acceleration.append(option('prefer-hardware', ctx.t('Hardware preferred', 'ハードウェア優先')), option('no-preference', ctx.t('Browser default', 'ブラウザー標準')), option('prefer-software', ctx.t('Software', 'ソフトウェア')));
    acceleration.value = 'prefer-hardware';
    rendering.append(option('inline', 'Canvas2D'), option('worker', 'WebGL'));
    rendering.value = 'inline';
    action(cursorSync, ctx.t('Cursor synchronization', 'カーソル同期'), 'pointer');
    cursorSync.title = ctx.t('Synchronize cursor shapes and hide the host cursor in captured video. Defaults to on only when the host confirms a connected mouse; absent or unknown defaults to off. Requires support from the Core and ClientHost.', 'カーソル形状を同期し、映像内のホストカーソルを非表示にします。接続時にホストのマウスを確認できた場合だけ既定でON、未接続・不明はOFFです。CoreとClientHostの対応が必要です。');
    cursorSync.setAttribute('aria-pressed', 'false');
    action(clipboardSync, ctx.t('Clipboard synchronization', 'クリップボード同期'), 'clipboard');
    clipboardSync.setAttribute('aria-pressed', String(clipboard.enabled));
    clipboardSync.title = ctx.t('Synchronize text with the host. The client takes priority on re-entry and paste. Requires browser clipboard permission. Disabled for view-only and same-PC connections.', 'ホストとテキストを同期します。操作領域への再入場と貼り付け時はクライアントの内容を優先します。ブラウザーのクリップボード許可が必要です。未操作モードと同一PC接続では無効です。');
    audioCodec.append(option('f32le', ctx.t('PCM · Uncompressed', 'PCM · 非圧縮')));
    const opusSupported = await supportsDesktopOpus();
    if (opusSupported)
    {
        audioCodec.append(option('opus', 'Opus'));
    }
    audioCodec.value = opusSupported ? 'opus' : 'f32le';
    for (const rate of [16, 24, 32, 48, 64, 96, 128, 192, 256])
    {
        audioBitrate.append(option(String(rate), rate + ' kbps'));
    }
    audioBitrate.value = String(defaultDesktopTransmissionPreset.audioBitrate);
    audioCodec.onchange = updateControls;
    initializeTransmissionPresets();
    action(sizing, ctx.t('Dot by dot (1:1)', 'Dot by dot（1:1）'), 'one-to-one');
    sizing.setAttribute('aria-pressed', 'false');
    action(follow, ctx.t('Follow pointer', 'マウス追従'), 'follow-pointer');
    follow.setAttribute('aria-pressed', 'true');
    start.className = 'primary desktop-connect';
    start.append(ctx.icon('play'));
    action(stop, ctx.t('Disconnect', '接続解除'), 'stop', ctx.t('Disconnect', '接続解除'));
    action(reload, ctx.t('Refresh hosts', 'ホスト一覧を更新'), 'refresh');
    updateSound();
    action(fullscreen, ctx.t('Fullscreen', '全画面表示'), 'fullscreen');
    action(videoFullscreen, ctx.t('Video fullscreen', '映像のみ全画面'), 'video-fullscreen');
    fullscreen.setAttribute('aria-pressed', 'false');
    videoFullscreen.setAttribute('aria-pressed', 'false');
    action(statistics, ctx.t('Statistics overlay', '統計オーバーレイ'), 'statistics');
    statistics.setAttribute('aria-pressed', String(!overlay.hidden));
    if (experimental)
    {
        toolbar.append(cluster);
    }
    toolbar.append(transmissionPreset, host, start, stop, connection, sound, fullscreen, videoFullscreen, cursorSync, clipboardSync, sizing, follow);
    toolbar.append(screens, keyboardSeparator, reconnectButton, viewOnlyButton, keyboardButton, displaySeparator, keybar, keySeparator, reload);
    cluster.onchange = hosts;
    rail.append(settingsMenu.element);
    if (codecMenu)
    {
        rail.append(codecMenu.element);
    }
    rail.append(statistics, detailsMenu.element);
    document.addEventListener('pointerdown', event =>
    {
        if (!rail.contains(event.target))
        {
            for (const item of menus)
            {
                item.open = false;
            }
        }
    }, { signal: ctx.signal });
    document.addEventListener('keydown', event =>
    {
        const expectedEscape = 'Escape';

        if (event.key === expectedEscape && menus.some(item => item.open))
        {
            event.preventDefault();
            event.stopPropagation();
            for (const item of menus)
            {
                if (item.open)
                {
                    item.open = false;
                    item.querySelector('summary').focus();
                }
            }
            return;
        }
        if (event.key !== expectedEscape || !fullscreenEscape && document.fullscreenElement !== viewport)
        {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        if (!fullscreenEscape)
        {
            fullscreenEscape = true;
            send(5);
            exitVideoFullscreen();
        }
    }, { capture: true, signal: ctx.signal });
    document.addEventListener('keyup', event =>
    {
        const expectedEscape2 = 'Escape';

        if (event.key !== expectedEscape2 || !fullscreenEscape)
        {
            return;
        }
        fullscreenEscape = false;
        event.preventDefault();
        event.stopPropagation();
    }, { capture: true, signal: ctx.signal });

    /** 物理キーの押下・解放を通信形式へ変換します。
     * @returns 呼出し元が使用する値です。
     */
    function physicalKey(event)
    {
        const key = desktopKey(event);
        if ((256) <= (key) && !scanCodesSupported)
        {
            if (!scanCodeWarning)
            {
                scanCodeWarning = true;
                ctx.say(ctx.t('Update the Core to send punctuation keys correctly.', '記号キーを正しく送信するにはCoreを更新してください。'));
            }
            return 0;
        }
        return key === 0x10 ? 0xa0 : key === 0x11 ? 0xa2 : key === 0x12 ? 0xa4 : key;
    }

    viewport.onkeydown = event =>
    {
        if (viewOnly)
        {
            return;
        }
        if (clipboard.key(event))
        {
            return;
        }
        const key = physicalKey(event);
        if (key)
        {
            event.preventDefault();
            physicalKeys.add(key);
            if (!virtualKeys.has(key))
            {
                send(4, 0, 0, 0, key, 1);
            }
        }
    };
    viewport.onkeyup = event =>
    {
        if (viewOnly)
        {
            return;
        }
        if (clipboard.key(event, false))
        {
            return;
        }
        const key = physicalKey(event);
        if (key)
        {
            event.preventDefault();
            physicalKeys.delete(key);
            if (!virtualKeys.has(key))
            {
                send(4, 0, 0, 0, key, 0);
            }
        }
    };
    keybar.addEventListener('keyup', event =>
    {
        if (physicalKeys.has(physicalKey(event)))
        {
            viewport.onkeyup(event);
        }
    }, { signal: ctx.signal });
    viewport.onblur = event =>
    {
        if (event?.relatedTarget !== softwareKeyboard.input && event?.relatedTarget !== keyboardButton && !keybar.contains(event?.relatedTarget))
        {
            clipboard.leaveArea();
            send(5);
        }
    };

    /** ユーザー操作から権限の読取りを開始し、イベントの外へ例外を残しません。 */
    function activateClipboard()
    {
        if (document.hidden || !document.hasFocus())
        {
            clipboard.leave();
            return;
        }
        clipboard.activateAsync().catch(() => ctx.say(ctx.t('Clipboard synchronization is unavailable.', 'クリップボード同期を利用できません。')));
    }

    viewport.addEventListener('pointerenter', () =>
    {
        clipboardPointerInside = true;
        activateClipboard();
    }, { signal: ctx.signal });
    viewport.addEventListener('pointerleave', () =>
    {
        clipboardPointerInside = false;
        clipboard.leaveArea();
    }, { signal: ctx.signal });
    viewport.addEventListener('focusin', activateClipboard, { signal: ctx.signal });

    /** 操作領域以外でのローカルコピーを、遅れて届いたホスト更新より優先します。 */
    function copiedOutsideDesktop(event)
    {
        if (!viewport.contains(event.target))
        {
            clipboard.copied();
        }
    }

    document.addEventListener('copy', copiedOutsideDesktop, { capture: true, signal: ctx.signal });
    document.addEventListener('cut', copiedOutsideDesktop, { capture: true, signal: ctx.signal });
    viewport.addEventListener('paste', event =>
    {
        if (!clipboard.enabled || !clipboard.available)
        {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        const text = event.clipboardData?.types.includes('text/plain') ? event.clipboardData.getData('text/plain') : null;
        clipboard.pasteAsync(text).catch(() => ctx.say(ctx.t('Clipboard synchronization is unavailable.', 'クリップボード同期を利用できません。')));
    }, { capture: true, signal: ctx.signal });
    keybar.addEventListener('pointerdown', event =>
    {
        if (softwareKeyboard.active)
        {
            event.preventDefault();
        }
    }, { signal: ctx.signal });
    keybar.addEventListener('focusout', event =>
    {
        if (!keybar.contains(event.relatedTarget) && !viewport.contains(event.relatedTarget) && event.relatedTarget !== keyboardButton)
        {
            send(5);
        }
    }, { signal: ctx.signal });
    window.addEventListener('blur', () =>
    {
        clipboard.leave();
        annotationPointer.leave();
        pointer = null;
        softwareKeyboard.hide();
        send(5);
    }, { signal: ctx.signal });
    window.addEventListener('focus', () =>
    {
        if (clipboardPointerInside || viewport.contains(document.activeElement))
        {
            activateClipboard();
        }
        cancelAnimationFrame(render);
        render = 0;
        audio?.resume().catch(error => ctx.say(error.message));
        schedule();
    }, { signal: ctx.signal });
    document.addEventListener('visibilitychange', () =>
    {
        clipboard.leave();
        annotationPointer.leave();
        hidden = !!document.hidden;
        pointer = null;
        softwareKeyboard.hide();
        send(5);
        if (hidden)
        {
            clearViews();
            audio?.reset();
        }
        else if (socket)
        {
            lastMessage = performance.now();
            audio?.resume().catch(error => ctx.say(error.message));
            choose(selected);
        }
    }, { signal: ctx.signal });
    const layout = createDesktopLayout(root, viewport, () => views, ctx.signal, () => scaleMode);
    ctx.signal.addEventListener('abort', disconnect, { once: true });
    reconnect.enable(true);
    await refreshAsync();
    return { updateControls,

        /** 所有する監視・接続・表示資源を解放します。
         * @returns 呼出し元が使用する値です。
         */
        dispose()
        {
            disposed = true;
            layout.dispose();
            return disconnectAsync();
        } };
}
