import { node, select, field, action, table, same, hostCount } from './dom.js';
import { powerScheduleForm } from './power-schedule-form.js';

/** 保存応答を、成功・未実行・失敗・結果不明に分けて表示します。
 * @returns Hostごとの結果表示キーです。
 */
function resultKey(result)
{
    const succeeded = 'succeeded', notExecuted = 'notExecuted', failed = 'failed';
    if (result.outcome === succeeded && result.view)
    {
        return 'powerSchedule.succeeded';
    }
    if (result.outcome === notExecuted)
    {
        return 'powerSchedule.notExecuted';
    }
    return result.outcome === failed ? 'powerSchedule.failed' : 'powerSchedule.saveUnconfirmed';
}

/** 表示済みの各Host・クラスタ・版を固定し、全対象の設定内容を確認してから保存します。 */
function openBulk(ctx, panels, signal, mode)
{
    if (signal.aborted)
    {
        return;
    }
    const snapshots = panels.map(panel => panel.snapshot()), replace = 'replace', enable = 'enable';
    const title = mode === replace ? 'powerSchedule.bulk' : mode === enable ? 'powerSchedule.bulkEnable' : 'powerSchedule.bulkDisable';
    const dialog = ctx.dialog(title), editor = node('div'), statuses = [], counts = [], columns = ['Host', 'powerSchedule.cluster', 'powerSchedule.enabledCount'];
    if (mode !== replace)
    {
        columns.push('powerSchedule.changing');
    }
    const targets = table([...columns, 'Status'].map(ctx.t));
    const disable = 'disable';
    const applyKey = mode === replace ? 'powerSchedule.bulkApply' : mode === enable ? 'powerSchedule.confirmEnable' : 'powerSchedule.confirmDisable';
    let submitted = false;
    dialog.root.append(node('p', ctx.t(mode === replace ? 'powerSchedule.bulkHelp' : 'powerSchedule.bulkToggleHelp'), 'hm-muted'), editor,
        node('h3', `${ctx.t('powerSchedule.targets')}: ${hostCount(ctx, snapshots.length)}`), targets.root);
    const form = mode === replace ? editTemplate(editor, ctx, snapshots) : null;
    for (const snapshot of snapshots)
    {
        const status = node('span', ctx.t('powerSchedule.pending'));
        const count = node('span', `${snapshot.schedules.filter(item => item.enabled).length} / ${snapshot.schedules.length}`);
        status.setAttribute('role', 'status');
        statuses.push(status);
        counts.push(count);
        const cells = [snapshot.hostId, snapshot.clusterId, count];
        if (mode !== replace)
        {
            cells.push(snapshot.schedules.filter(item => item.enabled !== (mode === enable)).length);
        }
        targets.add([...cells, status]);
    }
    const apply = action(ctx, applyKey, mode === disable ? 'stop' : 'check', async () =>
    {
        if (submitted || signal.aborted)
        {
            return;
        }
        if (!panels.every((panel, index) => panel.matches(snapshots[index])))
        {
            throw new Error('powerSchedule.bulkChanged');
        }
        const template = form?.read();
        const schedules = snapshots.map(snapshot => template ? structuredClone(template) : snapshot.schedules.map(item => ({ ...item, enabled: mode === enable })));
        submitted = true;
        apply.disabled = true;
        editor.inert = true;
        panels.forEach(panel => panel.setHeld(true));
        try
        {
            for (let index = 0; index < panels.length; index++)
            {
                if (signal.aborted)
                {
                    break;
                }
                const snapshot = snapshots[index], values = schedules[index], status = statuses[index];
                if (snapshot.active && same(snapshot.schedules, values) || mode !== replace && !values.length)
                {
                    status.textContent = ctx.t('powerSchedule.unchanged');
                    continue;
                }
                status.textContent = ctx.t('powerSchedule.saving');
                try
                {
                    const result = await panels[index].save(values, snapshot);
                    status.textContent = ctx.t(resultKey(result)) + (result.reason ? ' · ' + ctx.t(result.reason) : '');
                    if (result.view)
                    {
                        counts[index].textContent = `${result.view.schedules.filter(item => item.enabled).length} / ${result.view.schedules.length}`;
                    }
                }
                catch (error)
                {
                    status.textContent = ctx.t('powerSchedule.saveUnconfirmed');
                }
            }
            if (!signal.aborted)
            {
                ctx.say(ctx.t('powerSchedule.bulkComplete'));
            }
        }
        finally
        {
            panels.forEach(panel => panel.setHeld(false));
        }
    }, mode !== disable);
    apply.classList.toggle('danger', mode === disable);
    dialog.footer.append(ctx.button('Close', dialog.close), apply);
    signal.addEventListener('abort', dialog.close, { once: true });
    dialog.element.addEventListener('close', () => signal.removeEventListener('abort', dialog.close), { once: true });
}

/** 既存の保存済み予定を共通入力の初期値にし、任意のHostから明示的にコピーできます。
 * @returns 一括置換する全予定です。
 */
function editTemplate(root, ctx, snapshots)
{
    let dirty = false;
    const form = powerScheduleForm(ctx, snapshots[0].schedules, () =>
    {
        dirty = true;
    });
    const source = select(snapshots.map(snapshot => [snapshot.hostId, snapshot.hostId]), snapshots[0].hostId), tools = node('div', '', 'hm-actions');
    tools.append(field(ctx.t('powerSchedule.bulkSource'), source), action(ctx, 'powerSchedule.bulkCopy', 'copy', () => ctx.discard(() =>
    {
        const snapshot = snapshots.find(item => item.hostId === source.value);
        form.reset(snapshot.schedules);
        dirty = false;
    }, dirty)));
    root.append(tools, form.root);
    return form;
}

/** 選択Host全体の設定と、有効・無効だけの変更を別の明示操作にします。 */
export function powerScheduleBulk(root, ctx, panels, signal)
{
    root.classList.add('hm-schedule-bulk');
    root.append(node('span', `${ctx.t('powerSchedule.targets')}: ${hostCount(ctx, panels.length)}`),
        action(ctx, 'powerSchedule.bulk', 'settings', () => openBulk(ctx, panels, signal, 'replace')),
        action(ctx, 'powerSchedule.bulkEnable', 'check', () => openBulk(ctx, panels, signal, 'enable')),
        action(ctx, 'powerSchedule.bulkDisable', 'pause', () => openBulk(ctx, panels, signal, 'disable')));
}
