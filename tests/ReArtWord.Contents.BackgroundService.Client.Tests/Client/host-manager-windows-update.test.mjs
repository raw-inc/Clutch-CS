import test from 'node:test';
import assert from 'node:assert/strict';
import { windowsUpdateStatus } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/windows-update.js';
import { previewValues } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/operations.js';

/** 手動更新設定の確認用データを構成します。
 * @returns 期限の上書きがない状態です。
 */
function state()
{
    return { supported: true, noAutoUpdate: 1, deadlines: { SetComplianceDeadlineForQU: 0 }, restrictions: [] };
}

test('manual update status requires confirmed support and no overriding deadlines', () =>
{
    const current = state();
    assert.equal(windowsUpdateStatus(current), 'Manual updates only');
    assert.equal(windowsUpdateStatus({ ...current, deadlines: { quality: 1 } }), 'Update deadlines can override manual mode');
    assert.equal(windowsUpdateStatus({ ...current, restrictions: ['MDM'] }), 'Update policy is managed or unconfirmed');
    assert.equal(windowsUpdateStatus({ ...current, supported: false }), 'Device.UpdateEditionUnsupported');
});

test('update preview explains disabling local deadlines and preserves them on release', () =>
{
    const item = { action: 'windowsUpdate', before: { data: state() }, change: { manualOnly: true } };
    const [before, after] = previewValues(item);
    assert.match(before, /SetComplianceDeadlineForQU: Disabled/u);
    assert.match(after, /Local update and restart deadlines will be disabled/u);
    item.change.manualOnly = false;
    assert.match(previewValues(item)[1], /Disabled deadline policies will stay disabled/u);
});
