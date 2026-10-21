import test from 'node:test';
import assert from 'node:assert/strict';
import { createDesktopReconnect, parseDesktopReconnectDelays, desktopDisconnectReason } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-reconnect.js';

test('reconnect delays accept bounded integer lists and reject ambiguous or unlimited schedules', () =>
{
    assert.deepEqual(parseDesktopReconnectDelays('0, 3000, 10000'), [0, 3000, 10000]);
    assert.deepEqual(parseDesktopReconnectDelays(' 0 , 20 , 300000 '), [0, 20, 300000]);
    for (const value of ['', ' ', '0,', '-1', '1.5', 'Infinity', '0x10', '1e3', '300001', Array(21).fill('0').join(',')])
    {
        assert.throws(() => parseDesktopReconnectDelays(value));
    }
});

test('recovery waits from each failure, exhausts three attempts and resets only on confirmed success', async t =>
{
    t.mock.timers.enable({ apis: ['setTimeout'] });
    let attempts = 0, current;
    const reconnect = createDesktopReconnect(async () =>
    {
        attempts++;
    }, value =>
    {
        current = value;
    }, assert.fail);
    assert.equal(reconnect.failed(), false);
    reconnect.enable(true);
    assert.equal(reconnect.failed(), true);
    t.mock.timers.tick(0);
    await Promise.resolve();
    assert.equal(attempts, 1);
    t.mock.timers.tick(9000);
    assert.equal(attempts, 1);
    reconnect.failed();
    t.mock.timers.tick(2999);
    assert.equal(attempts, 1);
    t.mock.timers.tick(1);
    await Promise.resolve();
    assert.equal(attempts, 2);
    reconnect.failed();
    t.mock.timers.tick(9999);
    assert.equal(attempts, 2);
    t.mock.timers.tick(1);
    await Promise.resolve();
    assert.equal(attempts, 3);
    assert.equal(reconnect.failed(), false);
    assert.equal(current.pending, false);
    t.mock.timers.tick(60000);
    assert.equal(attempts, 3);
    reconnect.connected();
    assert.equal(current.attempt, 0);
    reconnect.failed();
    t.mock.timers.tick(0);
    await Promise.resolve();
    assert.equal(attempts, 4);
});

test('turning off, manual cancellation and leaving the view cancel pending recovery', async t =>
{
    t.mock.timers.enable({ apis: ['setTimeout'] });
    let attempts = 0;
    const reconnect = createDesktopReconnect(async () =>
    {
        attempts++;
    }, () =>
    {}, assert.fail);
    reconnect.enable(true);
    reconnect.configure('3000, 5000');
    reconnect.failed();
    reconnect.cancel();
    t.mock.timers.tick(10000);
    assert.equal(attempts, 0);
    reconnect.failed();
    reconnect.enable(false);
    t.mock.timers.tick(10000);
    assert.equal(attempts, 0);
    reconnect.enable(true);
    reconnect.failed();
    t.mock.timers.tick(3000);
    await Promise.resolve();
    assert.equal(attempts, 1);
});

test('duplicate failures cannot queue overlapping attempts and cancelled startup errors stay stale', async t =>
{
    t.mock.timers.enable({ apis: ['setTimeout'] });
    let reject, errors = 0, attempts = 0;
    const reconnect = createDesktopReconnect(() =>
    {
        attempts++;
        return new Promise((resolve, fail) =>
        {
            reject = fail;
        });
    }, () =>
    {}, () =>
    {
        errors++;
    });
    reconnect.enable(true);
    reconnect.failed();
    reconnect.failed();
    t.mock.timers.tick(0);
    assert.equal(attempts, 1);
    reconnect.cancel();
    reject(new Error('cancelled startup'));
    await Promise.resolve();
    assert.equal(errors, 0);
});

test('abnormal network closure does not claim a login-user problem and permanent failures do not retry', () =>
{
    const network = desktopDisconnectReason({ code: 1006, reason: '' });
    assert.equal(network.retryable, true);
    assert.match(network.message[0], /without a close notification/u);
    assert.doesNotMatch(network.message.join(' '), /login|ログイン/u);
    assert.match(desktopDisconnectReason({ code: 1011, reason: 'Desktop.UpstreamDisconnected' }).message[0], /between ClientHost and Core/u);
    for (const code of [1000, 1002, 1003, 1007, 1008, 1009])
    {
        assert.equal(desktopDisconnectReason({ code, reason: '' }).retryable, false);
    }
});
