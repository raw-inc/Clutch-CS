import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mapDesktopPoint, desktopKey, followDesktopPointer, desktopWheelDelta } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-input.js';

test('wheel units retain fine motion and do not turn horizontal or zero events into vertical notches', () =>
{
    assert.equal(desktopWheelDelta({ deltaY: 3, deltaMode: 1 }), -120);
    assert.equal(desktopWheelDelta({ deltaY: 100, deltaMode: 0 }), -120);
    assert.equal(desktopWheelDelta({ deltaY: 1, deltaMode: 0 }), -1.2);
    assert.equal(desktopWheelDelta({ deltaY: 0, deltaX: 100, deltaMode: 0 }), 0);
    assert.equal(desktopWheelDelta({ deltaY: NaN }), 0);
    assert.equal(desktopWheelDelta({ deltaY: 1, deltaMode: 2 }), -120);
    assert.equal(desktopWheelDelta({ deltaY: 600, deltaMode: 0, wheelDeltaY: -120 }), -120);
    assert.equal(desktopWheelDelta({ deltaY: -10000, deltaMode: 1 }), 1200);
});

test('letterboxing never changes physical pointer coordinates or accepts padding clicks', () =>
{
    const output = new Int32Array(3), monitor = { id: 2, width: 1920, height: 1080 };
    const bounds = { left: 10, top: 20, width: 1000, height: 400 };
    assert.equal(mapDesktopPoint(monitor, bounds, 510, 220, output), true);
    assert.deepEqual(Array.from(output), [2, 960, 540]);
    assert.equal(mapDesktopPoint(monitor, bounds, 20, 220, output), false);
    assert.equal(mapDesktopPoint(monitor, bounds, 510, 430, output), false);
});

test('physical letter keys survive local IME and left/right modifiers stay distinct', () =>
{
    assert.equal(desktopKey({ code: 'KeyA', keyCode: 229 }), 65);
    assert.equal(desktopKey({ code: 'ControlLeft', keyCode: 17 }), 0xa2);
    assert.equal(desktopKey({ code: 'ControlRight', keyCode: 17 }), 0xa3);
    assert.equal(desktopKey({ code: 'Numpad7', keyCode: 103 }), 103);
    assert.equal(desktopKey({ code: 'F24', keyCode: 135 }), 135);
    assert.equal(desktopKey({ code: 'Unidentified', keyCode: 229 }), 0);
});

test('pointer follow maps the visible area to bounded scrolling without accumulating motion', () =>
{
    const viewport = { clientWidth: 801, clientHeight: 601, clientLeft: 1, clientTop: 1, scrollWidth: 1601, scrollHeight: 1201, scrollLeft: 0, scrollTop: 0,

        getBoundingClientRect: () => ({ left: 10, top: 20 }) };
    assert.equal(followDesktopPointer(viewport, 411, 321), true);
    assert.equal(viewport.scrollLeft, 400);
    assert.equal(viewport.scrollTop, 300);
    for (let i = 0;i < 20;i++)
    {
        followDesktopPointer(viewport, 411, 321);
    }
    assert.equal(viewport.scrollLeft, 400);
    assert.equal(viewport.scrollTop, 300);
    followDesktopPointer(viewport, 811.5, 621.5);
    assert.equal(viewport.scrollLeft, 800);
    assert.equal(viewport.scrollTop, 600);
    assert.equal(followDesktopPointer(viewport, 813, 621), false);
    assert.equal(viewport.scrollLeft, 800);
    followDesktopPointer(viewport, 11, 21);
    assert.equal(viewport.scrollLeft, 0);
    assert.equal(viewport.scrollTop, 0);
    viewport.scrollWidth = 801;
    viewport.scrollHeight = 601;
    assert.equal(followDesktopPointer(viewport, 411, 321), false);
});

test('labels, padding and high-DPI scaling do not prevent reaching the image edges', () =>
{
    const viewport = { clientWidth: 804, clientHeight: 628, clientLeft: 0, clientTop: 0, scrollWidth: 1924, scrollHeight: 1108, scrollLeft: 0, scrollTop: 0,

        getBoundingClientRect: () => ({ left: 10, top: 20 }) };
    const insets = new Float64Array([2, 26, 2, 2]), monitor = { id: 0, width: 3840, height: 2160 }, output = new Int32Array(3);

    /** 試験対象の表示範囲を構成します。
     * @returns left・top・width・heightを含む結果です。
     */
    const bounds = () => ({ left: 12 - viewport.scrollLeft, top: 46 - viewport.scrollTop, width: 1920, height: 1080 });

    followDesktopPointer(viewport, 12, 46, insets);
    mapDesktopPoint(monitor, bounds(), 12, 46, output);
    assert.deepEqual(Array.from(output), [0, 0, 0]);
    followDesktopPointer(viewport, 811.5, 645.5, insets);
    mapDesktopPoint(monitor, bounds(), 811.5, 645.5, output);
    assert.deepEqual(Array.from(output), [0, 3839, 2159]);
});

test('a selected secondary display maps independently of primary size, origin, scale and rotation', () =>
{
    for (const monitor of [{ id: 1, left: 3840, top: 0, width: 1920, height: 1080 }, { id: 1, left: -2560, top: -300, width: 2560, height: 1440 }, { id: 2, left: 1920, top: 1080, width: 1080, height: 1920 }, { id: 1, left: 1920, top: 0, width: 1920, height: 1080 }])
    {
        for (const density of [1, 1.25, 1.5, 2])
        {
            const width = monitor.width / density, height = monitor.height / density, bounds = { left: 37, top: 83, width, height }, output = new Int32Array(3);
            for (const [x, y] of [[0, 0], [monitor.width / 2, monitor.height / 2], [monitor.width - 1, monitor.height - 1]])
            {
                assert.equal(mapDesktopPoint(monitor, bounds, bounds.left + (x + .5) / density, bounds.top + (y + .5) / density, output), true);
                assert.deepEqual(Array.from(output), [monitor.id, x, y]);
            }
        }
    }
});

test('JIS and US punctuation use the same physical positions across Firefox and Chrome', () =>
{
    for (const [code, scan, firefox, chrome] of [['Minus', 0x0c, 173, 189], ['Equal', 0x0d, 160, 222], ['Equal', 0x0d, 61, 187], ['Semicolon', 0x27, 59, 187], ['Semicolon', 0x27, 59, 186], ['Quote', 0x28, 58, 186], ['BracketLeft', 0x1a, 64, 192], ['BracketRight', 0x1b, 219, 219], ['Backslash', 0x2b, 221, 221], ['IntlYen', 0x7d, 220, 220], ['IntlRo', 0x73, 167, 226]])
    {
        for (const keyCode of [firefox, chrome])
        {
            for (const shiftKey of [true, false])
            {
                assert.equal(desktopKey({ code, keyCode, shiftKey }), 0x100 | scan);
            }
        }
    }
    assert.equal(desktopKey({ code: 'Unidentified', keyCode: 173 }), 0);
    assert.equal(desktopKey({ code: 'Unidentified', keyCode: 160 }), 0);
    assert.equal(desktopKey({ code: 'AudioVolumeMute', keyCode: 173 }), 173);
});
