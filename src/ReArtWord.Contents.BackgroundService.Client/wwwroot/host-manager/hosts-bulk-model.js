import { parseHosts, hostsText, setHostsEntry, validateHostsText } from './hosts-model.js';
import { addressBytes } from './network-model.js';
import { aggregateState } from './tri-state.js';

/** hosts項目を照合するための識別値を作成します。
 * @returns 呼出し元が使用する値です。
 */
export function hostsEntryKey(entry)
{
    return JSON.stringify([addressBytes(entry.address, entry.address.includes(':') ? 6 : 4), [...new Set(entry.names.toLowerCase().split(/\s+/))].sort()]);
}

/** 複数ホストのhosts設定を比較可能な形にまとめます。
 * @returns 呼出し元が使用する値です。
 */
export function aggregateHosts(contents)
{
    const documents = contents.map(parseHosts), groups = new Map();
    documents.forEach((document, host) => document.lines.forEach(line =>
    {
        if (!line.entry)
        {
            return;
        }
        const key = hostsEntryKey(line.entry);
        if (!groups.has(key))
        {
            groups.set(key, { key, entry: line.entry, values: Array.from({ length: contents.length }, () => []) });
        }
        groups.get(key).values[host].push(line.entry.enabled);
    }));
    return [...groups.values()].map(group => ({ ...group, states: group.values.map(values => values.length ? aggregateState(values) : 'missing'),
        state: aggregateState(group.values.map(values => !values.length ? null : values.every(Boolean) ? true : values.every(value => !value) ? false : null)) }));
}

// Build from the freshly read file at preview time. Unselected lines retain their original bytes.
/** 選択されたhosts変更を元の構成に反映します。
 * @returns 呼出し元が使用する値です。
 */
export function patchHosts(content, patches)
{
    const document = parseHosts(content);
    for (const { entry, state } of patches)
    {
        const expectedOther = 'other';

        if (state === expectedOther)
        {
            continue;
        }
        if (!['enable', 'disable'].includes(state))
        {
            throw new Error('Validation failed. Check the input values.');
        }
        const key = hostsEntryKey(entry), matches = document.lines.map((line, index) => ({ line, index })).filter(({ line }) => line.entry && hostsEntryKey(line.entry) === key);
        if (!matches.length)
        {
            setHostsEntry(document, document.lines.length, { ...entry, enabled: state === 'enable' });
        }
        else
        {
            for (const { line, index } of matches)
            {
                setHostsEntry(document, index, { ...line.entry, enabled: state === 'enable' });
            }
        }
    }
    return validateHostsText(hostsText(document));
}
