// Return physical screen coordinates only inside the displayed image, excluding letterboxing.
/** 表示上のポインター位置をリモート画面の座標へ対応付けます。
 * @returns 呼出し元が使用する値です。
 */
export function mapDesktopPoint(monitor, bounds, x, y, output)
{
    const scale = Math.min(bounds.width / monitor.width, bounds.height / monitor.height);
    if (!((0) < (scale)))
    {
        return false;
    }
    const left = bounds.left + (bounds.width - monitor.width * scale) / 2;
    const top = bounds.top + (bounds.height - monitor.height * scale) / 2;
    const column = Math.floor((x - left) / scale), row = Math.floor((y - top) / scale);
    if (column < 0 || row < 0 || (monitor.width) <= (column) || (monitor.height) <= (row))
    {
        return false;
    }
    output[0] = monitor.id;
    output[1] = column;
    output[2] = row;
    return true;
}

// Map the pointer's position in the visible viewport to the finite scroll range.
// This is absolute (not accumulated edge scrolling), so a stationary pointer
// cannot keep moving the image. CSS pixels also cover high-DPI native displays.
/** ポインターが見える範囲へ表示位置を追従させます。
 * @returns 呼出し元が使用する値です。
 */
export function followDesktopPointer(viewport, x, y, insets = null)
{
    const bounds = viewport.getBoundingClientRect();
    const width = viewport.clientWidth, height = viewport.clientHeight;
    const localX = x - bounds.left - viewport.clientLeft, localY = y - bounds.top - viewport.clientTop;
    if (width < 2 || height < 2 || localX < 0 || localY < 0 || (width) <= (localX) || (height) <= (localY))
    {
        return false;
    }
    const horizontal = Math.max(0, viewport.scrollWidth - width), vertical = Math.max(0, viewport.scrollHeight - height);
    if (!horizontal && !vertical)
    {
        return false;
    }
    const left = insets?.[0] ?? 0, top = insets?.[1] ?? 0, right = insets?.[2] ?? 0, bottom = insets?.[3] ?? 0;
    viewport.scrollLeft = horizontal * Math.max(0, Math.min(1, (localX - left) / Math.max(1, width - left - right - 1)));
    viewport.scrollTop = vertical * Math.max(0, Math.min(1, (localY - top) / Math.max(1, height - top - bottom - 1)));
    return true;
}

// Browser deltas are pixels, lines or pages, not Win32 wheel notches. Preserve
// fractional trackpad motion; Windows applies the destination's scroll setting.
/** ホイールの単位と移動量を共通の値へ変換します。
 * @returns 呼出し元が使用する値です。
 */
export function desktopWheelDelta(event)
{
    if (!Number.isFinite(event.deltaY) || event.deltaY === 0)
    {
        return 0;
    }
    const delta = Number.isFinite(event.wheelDeltaY) && event.wheelDeltaY !== 0 ? event.wheelDeltaY :
        -event.deltaY * (event.deltaMode === 1 ? 40 : event.deltaMode === 2 ? 120 : 1.2);
    return Math.max(-1200, Math.min(1200, delta));
}

const keys = { ShiftLeft: 0xa0, ShiftRight: 0xa1, ControlLeft: 0xa2, ControlRight: 0xa3, AltLeft: 0xa4, AltRight: 0xa5,
    MetaLeft: 0x5b, MetaRight: 0x5c, ContextMenu: 0x5d, Enter: 13, NumpadEnter: 13, Escape: 27, Backspace: 8, Tab: 9, Space: 32,
    ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40, Insert: 45, Delete: 46, Home: 36, End: 35, PageUp: 33, PageDown: 34,
    CapsLock: 20, NumLock: 144, ScrollLock: 145, Pause: 19, PrintScreen: 44, Convert: 28, NonConvert: 29, KanaMode: 21,
    Lang1: 21, Lang2: 25, NumpadMultiply: 106, NumpadAdd: 107, NumpadSubtract: 109, NumpadDecimal: 110, NumpadDivide: 111,
    AudioVolumeMute: 0xad, AudioVolumeDown: 0xae, AudioVolumeUp: 0xaf, MediaTrackNext: 0xb0, MediaTrackPrevious: 0xb1, MediaStop: 0xb2, MediaPlayPause: 0xb3 };

// DOM keyCode is not a Windows VK: Firefox Minus=173 would become VK_VOLUME_MUTE.
// Send physical Set-1 scan codes for layout-dependent punctuation. The receiver's
// JIS/US layout interprets them, and keyup is independent of Shift/IME changes.
// 0x100 marks a scan code in the existing key field; negotiate scanCodeInput first.
const punctuation = { Minus: 0x0c, Equal: 0x0d, BracketLeft: 0x1a, BracketRight: 0x1b, Backslash: 0x2b,
    Semicolon: 0x27, Quote: 0x28, Backquote: 0x29, Comma: 0x33, Period: 0x34, Slash: 0x35,
    IntlBackslash: 0x56, IntlRo: 0x73, IntlYen: 0x7d };

/** ブラウザーのキーをリモート入力用の値へ変換します。
 * @returns 呼出し元が使用する値です。
 */
export function desktopKey(event)
{
    const code = event.code || '';
    if (Object.hasOwn(punctuation, code))
    {
        return 0x100 | punctuation[code];
    }
    if (/^Key[A-Z]$/.test(code))
    {
        return code.charCodeAt(3);
    }
    if (/^Digit[0-9]$/.test(code))
    {
        return code.charCodeAt(5);
    }
    if (/^Numpad[0-9]$/.test(code))
    {
        return 96 + Number(code[6]);
    }
    if (/^F([1-9]|1[0-9]|2[0-4])$/.test(code))
    {
        return 111 + Number(code.slice(1));
    }
    if (keys[code])
    {
        return keys[code];
    }
    const legacy = event.keyCode;

    // Ambiguous printable/media/modifier values must never trigger volume or
    // a stuck modifier merely because a browser uses a different legacy table.
    return (0) < (legacy) && legacy < 256 && legacy !== 229 && !((160) <= (legacy) && legacy <= 183) ? legacy : 0;
}
