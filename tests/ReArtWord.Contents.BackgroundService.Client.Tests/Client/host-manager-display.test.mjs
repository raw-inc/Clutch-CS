import test from 'node:test';
import assert from 'node:assert/strict';
import { displayChange, displayModeLabel } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/display.js';
import { previewValues } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/operations.js';

test('Display changes retain the exact advertised mode and do not mutate the inventory', () =>
{
    const current = { width: 1920, height: 1080, refreshRate: 60, bitsPerPixel: 32, flags: 0 };
    const wanted = { width: 2560, height: 1440, refreshRate: 120, bitsPerPixel: 32, flags: 0 };
    const display = { current, modes: [current, wanted] };
    const change = displayChange(display, 1);
    assert.deepEqual(change, { mode: wanted });
    assert.notEqual(change.mode, wanted);
    change.mode.width = 1;
    assert.equal(wanted.width, 2560);
    assert.throws(() => displayChange(display, Number.NaN), /UnsupportedDisplayMode/u);
    assert.throws(() => displayChange(display, -1), /UnsupportedDisplayMode/u);
    assert.throws(() => displayChange(display, 3), /UnsupportedDisplayMode/u);
});

test('Display confirmation describes the previous and selected modes without internal identifiers', () =>
{
    const before = { width: 1920, height: 1080, refreshRate: 60, bitsPerPixel: 32, flags: 0 };
    const after = { ...before, refreshRate: 50, flags: 2 };
    const values = previewValues({ action: 'display', before: { data: { id: 'internal-device-id', current: before, modes: [before, after] } }, change: { mode: after } });
    assert.deepEqual(values, [displayModeLabel(before), displayModeLabel(after)]);
    assert(values[1].includes('Interlaced'));
    assert(values.every(value => !value.includes('internal-device-id')));
});
