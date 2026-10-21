import { hasControlCharacters } from './input-validation.js';
import { node, input, textarea, field, checkbox, action, section } from './dom.js';

/** 環境変数の入力と検証を構成します。 */
export function environmentForm(ctx, targets, sourceHost, variable)
{
    const dialog = ctx.dialog(variable ? 'Edit variable' : 'Add variable'), name = input(variable?.name ?? ''), expand = checkbox(ctx.t('Expand variable references'), variable?.expandable ?? true);
    name.readOnly = !!variable;
    const valueRoot = node('div'), text = textarea(variable?.value ?? '', 6);
    let entries = (variable?.value ?? '').split(';'), pathMode = false;
    dialog.root.append(node('p', `${ctx.t('Affected hosts')}: ${targets.map(item => item.host.target.hostId).join(', ')}`, 'hm-muted'));
    if (variable)
    {
        dialog.root.append(node('p', `${ctx.t('Based on')}: ${sourceHost.target.hostId} · ${ctx.t(targets[0].query.scope)}`, 'hm-muted'));
    }
    dialog.root.append(field(ctx.t('Variable name'), name), valueRoot, expand.root);

    /** 現在の項目一覧を描画します。 */
    function renderEntries(list)
    {
        list.replaceChildren();
        entries.forEach((entry, index) =>
        {
            const row = node('div', '', 'hm-path-entry'), value = input(entry);
            value.setAttribute('aria-label', `${ctx.t('Path entry')} ${index + 1}`);
            value.oninput = () =>
            {
                entries[index] = value.value;
            };
            const up = action(ctx, 'Move up', 'up', () =>
                {
                    [entries[index - 1], entries[index]] = [entries[index], entries[index - 1]];
                    renderEntries(list);
                }), down = action(ctx, 'Move down', 'down', () =>
                {
                    [entries[index + 1], entries[index]] = [entries[index], entries[index + 1]];
                    renderEntries(list);
                });
            const remove = action(ctx, 'Remove path', 'trash', () =>
            {
                entries.splice(index, 1);
                renderEntries(list);
            });
            for (const [button, label] of [[up, 'Move up'], [down, 'Move down'], [remove, 'Remove path']])
            {
                button.classList.add('hm-icon-button');
                button.setAttribute('aria-label', `${ctx.t(label)} ${index + 1}`);
                button.title = ctx.t(label);
            }
            up.disabled = index === 0;
            down.disabled = index === entries.length - 1;
            row.append(node('span', String(index + 1)), value, up, down, remove);
            list.append(row);
        });
    }

    /** 対象の値を入力欄または確認欄へ表示します。 */
    function renderValue()
    {
        const next = name.value.toUpperCase() === 'PATH';
        if (next && !pathMode)
        {
            entries = text.value.split(';');
        }
        if (!next && pathMode)
        {
            text.value = entries.join(';');
        }
        pathMode = next;
        valueRoot.replaceChildren();
        if (!pathMode)
        {
            valueRoot.append(field(ctx.t('Value'), text));
            return;
        }
        const group = section(ctx.t('Search paths'), ctx.t('Paths are searched from top to bottom. Variable references are preserved.')), list = node('div', '', 'hm-path-list');
        renderEntries(list);
        group.append(list, action(ctx, 'Add path', 'plus', () =>
        {
            entries.push('');
            renderEntries(list);
            list.lastElementChild?.querySelector('input')?.focus();
        }));
        valueRoot.append(group);
    }

    name.oninput = renderValue;
    renderValue();
    dialog.footer.append(ctx.button('Cancel', dialog.close), action(ctx, 'Preview changes', 'check', () =>
    {
        if (!name.value.trim() || (name.value.includes('=') || hasControlCharacters(name.value)))
        {
            throw new Error(ctx.t('Validation failed. Check the input values.'));
        }
        const value = pathMode ? entries.join(';') : text.value;
        return ctx.preview(targets.map(({ host, query }) => ({ host, query: { ...query, resourceId: name.value }, action: 'environment', change: { value, delete: false, expandable: expand.control.checked }, label: `${ctx.t('Set variable')} · ${name.value}` })));
    }, true));
}
