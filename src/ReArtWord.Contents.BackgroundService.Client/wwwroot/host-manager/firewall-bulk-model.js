import { aggregateState } from './tri-state.js';

/** 項目を表示用の順序へ並べます。
 * @returns 呼出し元が使用する値です。
 */
const sorted = values => [...values].map(value => value.toLowerCase()).sort();

/** ファイアウォール規則をまとめる識別値を作成します。
 * @returns 呼出し元が使用する値です。
 */
export function firewallGroupKey(rule)
{
    return JSON.stringify([rule.name.toLowerCase(), rule.direction, rule.action, rule.profile.split(',').map(value => value.trim()).sort()]);
}

/** ファイアウォール定義の同一性を表す値を作成します。
 * @returns 呼出し元が使用する値です。
 */
export function firewallDefinitionKey(rule)
{
    return JSON.stringify([firewallGroupKey(rule), rule.protocol.toUpperCase(), sorted(rule.localPorts), sorted(rule.remotePorts), sorted(rule.localAddresses), sorted(rule.remoteAddresses), rule.program.toLowerCase()]);
}

/** 複数ホストのファイアウォール設定をまとめます。
 * @returns 呼出し元が使用する値です。
 */
export function aggregateFirewall(templates)
{
    const groups = new Map();
    templates.forEach((rules, index) => rules.forEach(item =>
    {
        const key = firewallDefinitionKey(item.rule);
        if (!groups.has(key))
        {
            groups.set(key, { key, group: firewallGroupKey(item.rule), rule: item.rule, values: Array.from({ length: templates.length }, () => []), supported: true });
        }
        const group = groups.get(key);
        group.values[index].push(item.rule);
        group.supported &&= item.cloneable && item.rule.editable;
    }));
    return [...groups.values()].map(group => ({ ...group, state: aggregateState(group.values.map(values => !values.length ? null : values.every(rule => rule.enabled) ? true : values.every(rule => !rule.enabled) ? false : null)) }));
}

/** 利用可能なファイアウォール規則の雛形を取得します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function readFirewallTemplates(ctx, host, summaries, signal)
{
    const result = [];
    for (let offset = 0; offset < summaries.length; offset += 10)
    {
        const ids = summaries.slice(offset, offset + 10).map(rule => rule.id);
        const snapshot = await ctx.read(host, { area: 'firewall', resourceId: 'bulk', filter: JSON.stringify(ids) }, signal);
        if (snapshot.data.rules.length !== ids.length || snapshot.data.rules.some(item => !ids.includes(item.rule.id)))
        {
            throw new Error('Settings changed. Refresh before editing again.');
        }
        result.push(...snapshot.data.rules);
    }
    return result;
}

/** 選択内容から対象ごとのファイアウォール変更を組み立てます。
 * @returns 呼出し元が使用する値です。
 */
export function firewallChanges(hosts, groups, choices, translate = value => value)
{
    const changes = [];
    for (const group of groups)
    {
        const expectedOther = 'other';

        const state = choices.get(group.key);
        if (!state || state === expectedOther)
        {
            continue;
        }
        if (!group.supported)
        {
            throw new Error('This rule has conditions that cannot be reproduced on every selected host.');
        }
        hosts.forEach((host, index) =>
        {
            const present = group.values[index], enabled = state === 'enable';
            if (present.length)
            {
                for (const rule of present)
                {
                    if (rule.enabled !== enabled)
                    {
                        changes.push({ host, query: { area: 'firewall', resourceId: 'rule/' + rule.id }, action: 'firewallRuleEnabled',
                            snapshot: { revision: rule.revision, data: { ...rule, revision: null } }, change: { enabled }, label: rule.name });
                    }
                }
            }
            else
            {
                const operationId = crypto.randomUUID(), id = 'Contents.HostManager.' + operationId.replaceAll('-', '');
                changes.push({ host, operationId, query: { area: 'firewall', resourceId: 'rule/' + id }, action: 'firewallRuleAdd',
                    change: { rule: { ...group.rule, id, enabled, editable: true, revision: null, detailsHash: null } }, label: `${translate('Add rule')} · ${group.rule.name}` });
            }
        });
    }
    return changes;
}
