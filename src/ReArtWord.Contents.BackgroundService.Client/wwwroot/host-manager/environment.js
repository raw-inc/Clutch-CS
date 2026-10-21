import { node, select, input, field, heading, action, badge, table, empty, segmented } from './dom.js';
import { createReader, readStatus, noSelection } from './loader.js';
import { environmentForm } from './environment-form.js';
import { environmentBulk } from './environment-bulk.js';

/** 環境変数の取得・表示・変更を構成します。
 * @returns reload・hasChanges・disposeを含む結果です。
 */
export function environmentView(root, ctx)
{
    const hosts = structuredClone(ctx.hosts());
    if (!hosts.length)
    {
        return noSelection(root, ctx);
    }
    let scope = 'Machine', userSids = new Map(), reader = null;
    const bulk = (1) < (hosts.length) ? environmentBulk(ctx, hosts, query) : null;
    const title = heading(ctx.t('PATH and environment variables'), `${ctx.t('Selected hosts')}: ${hosts.length}`), status = node('div'), listing = node('div');
    const tools = node('div', '', 'hm-toolbar'), hostPick = select(hosts.map(host => [host.target.hostId, host.target.hostId]), hosts[0].target.hostId);
    const search = input('', 'search');
    search.className = 'hm-search';
    search.placeholder = ctx.t('Search variables');
    search.setAttribute('aria-label', ctx.t('Search variables'));
    const users = action(ctx, 'Choose users', 'settings', selectUsers);
    users.hidden = true;
    const scopePicker = segmented([['Machine', ctx.t('Machine')], ['User', ctx.t('User')]], scope, value =>
    {
        scopePicker.selectValue(scope);
        ctx.discard(() =>
        {
            scope = value;
            scopePicker.selectValue(scope);
            users.hidden = scope !== 'User';
            return reload();
        }, bulk?.hasChanges());
    }, ctx.t('Scope'));
    const add = action(ctx, 'Add variable', 'plus', () => edit(null), true), refresh = action(ctx, 'Refresh settings', 'refresh', () => ctx.discard(reload, bulk?.hasChanges())), actions = node('div', '', 'hm-actions');
    actions.append(refresh, add);
    title.append(actions);
    tools.append(scopePicker, users, field(ctx.t('Variables on host'), hostPick), search);
    hostPick.parentElement.hidden = !!bulk;
    root.append(title, node('p', ctx.t('Persistent environment variables apply to new processes. Existing processes and other user sessions may require a restart or sign-in.'), 'hm-muted'), tools, status, listing);

    /** 現在の対象に必要な照会条件を作成します。
     * @returns area・scope・userSid・resourceIdを含む結果です。
     */
    function query(host, resourceId = null)
    {
        const expectedUser = 'User';

        return { area: 'environment', scope, userSid: scope === expectedUser ? userSids.get(host.target.hostId) : null, resourceId };
    }

    /** 対象の設定が明示されているかを確認します。
     * @returns 呼出し元が使用する値です。
     */
    function configured()
    {
        return scope !== 'User' || hosts.every(host => userSids.get(host.target.hostId));
    }

    /** 指定された対象の編集を開始します。 */
    function edit(variable)
    {
        const source = hosts.find(host => host.target.hostId === hostPick.value);
        environmentForm(ctx, hosts.map(host => ({ host, query: query(host) })), source, variable);
    }

    /** 現在の値を表示内容へ反映します。 */
    function render()
    {
        listing.replaceChildren();
        add.disabled = refresh.disabled = !configured();
        if (!configured())
        {
            listing.append(empty(ctx.t('Choose a user for each host'), ctx.t('Only loaded user profiles can be selected.'), 'monitor'));
            return;
        }
        if (bulk)
        {
            bulk.render(listing, reader?.states ?? new Map(), search.value.trim().toLocaleLowerCase());
            return;
        }
        const host = hosts.find(host => host.target.hostId === hostPick.value), state = reader?.states.get(hostPick.value);
        if (!state?.snapshot)
        {
            return;
        }
        const text = search.value.trim().toLocaleLowerCase();
        const variables = [...state.snapshot.data.variables].filter(variable => `${variable.name} ${variable.value}`.toLocaleLowerCase().includes(text))
            .sort((a, b) => Number(b.name.toUpperCase() === 'PATH') - Number(a.name.toUpperCase() === 'PATH') || a.name.localeCompare(b.name));
        if (!variables.length)
        {
            listing.append(empty(ctx.t('No matching variables'), ctx.t('Try another search or add a variable.'), 'code'));
            return;
        }
        const rows = table(['Name', 'Value', 'Edit'].map(ctx.t));
        for (const variable of variables)
        {
            const path = variable.name.toUpperCase() === 'PATH', name = node('div'), value = node('div');
            name.append(ctx.button(variable.name, () => edit(variable), 'hm-link'), node('small', ctx.t(variable.expandable ? 'Variable references' : 'Literal value')));
            value.append(node('pre', path ? (variable.value ?? '').split(';').join('\n') : variable.value, 'hm-value'));
            if (path)
            {
                value.append(node('small', `${(variable.value ?? '').split(';').length} ${ctx.t('path entries')}`));
            }
            const actions = node('div', '', 'hm-actions');
            actions.append(action(ctx, 'Edit', 'settings', () => edit(variable)), ctx.button('Delete', () => ctx.preview(hosts.map(target => ({ host: target, query: query(target, variable.name), action: 'environment', change: { value: null, delete: true, expandable: variable.expandable }, label: `${ctx.t('Delete variable')} · ${variable.name}` }))), 'danger'));
            rows.add([name, value, actions]);
        }
        const summary = node('div', '', 'hm-toolbar');
        summary.append(badge(`${variables.length} ${ctx.t('variables')}`), node('span', `${host.target.hostId} · ${ctx.t(scope)}`, 'hm-muted'));
        listing.append(summary, rows.root);
    }

    /** 操作するユーザー範囲を選択します。 */
    function selectUsers()
    {
        const dialog = ctx.dialog('Choose users'), chosen = new Map(userSids);
        dialog.root.append(node('p', ctx.t('Only loaded user profiles can be selected.'), 'hm-muted'));
        const apply = action(ctx, 'Use selected users', 'check', () => ctx.discard(() =>
        {
            userSids = chosen;
            dialog.close();
            return reload();
        }, bulk?.hasChanges()), true);

        /** 現在の設定と状態から表示を更新します。 */
        const update = () =>
        {
            apply.disabled = !hosts.every(host => chosen.get(host.target.hostId));
        };

        for (const host of hosts)
        {
            const users = host.capabilities.users, pick = select([['', ctx.t('Select a user')], ...users.map(user => [user.sid, user.name])], chosen.get(host.target.hostId) ?? '');
            pick.onchange = () =>
            {
                chosen.set(host.target.hostId, pick.value);
                update();
            };
            dialog.root.append(field(host.target.hostId, pick, users.length ? '' : ctx.t('No loaded user profiles')));
        }
        update();
        dialog.footer.append(ctx.button('Cancel', dialog.close), apply);
    }

    /** 最新状態を取得し、現在の表示へ反映します。
     * @returns 呼出し元が使用する値です。
     */
    function reload()
    {
        bulk?.reset();
        reader?.dispose();
        status.replaceChildren();
        reader = createReader(ctx, hosts, host => query(host), states =>
        {
            readStatus(status, ctx, states);
            render();
        });
        render();
        if (configured())
        {
            return reader.load();
        }
    }

    search.oninput = hostPick.onchange = render;
    ctx.guard(reload);
    return { reload,

        hasChanges: () => bulk?.hasChanges() ?? false,

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            reader?.dispose();
        } };
}
