import { node, input, select, field, heading, action, badge, table } from './dom.js';
import { createReader, readStatus, noSelection } from './loader.js';
import { powerTimers, powerActions, powerTime, powerStatus, powerMinutes, installationPowerChange } from './power-model.js';

/** 時間・押下動作のうち入力された値だけを取り出します。
 * @returns 変更しない項目を含まない予定です。
 */
export function powerFormChange(controls)
{
    const ac = 'ac';
    const change = {};
    for (const supply of ['ac', 'dc'])
    {
        const values = {};
        for (const key of Object.keys(powerTimers))
        {
            const value = powerMinutes(controls[supply][key].value);
            if (value !== undefined)
            {
                values[key] = value;
            }
        }
        const button = controls[supply].powerButtonAction.value;
        if (button.length)
        {
            values.powerButtonAction = Number(button);
        }
        if (Object.keys(values).length)
        {
            change[supply] = values;
        }
        const shutdown = powerMinutes(controls[supply].shutdown.value);
        if (shutdown !== undefined)
        {
            change[supply === ac ? 'idleShutdownAcSeconds' : 'idleShutdownDcSeconds'] = shutdown;
        }
    }
    if (controls.fastStartup.value.length)
    {
        const enabled = 'true';
        change.fastStartup = controls.fastStartup.value === enabled;
    }
    if (!Object.keys(change).length)
    {
        throw new Error('Device.NoChanges');
    }
    return change;
}

/** 分単位の任意入力欄を作ります。
 * @returns 空欄を変更なしとする入力です。
 */
function minutesInput(ctx)
{
    const control = input('', 'number');
    control.min = '0';
    control.max = '10080';
    control.step = '1';
    control.placeholder = ctx.t('Keep unchanged');
    return control;
}

/** 設定済みの値とは別に、明示変更する項目だけを入力します。 */
function editPower(ctx, states, preset)
{
    const ac = 'ac';
    const dialog = ctx.dialog('Edit power settings'), controls = {};
    dialog.root.append(node('p', ctx.t('Blank fields are unchanged. Time is in minutes; 0 disables the timer.'), 'hm-muted'));
    const grid = node('div', '', 'hm-grid');
    for (const supply of ['ac', 'dc'])
    {
        const panel = node('section'), values = {};
        panel.append(node('h3', ctx.t(supply === ac ? 'AC power' : 'Battery power')));
        for (const [key, label] of Object.entries(powerTimers))
        {
            values[key] = minutesInput(ctx);
            panel.append(field(ctx.t(label), values[key]));
        }
        values.shutdown = minutesInput(ctx);
        values.powerButtonAction = select([['', ctx.t('Keep unchanged')], ...powerActions.map((label, index) => [String(index), ctx.t(label)])]);
        panel.append(field(ctx.t('Idle shutdown (Clutch-CS)'), values.shutdown), field(ctx.t('Power button action'), values.powerButtonAction));
        controls[supply] = values;
        grid.append(panel);
    }
    controls.fastStartup = select([['', ctx.t('Keep unchanged')], ['false', ctx.t('Disabled')], ['true', ctx.t('Enabled')]]);
    dialog.root.append(grid, field(ctx.t('Fast Startup'), controls.fastStartup), node('p', ctx.t('Idle shutdown requires Core to run and verified input inactivity in every signed-in session. Enabling it starts a new waiting period. Sleep or hibernation can suspend this monitor.'), 'hm-muted'));
    dialog.footer.append(ctx.button('Cancel', dialog.close), action(ctx, 'Preview changes', 'check', () => ctx.preview(states.map(state => ({
        host: state.host, query: { area: 'power' }, action: 'power', baseRevision: state.snapshot.revision,
        change: powerFormChange(controls), label: ctx.t('Power settings')
    }))), true));
    if (preset)
    {
        for (const supply of ['ac', 'dc'])
        {
            for (const key of Object.keys(powerTimers))
            {
                if (states.every(state => installationPowerChange(state.snapshot.data)[supply][key] === 0))
                {
                    controls[supply][key].value = '0';
                }
            }
            controls[supply].shutdown.value = '0';
        }
        controls.fastStartup.value = 'false';
    }
}

/** 電源設定の現在値と編集可能性をHostごとに表示します。 */
function renderPowerState(root, ctx, state)
{
    const value = state.snapshot.data, section = node('section', '', 'hm-section');
    section.append(node('h3', state.host.target.hostId), node('p', `${ctx.t('Active power plan')}: ${value.schemeName}`), badge(ctx.t(powerStatus(value))));
    const list = table([ctx.t('Setting'), ctx.t('AC power'), ctx.t('Battery power')]);
    for (const [key, label] of Object.entries(powerTimers))
    {
        list.add([ctx.t(label), powerTime(value.ac[key], ctx.t), powerTime(value.dc[key], ctx.t)]);
    }
    list.add([ctx.t('Idle shutdown (Clutch-CS)'), powerTime(value.idleShutdown.acSeconds, ctx.t), powerTime(value.idleShutdown.dcSeconds, ctx.t)]);
    list.add([ctx.t('Power button action'), ctx.t(powerActions[value.ac.powerButtonAction] ?? 'Unconfirmed'), ctx.t(powerActions[value.dc.powerButtonAction] ?? 'Unconfirmed')]);
    section.append(list.root, node('p', `${ctx.t('Fast Startup')}: ${ctx.t(value.fastStartup == null ? 'Unconfirmed' : value.fastStartup ? 'Enabled' : 'Disabled')}`));
    if (!value.sleepAvailable || !value.hibernateAvailable)
    {
        section.append(node('p', ctx.t('Some sleep or hibernation features are unavailable. Only supported actions can be enabled.'), 'hm-muted'));
    }
    if (value.restrictions.length)
    {
        section.append(node('p', ctx.t('Device.PowerManagedPolicy'), 'hm-muted'));
    }
    const edit = action(ctx, 'Edit power settings', 'settings', () => editPower(ctx, [state], false));
    edit.disabled = !state.host.capabilities.elevated || !!value.restrictions.length;
    section.append(edit);
    root.append(section);
}

/** 選択Hostの電源設定を読み、確認後に部分変更します。
 * @returns 読取り処理の寿命制御です。
 */
export function powerView(root, ctx)
{
    const hosts = structuredClone(ctx.hosts());
    if (!hosts.length)
    {
        return noSelection(root, ctx);
    }
    const title = heading(ctx.t('Power settings'), `${ctx.t('Selected hosts')}: ${hosts.length}`), status = node('div'), listing = node('div');
    const reader = createReader(ctx, hosts, () => ({ area: 'power' }), render);
    const edit = action(ctx, 'Edit selected hosts', 'settings', () => editPower(ctx, [...reader.states.values()], false));
    const preset = action(ctx, 'Prepare continuous operation settings', 'check', () => editPower(ctx, [...reader.states.values()], true));
    edit.disabled = true;
    preset.disabled = true;
    title.append(action(ctx, 'Refresh settings', 'refresh', () => reader.load()), edit, preset);
    root.append(title, node('p', ctx.t('Review automatic timers before operation. The continuous operation preset disables these timers and Fast Startup; the power button action is retained.'), 'hm-muted'), status, listing);

    /** 読取り成功・管理者権限をすべて確認できた対象だけを編集します。 */
    function render()
    {
        readStatus(status, ctx, reader.states);
        const states = [...reader.states.values()];
        edit.disabled = states.length !== hosts.length || states.some(state => !state.snapshot || !state.host.capabilities.elevated || state.snapshot.data.restrictions.length);
        preset.disabled = edit.disabled;
        listing.replaceChildren();
        for (const state of states.filter(state => state.snapshot))
        {
            renderPowerState(listing, ctx, state);
        }
    }

    ctx.guard(reader.load);
    return { reload: () => reader.load(),

        dispose: () => reader.dispose() };
}
