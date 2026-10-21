import { node, textarea, input, field, checkbox, action, segmented, empty, badge } from './dom.js';
import { parseHosts, hostsText, setHostsEntry, removeHostsEntry } from './hosts-model.js';

/** hostsの構造化表示とテキスト編集を接続します。
 * @returns root・value・setValue・setDisabledを含む結果です。
 */
export function hostsEditor(ctx, changed)
{
    let document = parseHosts(''), mode = 'entries', disabled = true;
    const root = node('div', '', 'hm-hosts-editor'), tools = node('div', '', 'hm-toolbar'), body = node('div');
    const text = textarea('', 16);
    text.className = 'hm-code-editor';
    text.setAttribute('aria-label', ctx.t('Replace hosts content'));
    const modes = segmented([['entries', ctx.t('Entry editor')], ['text', ctx.t('Text editor')]], mode, value =>
    {
        mode = value;
        render();
    }, ctx.t('Editing mode'));
    const add = action(ctx, 'Add hosts entry', 'plus', () => edit(null));
    tools.append(modes, add);
    root.append(tools, body, node('p', ctx.t('Comments and blank lines are preserved. Both editors use the same content.'), 'hm-muted'));

    /** 現在の変更内容を購読元へ通知します。 */
    function notify()
    {
        text.value = hostsText(document);
        changed();
    }

    /** 現在の値を表示内容へ反映します。 */
    function render()
    {
        const expectedText = 'text';

        body.replaceChildren();
        add.disabled = disabled;
        if (mode === expectedText)
        {
            text.value = hostsText(document);
            text.disabled = disabled;
            body.append(text);
            return;
        }
        const entries = document.lines.map((line, index) => ({ ...line, index })).filter(line => line.entry);
        if (!entries.length)
        {
            body.append(empty(ctx.t('No host entries'), ctx.t('Add an address and its host names, or switch to the text editor.'), 'file'));
        }
        for (const { entry, index } of entries)
        {
            const row = node('div', '', 'hm-hosts-entry'), enabled = checkbox('', entry.enabled), copy = node('div', '', 'hm-hosts-entry-copy');
            enabled.control.setAttribute('aria-label', `${ctx.t('Enabled')} · ${entry.address} · ${entry.names}`);
            enabled.control.disabled = disabled;
            enabled.control.onchange = () =>
            {
                setHostsEntry(document, index, { ...entry, enabled: enabled.control.checked });
                notify();
                render();
            };
            copy.append(node('strong', entry.address), node('span', entry.names));
            if (entry.comment)
            {
                copy.append(node('small', entry.comment));
            }
            const editButton = action(ctx, 'Edit', 'settings', () => edit(index)), remove = action(ctx, 'Delete', 'trash', () =>
            {
                removeHostsEntry(document, index);
                notify();
                render();
            });
            editButton.classList.add('hm-icon-button');
            editButton.setAttribute('aria-label', `${ctx.t('Edit')} · ${entry.address}`);
            editButton.title = ctx.t('Edit');
            remove.classList.add('hm-icon-button', 'danger');
            remove.setAttribute('aria-label', `${ctx.t('Delete')} · ${entry.address}`);
            remove.title = ctx.t('Delete');
            editButton.disabled = remove.disabled = disabled;
            row.append(enabled.root, copy, badge(ctx.t(entry.enabled ? 'Enabled' : 'Disabled'), entry.enabled ? 'good' : ''), editButton, remove);
            body.append(row);
        }
        if (document.lines.some(line => line.raw.trim() && !line.raw.trim().startsWith('#') && !line.entry))
        {
            body.append(node('p', ctx.t('Some lines can only be edited as text.'), 'hm-read-error'));
        }
    }

    /** 指定された対象の編集を開始します。 */
    function edit(index)
    {
        const existing = index == null ? null : document.lines[index].entry, dialog = ctx.dialog(existing ? 'Edit hosts entry' : 'Add hosts entry');
        dialog.element?.classList.add('hm-entry-dialog');
        const address = input(existing?.address ?? ''), names = input(existing?.names ?? ''), comment = input(existing?.comment ?? ''), enabled = checkbox(ctx.t('Enabled'), existing?.enabled ?? true);
        address.placeholder = '192.0.2.10';
        names.placeholder = 'app.local alias.local';
        dialog.root.append(field(ctx.t('IP address'), address, 'IPv4 / IPv6'), field(ctx.t('Host names and aliases'), names), field(ctx.t('Comment'), comment), enabled.root);
        dialog.footer.append(ctx.button('Cancel', dialog.close), action(ctx, 'Use entry', 'check', () =>
        {
            setHostsEntry(document, index ?? document.lines.length, { address: address.value.trim(), names: names.value, comment: comment.value, enabled: enabled.control.checked });
            notify();
            render();
            dialog.close();
        }, true));
    }

    text.oninput = () =>
    {
        document = parseHosts(text.value);
        changed();
    };
    render();
    return { root,

        get value()
        {
            return hostsText(document);
        },

        /** 新しい値を保持して表示に反映します。 */
        setValue(value, inform = false)
        {
            document = parseHosts(value);
            render();
            if (inform)
            {
                changed();
            }
        },

        /** 操作禁止状態を入力要素へ反映します。 */
        setDisabled(value)
        {
            disabled = value;
            render();
        } };
}
