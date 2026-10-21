import { hasControlCharacters } from './input-validation.js';
import { node, table, field, input, textarea, select, checkbox, action, details, badge } from './dom.js';
import { triState } from './tri-state.js';
import { aggregatePaths, aggregateVariables } from './environment-bulk-model.js';

/** 複数ホストの環境変数を変更する画面を構成します。
 * @returns render・reset・hasChangesを含む結果です。
 */
export function environmentBulk(ctx, hosts, query)
{
    const choices = new Map(), added = new Set();
    let states = [], paths = [];

    /** 保存前の変更予定を呼出し元へ返します。
     * @returns 呼出し元が使用する値です。
     */
    function pending()
    {
        return paths.filter(row => choices.has(row.value) && choices.get(row.value) !== 'other' && choices.get(row.value) !== row.state).map(row => ({ value: row.value, state: choices.get(row.value) }));
    }

    /** 一時状態を初期値へ戻します。 */
    function reset()
    {
        choices.clear();
        added.clear();
    }

    /** 現在の値を表示内容へ反映します。 */
    function render(target, currentStates, search)
    {
        states = hosts.map(host => currentStates.get(host.target.hostId));
        if (states.some(state => !state?.snapshot))
        {
            return;
        }
        const variables = states.map(state => state.snapshot.data.variables), groups = aggregateVariables(variables);
        const pathVariables = variables.map(values => values.find(value => value.name.toUpperCase() === 'PATH') ?? null);
        paths = aggregatePaths(pathVariables);
        for (const value of added)
        {
            if (!paths.some(row => row.value === value))
            {
                paths.push({ value, state: 'other', present: hosts.map(() => false) });
            }
        }
        const pathTitle = node('div', '', 'hm-heading');
        pathTitle.append(node('h3', ctx.t('PATH entries across hosts')), action(ctx, 'Add path', 'plus', () =>
        {
            const dialog = ctx.dialog('Add path'), value = input('');
            dialog.root.append(field(ctx.t('Path entry'), value));
            dialog.footer.append(ctx.button('Cancel', dialog.close), action(ctx, 'Use entry', 'check', () =>
            {
                if (!value.value || (value.value.includes(';') || hasControlCharacters(value.value)))
                {
                    throw new Error(ctx.t('Validation failed. Check the input values.'));
                }
                added.add(value.value);
                choices.set(value.value, 'enable');
                dialog.close();
                target.replaceChildren();
                render(target, currentStates, search);
            }, true));
        }));
        target.append(pathTitle, node('p', ctx.t('Enable adds the path if missing; disable removes that path. Other preserves order, duplicates and variable references on each host.'), 'hm-muted'));
        const list = table(['Path entry', 'Across selected hosts', 'Host states'].map(ctx.t));
        const summary = node('span'), preview = action(ctx, 'Preview changes', 'check', () => ctx.preview(hosts.map((host, index) => ({ host, query: query(host, pathVariables[index]?.name ?? 'Path'), action: 'environment', patch: { kind: 'paths', entries: pending() }, label: ctx.t('Selected PATH entries') })), reset), true);

        /** 現在の設定と状態から表示を更新します。 */
        function update()
        {
            summary.textContent = `${ctx.t('Changes')}: ${pending().length}`;
            preview.disabled = !pending().length;
        }

        for (const row of paths.filter(row => row.value.toLocaleLowerCase().includes(search)))
        {
            const status = table(['Host', 'Status'].map(ctx.t));
            hosts.forEach((host, index) => status.add([host.target.hostId, ctx.t(row.present[index] ? 'enable' : 'Not defined')]));
            list.add([node('code', row.value), triState(ctx, row.value, choices.get(row.value) ?? row.state, value =>
            {
                choices.set(row.value, value);
                update();
            }), details(ctx.t('Host states'), status.root)]);
        }
        const bar = node('div', '', 'hm-selection-bar');
        bar.append(summary, ctx.button('Reset changes', () =>
        {
            reset();
            target.replaceChildren();
            render(target, currentStates, search);
        }), preview);
        target.append(list.root, bar);
        update();
        const grid = table(['Name', 'Value', 'Edit'].map(ctx.t));
        for (const group of groups.filter(group => group.key !== 'PATH' && `${group.name} ${group.values.map(value => value?.value ?? '').join(' ')}`.toLocaleLowerCase().includes(search)).sort((a, b) => a.name.localeCompare(b.name)))
        {
            const values = table(['Host', 'Value'].map(ctx.t));
            hosts.forEach((host, index) => values.add([host.target.hostId, group.values[index]?.value ?? ctx.t('Not defined')]));
            const current = node('div');
            current.append(badge(ctx.t(group.common ? 'All hosts agree' : 'other'), group.common ? '' : 'warn'), details(ctx.t('Host states'), values.root));
            grid.add([group.name, current, action(ctx, 'Edit', 'settings', () => editVariable(group))]);
        }
        target.append(node('h3', ctx.t('Environment variables')), grid.root);
    }

    /** 選択した環境変数の変更内容を編集します。 */
    function editVariable(group)
    {
        const dialog = ctx.dialog('Edit variable'), mode = select([['keep', ctx.t('Keep unchanged')], ['set', ctx.t('Set variable')], ['delete', ctx.t('Delete variable')]], 'keep');
        const value = textarea(group.common ? group.values[0].value : '', 5), expand = checkbox(ctx.t('Expand variable references'), group.common ? group.values[0].expandable : true);
        value.disabled = expand.control.disabled = true;
        const from = select([['', ctx.t('Choose source value')], ...hosts.filter((host, index) => group.values[index]).map(host => [host.target.hostId, host.target.hostId])]);
        const preview = action(ctx, 'Preview changes', 'check', () => ctx.preview(hosts.map(host => ({ host, query: query(host, group.name), action: 'environment', patch: { kind: 'variable', delete: mode.value === 'delete', value: value.value, expandable: expand.control.checked }, label: group.name }))), true);
        preview.disabled = true;
        mode.onchange = () =>
        {
            preview.disabled = mode.value === 'keep';
            value.disabled = expand.control.disabled = from.disabled = mode.value !== 'set';
        };
        from.disabled = true;
        from.onchange = () =>
        {
            const index = hosts.findIndex(host => host.target.hostId === from.value);
            if ((0) <= (index))
            {
                value.value = group.values[index].value;
                expand.control.checked = group.values[index].expandable;
            }
        };
        dialog.root.append(node('p', group.name, 'hm-muted'), field(ctx.t('Change'), mode), field(ctx.t('Source host'), from), field(ctx.t('Value'), value), expand.root);
        dialog.footer.append(ctx.button('Cancel', dialog.close), preview);
    }

    return { render, reset,

        hasChanges: () => (0) < (pending().length) };
}
