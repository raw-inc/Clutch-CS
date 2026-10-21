import { node, select, field, heading, action, badge } from './dom.js';
import { createReader, readStatus, noSelection } from './loader.js';
import { hostsEditor } from './hosts-editor.js';
import { validateHostsText } from './hosts-model.js';
import { hostsBulkView } from './hosts-bulk.js';

/** hosts設定の取得・編集・確認を構成します。
 * @returns dispose・hasChanges・reloadを含む結果です。
 */
export function hostsView(root, ctx)
{
    const hosts = structuredClone(ctx.hosts());
    if (!hosts.length)
    {
        return noSelection(root, ctx);
    }
    if ((1) < (hosts.length))
    {
        return hostsBulkView(root, ctx, hosts);
    }
    let original = '', edited = false, loaded = false;
    const title = heading(ctx.t('Hosts file'), ctx.t('Review a host’s current content, then edit the shared replacement.'));
    const source = select(hosts.map(host => [host.target.hostId, host.target.hostId]), hosts[0].target.hostId), status = node('div');
    const contents = hostsEditor(ctx, () =>
    {
        edited = contents.value !== original;
        update();
    });
    const current = node('pre'), left = node('section', '', 'hm-editor-pane'), right = node('section', '', 'hm-editor-pane'), grid = node('div', '', 'hm-editor-grid');
    const metadata = node('span', '', 'hm-editor-meta'), changed = badge(ctx.t('No changes'));
    const copy = action(ctx, 'Use this content', 'copy', () =>
    {
        const state = reader.states.get(source.value);
        if (state?.snapshot)
        {
            contents.setValue(state.snapshot.data.content, true);
        }
    });
    copy.disabled = true;
    left.append(field(ctx.t('Source host'), source), current, copy);
    const rightHeading = node('div', '', 'hm-heading');
    rightHeading.append(node('h3', ctx.t('Replace hosts content')), changed);
    right.append(rightHeading, contents.root, metadata);
    grid.append(left, right);
    const preview = action(ctx, 'Preview changes', 'check', () =>
    {
        const content = validateHostsText(contents.value);
        return ctx.preview(hosts.map(host => ({ host, query: { area: 'hosts' }, action: 'hosts', change: { content }, label: ctx.t('Replace hosts content') })), () =>
        {
            edited = false;
            original = contents.value;
        });
    }, true);
    const bar = node('div', '', 'hm-selection-bar');
    bar.append(node('span', `${ctx.t('Affected hosts')}: ${hosts.map(host => host.target.hostId).join(', ')}`), preview);
    title.append(action(ctx, 'Refresh settings', 'refresh', () => ctx.discard(() =>
    {
        edited = false;
        return reader.load();
    }, edited)));
    root.append(title, node('p', ctx.t('The complete hosts content will replace each selected host’s file. Review every difference below.'), 'hm-muted'), status, grid, bar);

    /** 現在の設定と状態から表示を更新します。 */
    function update()
    {
        const state = reader.states.get(source.value);
        current.textContent = state?.snapshot?.data.content ?? ctx.t(state?.error || 'Loading…');
        copy.disabled = !state?.snapshot || !loaded;
        metadata.textContent = `${contents.value.split(/\r?\n/).length} ${ctx.t('lines')} · ${new TextEncoder().encode(contents.value).length} B`;
        const differs = loaded && [...reader.states.values()].some(item => item.snapshot.data.content !== contents.value);
        changed.textContent = ctx.t(differs ? 'Pending changes' : 'No changes');
        changed.className = 'hm-badge' + (differs ? ' accent' : '');
        preview.disabled = !differs;
    }

    const reader = createReader(ctx, hosts, () => ({ area: 'hosts' }), states =>
    {
        readStatus(status, ctx, states);
        loaded = states.size === hosts.length && [...states.values()].every(state => state.snapshot);
        contents.setDisabled(!loaded);
        if (loaded && !edited)
        {
            original = states.get(source.value).snapshot.data.content;
            contents.setValue(original);
        }
        update();
    });
    source.onchange = update;
    ctx.guard(reader.load);
    return { dispose: reader.dispose,

        hasChanges: () => edited,

        reload: reader.load };
}
