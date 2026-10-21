import test from 'node:test';
import assert from 'node:assert/strict';
import { installationPowerChange, powerMinutes, powerStatus, powerTime } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/power-model.js';
import { previewValues } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/operations.js';

/** 自動タイマーを無効にした確認用の状態を作ります。
 * @returns ボタン動作を含むOS設定です。
 */
function state()
{
    const values = { displaySeconds: 0, sleepSeconds: 0, hibernateSeconds: 0, unattendedSleepSeconds: 0, powerButtonAction: 3 };
    return { ac: { ...values }, dc: { ...values }, fastStartup: true, idleShutdown: { acSeconds: 0, dcSeconds: 0 }, restrictions: [] };
}

test('power time treats blank as unchanged and rejects negative, fractional and unlimited minutes', () =>
{
    assert.equal(powerMinutes(''), undefined);
    assert.equal(powerMinutes('0'), 0);
    assert.equal(powerMinutes('60'), 3600);
    for (const invalid of ['-1', '1.1', '10081', 'Infinity', 'words'])
    {
        assert.throws(() => powerMinutes(invalid), /Device.InvalidPowerSetting/u);
    }
    assert.equal(powerTime(null), 'Unconfirmed');
    assert.equal(powerTime(0), 'Disabled');
});

test('installation preset keeps button actions and unsupported fields unchanged', () =>
{
    const current = state();
    current.dc.hibernateSeconds = null;
    const change = installationPowerChange(current);
    assert.equal(change.ac.displaySeconds, 0);
    assert.equal(change.idleShutdownAcSeconds, 0);
    assert.equal(change.fastStartup, false);
    assert.equal(change.ac.powerButtonAction, undefined);
    assert.equal(change.dc.hibernateSeconds, undefined);
});

test('power overview includes unattended wake and does not call unknown settings disabled', () =>
{
    const current = state();
    assert.equal(powerStatus(current), 'All managed automatic timers are disabled');
    current.ac.unattendedSleepSeconds = 120;
    assert.equal(powerStatus(current), 'Automatic power timers are enabled');
    current.dc.sleepSeconds = null;
    assert.equal(powerStatus(current), 'Automatic power settings unconfirmed');
});

test('power preview compares only explicitly changed AC and host-wide settings', () =>
{
    const [before, after] = previewValues({ action: 'power', before: { data: state() }, change: { ac: { displaySeconds: 60 }, fastStartup: false } });
    assert.match(before, /AC power.*Disabled/u);
    assert.match(after, /1 minutes/u);
    assert.match(after, /Fast Startup: Disabled/u);
    assert.doesNotMatch(after, /Battery|Power button/u);
});
