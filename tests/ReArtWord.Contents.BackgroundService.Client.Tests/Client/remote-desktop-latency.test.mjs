import test from 'node:test';
import assert from 'node:assert/strict';
import { createDesktopLatency } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-latency.js';

test('delivery time includes full-frame transfer and corrects large clock offsets and server processing', () =>
{
    const clock = createDesktopLatency();
    assert.equal(clock.probe(1000), null);
    assert.equal(clock.value([0], 1000), null);
    clock.enable(true);
    const [sequence] = clock.probe(1000);
    clock.reply(sequence, 1000000, 11020000, 11025000, 1045);
    clock.mark(0, 42, 11100000);
    clock.frame(0, 42, 1137);
    assert.equal(clock.value([0], 1137), 37);
    clock.mark(1, 43, 11200000);
    clock.frame(1, 43, 1241);
    assert.equal(clock.value([0, 1], 1241), 41);
    assert.equal(clock.value([2], 1241), null);
});

test('the least delayed recent round trip calibrates the clock and old calibrations expire', () =>
{
    const clock = createDesktopLatency();
    clock.enable(true);
    const [first] = clock.probe(1000);
    clock.reply(first, 1000000, 11020000, 11025000, 1045);
    const [second] = clock.probe(2000);
    clock.reply(second, 2000000, 12080000, 12085000, 2105);
    clock.mark(0, 1, 12200000);
    clock.frame(0, 1, 2210);
    assert.equal(clock.value([0], 2210), 10);
    assert.equal(clock.value([0], 63000), null);
});

test('unsolicited, corrupted and old-session replies never become zero-latency measurements', () =>
{
    const clock = createDesktopLatency();
    clock.enable(true);
    const [sequence] = clock.probe(1000);
    clock.reply(sequence + 1, 1000000, 11020000, 11025000, 1045);
    clock.mark(0, 1, 11100000);
    clock.frame(0, 1, 1137);
    assert.equal(clock.value([0], 1137), null);
    clock.reset();
    clock.enable(true);
    const [fresh] = clock.probe(1000);
    assert.notEqual(fresh, sequence);
    clock.reply(sequence, 1000000, 11020000, 11025000, 1045);
    clock.reply(fresh, 1000000, 11025000, 11020000, 1045);
    clock.mark(0, 1, 11100000);
    clock.frame(0, 1, 1137);
    assert.equal(clock.value([0], 1137), null);
});

test('measurement requires the matching frame marker and does not retain missing or mismatched marks', () =>
{
    const clock = createDesktopLatency();
    clock.enable(true);
    const [sequence] = clock.probe(1000);
    clock.reply(sequence, 1000000, 11020000, 11025000, 1045);
    clock.mark(0, 42, 11100000);
    clock.frame(0, 43, 1137);
    assert.equal(clock.value([0], 1137), null);
    clock.frame(0, 42, 1138);
    assert.equal(clock.value([0], 1138), null);
    clock.mark(0, 44, 12000000);
    clock.frame(0, 44, 1139);
    assert.equal(clock.value([0], 1139), null);
});

test('unanswered probes are bounded and can recover without reconnecting', () =>
{
    const clock = createDesktopLatency();
    clock.enable(true);
    const [sequence] = clock.probe(1000);
    assert.equal(clock.probe(2000), null);
    assert.equal(clock.probe(5999), null);
    const [next] = clock.probe(6000);
    assert.notEqual(next, sequence);
    clock.reply(next, 6000000, 16020000, 16025000, 6045);
    clock.mark(0, 1, 16100000);
    clock.frame(0, 1, 6110);
    assert.equal(clock.value([0], 6110), 10);
});
