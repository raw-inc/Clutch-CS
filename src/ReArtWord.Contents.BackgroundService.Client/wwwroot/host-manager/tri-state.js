import { node } from './dom.js';

/** 複数対象の状態を共通値または混在に分類します。
 * @returns 呼出し元が使用する値です。
 */
export function aggregateState(values)
{
    if (values.length && values.every(value => value === true))
    {
        return 'enable';
    }
    if (values.length && values.every(value => value === false))
    {
        return 'disable';
    }
    return 'other';
}

/** 共通・混在・未変更を表す入力を構成します。
 * @returns 呼出し元が使用する値です。
 */
export function triState(ctx, label, value, changed)
{
    const root = node('div', '', 'hm-tristate');
    root.setAttribute('role', 'radiogroup');
    root.setAttribute('aria-label', label);
    const keys = ['disable', 'other', 'enable'];

    /** 対象と選択状態を照合して切り替えます。 */
    function select(next)
    {
        value = next;
        for (const button of root.children)
        {
            const selected = button.dataset.value === value;
            button.setAttribute('aria-checked', String(selected));
            button.tabIndex = selected ? 0 : -1;
        }
    }

    for (const key of keys)
    {
        const button = ctx.button(key, () =>
        {
            select(key);
            changed(key);
        });
        button.dataset.value = key;
        button.setAttribute('role', 'radio');
        root.append(button);
    }
    root.addEventListener('keydown', event =>
    {
        const expectedHome = 'Home';
        const expectedEnd = 'End';
        const expectedArrowright = 'ArrowRight';

        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key))
        {
            return;
        }
        event.preventDefault();
        const index = event.key === expectedHome ? 0 : event.key === expectedEnd ? 2 : (keys.indexOf(value) + (event.key === expectedArrowright ? 1 : 2)) % 3;
        root.children[index].click();
        root.children[index].focus();
    });
    select(value);
    return root;
}
