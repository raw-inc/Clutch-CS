import { node, select, field, heading, action, badge } from './dom.js';
import { noSelection } from './loader.js';
import { powerScheduleForm } from './power-schedule-form.js';
import { powerScheduleBulk } from './power-schedules-bulk.js';

export const powerScheduleKeys = ['powerSchedule.title', 'powerSchedule.help', 'powerSchedule.cluster', 'powerSchedule.add', 'powerSchedule.save',
    'powerSchedule.wake', 'powerSchedule.shutdown', 'powerSchedule.time', 'powerSchedule.zone', 'powerSchedule.daily', 'powerSchedule.weekdays',
    'powerSchedule.enabled', 'powerSchedule.remove', 'powerSchedule.next', 'powerSchedule.last', 'powerSchedule.active', 'powerSchedule.inactive',
    'powerSchedule.wakeAvailable', 'powerSchedule.wakeMissing', 'powerSchedule.saved', 'powerSchedule.invalid', 'powerSchedule.readUnavailable',
    'powerSchedule.clusterUnavailable', 'powerSchedule.hostUnknown', 'powerSchedule.saveUnconfirmed', 'powerSchedule.activationUnconfirmed',
    'powerSchedule.unconfirmed', 'powerSchedule.resultUnconfirmed', 'powerSchedule.noTarget', 'powerSchedule.missed',
    'powerSchedule.bulk', 'powerSchedule.bulkHelp', 'powerSchedule.bulkEnable', 'powerSchedule.bulkDisable', 'powerSchedule.bulkToggleHelp',
    'powerSchedule.bulkSource', 'powerSchedule.bulkCopy', 'powerSchedule.bulkApply', 'powerSchedule.bulkUnavailable', 'powerSchedule.bulkDrafts',
    'powerSchedule.bulkChanged', 'powerSchedule.bulkComplete', 'powerSchedule.pending', 'powerSchedule.saving', 'powerSchedule.succeeded',
    'powerSchedule.notExecuted', 'powerSchedule.failed', 'powerSchedule.unchanged', 'powerSchedule.enabledCount', 'powerSchedule.targets',
    'powerSchedule.day0', 'powerSchedule.day1', 'powerSchedule.day2', 'powerSchedule.day3', 'powerSchedule.day4', 'powerSchedule.day5', 'powerSchedule.day6',
    'host.power.scheduleExpired', 'host.power.scheduleAlreadyAccepted', 'host.power.scheduleClaimUnconfirmed', 'Offline', 'Action', 'Repeat'];

/** 実行回の日時は、予定のタイムゾーンに対応するオフセットを維持して表示します。
 * @returns 明示した時差を持つ表示文字列です。
 */
function instant(value)
{
    return value ? value.replace('T', ' ').replace('Z', ' UTC') : '—';
}

/** 一Hostの予定と、共有するクラスタを明示した編集パネルを構成します。
 * @returns 個別編集と一括保存で共有する状態です。
 */
function hostPanel(root, ctx, host, signal)
{
    const section = node('section', '', 'hm-section'), title = heading(host.target.hostId), status = node('div', '', 'hm-muted');
    const cluster = select([]), controls = node('div', '', 'hm-actions'), resultStatus = node('p', '', 'hm-message');
    resultStatus.setAttribute('role', 'status');
    let view = null, dirty = false, generation = 0, uncertain = false, selectedCluster = '', loading = false, busy = false, held = false;
    const form = powerScheduleForm(ctx, [], () =>
    {
        dirty = true;
    });

    /** 読取り・保存の途中や結果不明の値を、次の書込みに使用させません。 */
    function updateControls()
    {
        form.setDisabled(!view || loading || busy || held || uncertain);
        save.disabled = !view || loading || busy || held || uncertain;
        cluster.disabled = busy || held;
        refresh.disabled = busy || held;
    }

    /** 確認できた現在版からだけ、編集値を作り直します。 */
    function render()
    {
        form.reset(view.schedules);
        status.replaceChildren(badge(ctx.t(view.active ? 'powerSchedule.active' : 'powerSchedule.inactive')),
            node('p', `${ctx.t('powerSchedule.enabledCount')}: ${view.schedules.filter(item => item.enabled).length} / ${view.schedules.length}`),
            node('p', ctx.t(view.wakeProfileAvailable ? 'powerSchedule.wakeAvailable' : 'powerSchedule.wakeMissing')));
        for (const item of view.status)
        {
            const wake = 'wake';
            const definition = view.schedules.find(row => row.id === item.id);
            const label = ctx.t(definition?.action === wake ? 'powerSchedule.wake' : 'powerSchedule.shutdown');
            status.append(node('p', `${label} · ${ctx.t('powerSchedule.next')}: ${instant(item.nextAt)} · ${ctx.t('powerSchedule.last')}: ${instant(item.lastAt)} · ${ctx.t(item.reason ?? item.outcome ?? '')}`));
        }
    }

    /** 古いクラスタの遅着応答では、編集中の新しい選択を置き換えません。
     * @returns 現在の表示への反映です。
     */
    async function loadAsync()
    {
        if (busy || held || signal.aborted)
        {
            return;
        }
        const current = ++generation;
        selectedCluster = cluster.value;
        view = null;
        form.reset([]);
        dirty = false;
        loading = true;
        resultStatus.textContent = '';
        updateControls();
        status.textContent = ctx.t('Loading settings');
        try
        {
            if (!selectedCluster)
            {
                throw new Error('powerSchedule.clusterUnavailable');
            }
            const loaded = await ctx.api('power-schedules/read', { clusterId: selectedCluster, hostId: host.target.hostId }, signal);
            if (signal.aborted || current !== generation)
            {
                return;
            }
            view = loaded;
            uncertain = false;
            render();
        }
        catch (error)
        {
            if (!signal.aborted && current === generation)
            {
                status.textContent = ctx.t('powerSchedule.readUnavailable');
            }
            throw error;
        }
        finally
        {
            if (current === generation)
            {
                loading = false;
                updateControls();
            }
        }
    }

    /** 確認画面の版と一致する一Hostだけを保存し、結果不明では再送を止めます。
     * @returns 元の保存の結果です。
     */
    async function saveAsync(schedules, expected = view)
    {
        if (!view || uncertain || busy || loading || signal.aborted || view.clusterId !== expected?.clusterId || view.revision !== expected?.revision)
        {
            throw new Error('powerSchedule.bulkChanged');
        }
        busy = true;
        updateControls();
        resultStatus.textContent = ctx.t('powerSchedule.saving');
        try
        {
            const result = await ctx.api('power-schedules/save', { operationId: crypto.randomUUID(), clusterId: expected.clusterId, hostId: expected.hostId,
                expectedRevision: expected.revision, schedules }, signal);
            const succeeded = 'succeeded';
            if (result.outcome !== succeeded || !result.view)
            {
                uncertain = true;
                resultStatus.textContent = ctx.t(result.reason ?? 'powerSchedule.saveUnconfirmed');
                return result;
            }
            view = result.view;
            dirty = false;
            render();
            resultStatus.textContent = ctx.t('powerSchedule.saved');
            return result;
        }
        catch (error)
        {
            uncertain = true;
            resultStatus.textContent = ctx.t('powerSchedule.saveUnconfirmed');
            throw error;
        }
        finally
        {
            busy = false;
            updateControls();
        }
    }

    const save = action(ctx, 'powerSchedule.save', 'check', async () =>
    {
        if (held || save.disabled)
        {
            return;
        }
        const result = await saveAsync(form.read());
        const succeeded = 'succeeded';
        ctx.say(ctx.t(result.outcome === succeeded && result.view ? 'powerSchedule.saved' : result.reason ?? 'powerSchedule.saveUnconfirmed'));
    }, true);
    const refresh = action(ctx, 'Refresh settings', 'refresh', () => ctx.discard(loadAsync));
    controls.append(save, refresh);
    section.append(title, field(ctx.t('powerSchedule.cluster'), cluster), status, form.root, controls, resultStatus);
    root.append(section);
    updateControls();
    cluster.onchange = () =>
    {
        const requested = cluster.value;
        cluster.value = selectedCluster;
        if (busy || held)
        {
            return;
        }
        return ctx.guard(() => ctx.discard(async () =>
        {
            cluster.value = requested;
            await loadAsync();
        }));
    };

    /** 対象Hostの確認済み所属から、初期の担当クラスタを選びます。
     * @returns 最初の現在値の読取りです。
     */
    async function initializeAsync()
    {
        const choices = await ctx.api('power-schedules/clusters?hostId=' + encodeURIComponent(host.target.hostId), undefined, signal);
        if (signal.aborted)
        {
            return;
        }
        for (const value of choices)
        {
            const option = node('option', value);
            option.value = value;
            cluster.append(option);
        }
        cluster.value = choices.includes(host.target.clusterId) ? host.target.clusterId : choices[0] ?? '';
        if (!choices.length)
        {
            status.textContent = ctx.t('powerSchedule.clusterUnavailable');
            return;
        }
        await loadAsync();
    }

    return { initialize: initializeAsync, reload: loadAsync, save: saveAsync,

        hasChanges: () => dirty || busy || held,

        /** 全対象の確認が済むまで、一括操作の保存要求を作りません。
         * @returns 明示的に読み取った予定の独立した写しです。
         */
        snapshot()
        {
            if (!view || loading || busy || held || uncertain)
            {
                throw new Error('powerSchedule.bulkUnavailable');
            }
            if (dirty)
            {
                throw new Error('powerSchedule.bulkDrafts');
            }
            return structuredClone(view);
        },

        matches: expected => !dirty && !loading && !busy && !held && !uncertain && view?.clusterId === expected.clusterId && view?.revision === expected.revision,

        /** 一括適用中は、他Hostの保存やクラスタ切替との交錯を防ぎます。 */
        setHeld(value)
        {
            held = value;
            updateControls();
        } };
}

/** 選択Hostの予定をクラスタ別に管理し、ブラウザーは予定の実行主体にしません。
 * @returns この画面が所有する通信と未保存編集の寿命です。
 */
export function powerSchedulesView(root, ctx)
{
    const hosts = structuredClone(ctx.hosts());
    if (!hosts.length)
    {
        return noSelection(root, ctx);
    }
    const abort = new AbortController(), signal = AbortSignal.any([ctx.signal, abort.signal]), toolbar = node('div', '', 'hm-toolbar');
    root.append(heading(ctx.t('powerSchedule.title'), ctx.t('powerSchedule.help')), toolbar);
    const panels = hosts.map(host => hostPanel(root, ctx, host, signal));
    powerScheduleBulk(toolbar, ctx, panels, signal);
    ctx.guard(async () =>
    {
        for (const panel of panels)
        {
            await ctx.guard(panel.initialize);
        }
    });
    return { reload: async () =>
    {
        for (const panel of panels)
        {
            await panel.reload();
        }
    },

    hasChanges: () => panels.some(panel => panel.hasChanges()),

    dispose: () => abort.abort() };
}
