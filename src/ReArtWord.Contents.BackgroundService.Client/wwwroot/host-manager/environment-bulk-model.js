import { hasControlCharacters } from './input-validation.js';
import { aggregateState } from './tri-state.js';

/** 複数ホストのパス設定を共通値と差分へまとめます。
 * @returns 呼出し元が使用する値です。
 */
export function aggregatePaths(variables)
{
    const entries = variables.map(variable => variable?.value.split(';') ?? []), values = [...new Set(entries.flat().filter(value => value !== ''))];
    return values.map(value => ({ value, state: aggregateState(entries.map(items => items.includes(value))), present: entries.map(items => items.includes(value)) }));
}

/** 指定されたパスの変更を対象の設定に反映します。
 * @returns 呼出し元が使用する値です。
 */
export function patchPaths(variable, patches)
{
    let entries = variable ? variable.value.split(';') : [], changed = false;
    for (const { value, state } of patches)
    {
        const expectedEnable = 'enable';
        const expectedDisable = 'disable';

        if (!value || value.includes(';') || hasControlCharacters(value))
        {
            throw new Error('Validation failed. Check the input values.');
        }
        if (state === expectedEnable && !entries.includes(value))
        {
            entries.push(value);
            changed = true;
        }
        if (state === expectedDisable && entries.includes(value))
        {
            entries = entries.filter(item => item !== value);
            changed = true;
        }
    }
    return changed ? { value: entries.join(';'), delete: false, expandable: variable?.expandable ?? true } : null;
}

/** 複数ホストの環境変数を比較可能な一覧へまとめます。
 * @returns 呼出し元が使用する値です。
 */
export function aggregateVariables(states)
{
    const groups = new Map();
    states.forEach((variables, index) => variables.forEach(variable =>
    {
        const key = variable.name.toUpperCase();
        if (!groups.has(key))
        {
            groups.set(key, { key, name: variable.name, values: Array(states.length).fill(null) });
        }
        groups.get(key).values[index] = variable;
    }));
    return [...groups.values()].map(group => ({ ...group, common: group.values.every(value => value && value.value === group.values[0]?.value && value.expandable === group.values[0]?.expandable) }));
}
