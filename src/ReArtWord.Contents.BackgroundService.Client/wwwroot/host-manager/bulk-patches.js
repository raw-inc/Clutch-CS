import { patchHosts } from './hosts-bulk-model.js';
import { patchPaths } from './environment-bulk-model.js';

/** 各対象の元の値を保ったまま一括変更を組み立てます。
 * @returns 呼出し元が使用する値です。
 */
export function resolveBulkPatch(item, before)
{
    const expectedHosts = 'hosts';
    const expectedPaths = 'paths';
    const expectedEnabled = 'enabled';
    const expectedVariable = 'variable';

    if (item.patch?.kind === expectedHosts)
    {
        const content = patchHosts(before.data.content, item.patch.entries);
        return content === before.data.content ? null : { content };
    }
    if (item.patch?.kind === expectedPaths)
    {
        return patchPaths(before.data, item.patch.entries);
    }
    if (item.patch?.kind === expectedEnabled)
    {
        if (!before.data)
        {
            throw new Error('Settings changed. Refresh before editing again.');
        }
        return before.data.enabled === item.patch.enabled ? null : { enabled: item.patch.enabled };
    }
    if (item.patch?.kind === expectedVariable)
    {
        if (item.patch.delete)
        {
            return before.data ? { value: null, delete: true, expandable: before.data.expandable } : null;
        }
        const { value, expandable } = item.patch;
        return before.data?.value === value && before.data?.expandable === expandable ? null : { value, expandable, delete: false };
    }
    return item.change;
}
