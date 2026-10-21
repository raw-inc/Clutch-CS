import test from 'node:test';
import assert from 'node:assert/strict';
import { DomNode, createDocument, descendants } from './host-manager-dom.mjs';
import { powerSchedulesView } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/power-schedules.js';

/** OSや管理通信を使わず、画面の編集と保存要求を観測します。
 * @returns 操作可能なDOMと記録された要求です。
 */
async function fixtureAsync(t, outcome = 'succeeded', views = null)
{
    const original = globalThis.document, originalNode = globalThis.Node;
    globalThis.document = createDocument();
    globalThis.Node = DomNode;
    t.after(() =>
    {
        globalThis.document = original;
        globalThis.Node = originalNode;
    });
    const root = new DomNode('div'), tasks = [], calls = [], messages = [], buttons = new Map(), dialogs = [];
    const savedViews = views ?? [{ clusterId: 'alpha', hostId: 'offline-host', revision: 'original', active: true, wakeProfileAvailable: true, schedules: [], status: [] }];
    const ctx = { t: value => value,

        signal: new AbortController().signal,

        hosts: () => savedViews.map(view => ({ target: { hostId: view.hostId, clusterId: view.clusterId }, capabilities: null })),

        dialog: () =>
        {
            const element = new DomNode('dialog'), body = new DomNode('div'), footer = new DomNode('div');
            element.append(body, footer);
            const dialog = { root: body, footer, element,

                close: () => element.close() };
            dialogs.push(dialog);
            element.showModal();
            return dialog;
        },

        guard: run =>
        {
            const pending = Promise.resolve().then(run);
            tasks.push(pending);
            return pending;
        },

        discard: run => run(),

        say: message => messages.push(message),

        button: (label, run) =>
        {
            const button = new DomNode('button', label);
            button.onclick = run;
            buttons.set(label, button);
            return button;
        },

        api: async (path, request) =>
        {
            calls.push({ path, request });
            const read = 'power-schedules/read', save = 'power-schedules/save';
            if (path === read)
            {
                return structuredClone({ ...savedViews.find(view => view.hostId === request.hostId), clusterId: request.clusterId });
            }
            if (path === save)
            {
                const view = outcome === 'succeeded' ? { ...request, revision: 'saved-' + request.hostId, active: true, wakeProfileAvailable: true, status: [] } : null;
                if (view)
                {
                    savedViews.splice(savedViews.findIndex(item => item.hostId === request.hostId), 1, view);
                }
                return { outcome, view };
            }
            return [...new Set(['alpha', 'beta', ...savedViews.map(view => view.clusterId)])];
        } };
    const view = powerSchedulesView(root, ctx);
    t.after(() => view.dispose());
    await Promise.all(tasks);
    return { root, view, buttons, calls, messages, ctx, dialogs, savedViews };
}

test('offline host schedules stay local until saved with explicit cluster and revision', async t =>
{
    const state = await fixtureAsync(t);
    await state.buttons.get('powerSchedule.add').onclick();
    assert.equal(state.view.hasChanges(), true);
    assert.equal(state.calls.some(call => call.path.endsWith('/save')), false);
    const fields = descendants(state.root).filter(item => item.tag === 'input');
    const zone = fields.find(item => item.getAttribute('aria-label') === 'powerSchedule.zone');
    const time = fields.find(item => item.getAttribute('aria-label') === 'powerSchedule.time');
    assert.equal(zone.value, 'UTC');
    zone.value = 'Asia/Tokyo';
    time.value = '08:30:00';
    await state.buttons.get('powerSchedule.save').onclick();
    const saved = state.calls.find(call => call.path.endsWith('/save')).request;
    assert.equal(saved.hostId, 'offline-host');
    assert.equal(saved.clusterId, 'alpha');
    assert.equal(saved.expectedRevision, 'original');
    assert.deepEqual(saved.schedules[0], { id: saved.schedules[0].id, action: 'wake', time: '08:30:00', timeZoneId: 'Asia/Tokyo', weekdays: [], enabled: true });
    assert.equal(state.view.hasChanges(), false);
});

test('weekday mode requires at least one day before saving', async t =>
{
    const state = await fixtureAsync(t);
    await state.buttons.get('powerSchedule.add').onclick();
    const repeat = descendants(state.root).find(item => item.getAttribute('aria-label') === 'Repeat');
    repeat.value = 'weekdays';
    repeat.onchange();
    await assert.rejects(state.buttons.get('powerSchedule.save').onclick(), /powerSchedule.invalid/u);
    assert.equal(state.calls.some(call => call.path.endsWith('/save')), false);
    const friday = descendants(state.root).find(item => item.tag === 'label' && item.textContent === 'powerSchedule.day5').children[0];
    friday.checked = true;
    await state.buttons.get('powerSchedule.save').onclick();
    assert.deepEqual(state.calls.find(call => call.path.endsWith('/save')).request.schedules[0].weekdays, [5]);
});

test('unconfirmed save locks another submission until explicit refresh', async t =>
{
    const state = await fixtureAsync(t, 'unknown');
    await state.buttons.get('powerSchedule.add').onclick();
    await state.buttons.get('powerSchedule.save').onclick();
    assert.equal(state.buttons.get('powerSchedule.save').disabled, true);
    await state.buttons.get('powerSchedule.save').onclick();
    assert.equal(state.calls.filter(call => call.path.endsWith('/save')).length, 1);
    await state.buttons.get('Refresh settings').onclick();
    assert.equal(state.buttons.get('powerSchedule.save').disabled, false);
    assert.equal(state.view.hasChanges(), false);
});

test('cancelled cluster change retains the original draft and cluster selection', async t =>
{
    const state = await fixtureAsync(t);
    await state.buttons.get('powerSchedule.add').onclick();
    state.ctx.discard = () => false;
    const cluster = descendants(state.root).find(item => item.getAttribute('aria-label') === 'powerSchedule.cluster');
    cluster.value = 'beta';
    await cluster.onchange();
    assert.equal(cluster.value, 'alpha');
    assert.equal(state.view.hasChanges(), true);
    assert.equal(state.calls.filter(call => call.path.endsWith('/read')).length, 1);
});

/** 異なるクラスタ・曜日・操作を持つ複数Hostの保存値を準備します。
 * @returns 一括操作で維持すべき各Hostの予定です。
 */
function hostViews()
{
    return ['alpha', 'beta', 'gamma'].map((clusterId, index) => ({ clusterId, hostId: 'host-' + index, revision: 'revision-' + index,
        active: true, wakeProfileAvailable: true, status: [], schedules: [{ id: 'schedule-' + index, action: index === 1 ? 'wake' : 'shutdown',
            time: '08:0' + index, timeZoneId: 'UTC', weekdays: [index], enabled: index !== 1 }] }));
}

/** 同じ表示名の操作を対象領域内から取り出します。
 * @returns 試験で操作するボタンです。
 */
function buttonsIn(root, name)
{
    return descendants(root).filter(item => item.tag === 'button' && item.textContent === name);
}

test('bulk editor replaces every selected host with one list while preserving target cluster and revision', async t =>
{
    const initial = hostViews(), state = await fixtureAsync(t, 'succeeded', structuredClone(initial));
    await state.buttons.get('powerSchedule.bulk').onclick();
    const dialog = state.dialogs.at(-1), time = descendants(dialog.root).find(item => item.getAttribute('aria-label') === 'powerSchedule.time');
    time.value = '23:15:00';
    const zone = descendants(dialog.root).find(item => item.getAttribute('aria-label') === 'powerSchedule.zone');
    zone.value = 'Asia/Tokyo';
    assert.equal(state.calls.some(call => call.path.endsWith('/save')), false);
    assert.match(dialog.root.textContent, /host-0.*alpha.*host-1.*beta.*host-2.*gamma/u);
    await state.buttons.get('powerSchedule.bulkApply').onclick();
    const requests = state.calls.filter(call => call.path.endsWith('/save')).map(call => call.request);
    assert.equal(requests.length, 3);
    assert.equal(new Set(requests.map(item => item.operationId)).size, 3);
    for (let index = 0; index < requests.length; index++)
    {
        assert.equal(requests[index].hostId, initial[index].hostId);
        assert.equal(requests[index].clusterId, initial[index].clusterId);
        assert.equal(requests[index].expectedRevision, initial[index].revision);
        assert.deepEqual(requests[index].schedules, [{ ...initial[0].schedules[0], time: '23:15:00', timeZoneId: 'Asia/Tokyo' }]);
    }
    assert.equal(state.view.hasChanges(), false);
    assert.equal((dialog.root.textContent.match(/powerSchedule.succeeded/gu) ?? []).length, 3);
    await state.buttons.get('powerSchedule.bulkApply').onclick();
    assert.equal(state.calls.filter(call => call.path.endsWith('/save')).length, 3);
});

test('bulk enable and disable preserve each hosts schedule fields and skip empty or unchanged hosts', async t =>
{
    const initial = hostViews();
    initial[2].schedules = [];
    const state = await fixtureAsync(t, 'succeeded', structuredClone(initial));
    await state.buttons.get('powerSchedule.bulkDisable').onclick();
    await state.buttons.get('powerSchedule.bulkApply').onclick();
    let requests = state.calls.filter(call => call.path.endsWith('/save')).map(call => call.request);
    assert.equal(requests.length, 1);
    assert.deepEqual(requests[0].schedules, initial[0].schedules.map(item => ({ ...item, enabled: false })));
    state.dialogs.at(-1).close();
    await state.buttons.get('powerSchedule.bulkEnable').onclick();
    await state.buttons.get('powerSchedule.bulkApply').onclick();
    requests = state.calls.filter(call => call.path.endsWith('/save')).map(call => call.request);
    assert.equal(requests.length, 3);
    assert.deepEqual(requests[1].schedules, initial[0].schedules);
    assert.deepEqual(requests[2].schedules, initial[1].schedules.map(item => ({ ...item, enabled: true })));
    assert.equal(requests.some(item => item.hostId === 'host-2'), false);
    assert.equal(requests[1].expectedRevision, 'saved-host-0');
});

test('template can copy a different host and validate weekdays before any save', async t =>
{
    const initial = hostViews(), state = await fixtureAsync(t, 'succeeded', initial);
    await state.buttons.get('powerSchedule.bulk').onclick();
    const dialog = state.dialogs.at(-1), source = descendants(dialog.root).find(item => item.getAttribute('aria-label') === 'powerSchedule.bulkSource');
    source.value = 'host-1';
    await state.buttons.get('powerSchedule.bulkCopy').onclick();
    const day = descendants(dialog.root).find(item => item.tag === 'label' && item.textContent === 'powerSchedule.day1').children[0];
    day.checked = false;
    await assert.rejects(state.buttons.get('powerSchedule.bulkApply').onclick(), /powerSchedule.invalid/u);
    assert.equal(state.calls.some(call => call.path.endsWith('/save')), false);
    day.checked = true;
    await state.buttons.get('powerSchedule.bulkApply').onclick();
    const requests = state.calls.filter(call => call.path.endsWith('/save')).map(call => call.request);
    assert.deepEqual(requests.map(item => item.hostId), ['host-0', 'host-2']);
    assert.deepEqual(requests[0].schedules, initial[1].schedules);
});

for (const failure of ['unknown', 'notExecuted', 'exception', 'missingView'])
{
    test(`bulk ${failure} on one host is reported without resending or blocking other hosts`, async t =>
    {
        const state = await fixtureAsync(t, 'succeeded', hostViews()), api = state.ctx.api, sent = [];
        state.ctx.api = async (path, request, signal) =>
        {
            if (path.endsWith('/save'))
            {
                sent.push(request);
                if (request.hostId === 'host-1')
                {
                    if (failure === 'exception')
                    {
                        throw new Error('network unavailable');
                    }
                    return { outcome: failure === 'missingView' ? 'succeeded' : failure, reason: 'powerSchedule.saveUnconfirmed' };
                }
            }
            return api(path, request, signal);
        };
        await state.buttons.get('powerSchedule.bulk').onclick();
        await state.buttons.get('powerSchedule.add').onclick();
        await state.buttons.get('powerSchedule.bulkApply').onclick();
        assert.deepEqual(sent.map(item => item.hostId), ['host-0', 'host-1', 'host-2']);
        assert.match(state.dialogs.at(-1).root.textContent, /powerSchedule.succeeded.*powerSchedule.saveUnconfirmed.*powerSchedule.succeeded/u);
        await state.buttons.get('powerSchedule.bulkApply').onclick();
        assert.equal(sent.length, 3);
        state.dialogs.at(-1).close();
        assert.throws(() => state.buttons.get('powerSchedule.bulk').onclick(), /powerSchedule.bulkUnavailable/u);
        assert.equal(buttonsIn(state.root, 'powerSchedule.save')[1].disabled, true);
        await buttonsIn(state.root, 'Refresh settings')[1].onclick();
        await state.buttons.get('powerSchedule.bulk').onclick();
        assert.equal(state.dialogs.at(-1).element.open, true);
    });
}

test('individual drafts and stale confirmation never silently overwrite host settings', async t =>
{
    const state = await fixtureAsync(t, 'succeeded', hostViews());
    await buttonsIn(state.root, 'powerSchedule.add')[0].onclick();
    assert.throws(() => state.buttons.get('powerSchedule.bulkDisable').onclick(), /powerSchedule.bulkDrafts/u);
    await buttonsIn(state.root, 'Refresh settings')[0].onclick();
    await state.buttons.get('powerSchedule.bulkDisable').onclick();
    state.savedViews[0].revision = 'external-save';
    await buttonsIn(state.root, 'Refresh settings')[0].onclick();
    await assert.rejects(state.buttons.get('powerSchedule.bulkApply').onclick(), /powerSchedule.bulkChanged/u);
    assert.equal(state.calls.some(call => call.path.endsWith('/save')), false);
});

test('bulk submission locks individual controls and stopping the view leaves remaining hosts unsent', async t =>
{
    const state = await fixtureAsync(t, 'succeeded', hostViews()), api = state.ctx.api;
    let release;
    const waiting = new Promise(resolve =>
    {
        release = resolve;
    });
    state.ctx.api = async (path, request, signal) =>
    {
        if (path.endsWith('/save'))
        {
            await waiting;
        }
        return api(path, request, signal);
    };
    await state.buttons.get('powerSchedule.bulk').onclick();
    await state.buttons.get('powerSchedule.add').onclick();
    const running = state.buttons.get('powerSchedule.bulkApply').onclick();
    assert.equal(state.view.hasChanges(), true);
    assert.equal(buttonsIn(state.root, 'powerSchedule.save').every(item => item.disabled), true);
    assert.equal(buttonsIn(state.root, 'Refresh settings').every(item => item.disabled), true);
    assert.throws(() => state.buttons.get('powerSchedule.bulk').onclick(), /powerSchedule.bulkUnavailable/u);
    state.view.dispose();
    release();
    await running;
    assert.equal(state.calls.filter(call => call.path.endsWith('/save')).length, 1);
    assert.equal(state.dialogs.at(-1).element.open, false);
});
