import { uiIcon } from '../ui-icons.js';

// All OS values enter the document as text or form values, never HTML.
/** 指定した属性を持つ画面要素を作成します。
 * @returns 呼出し元が使用する値です。
 */
export function node(tag, text = '', className = '')
{
    const element = document.createElement(tag);
    element.textContent = text;
    if (className)
    {
        element.className = className;
    }
    return element;
}

/** 説明と入力要素を一つのフィールドへまとめます。
 * @returns 呼出し元が使用する値です。
 */
export function field(label, control, help)
{
    const root = node('label', '', 'hm-field');
    root.append(node('span', label), control);
    control.setAttribute('aria-label', label);
    if (help)
    {
        root.append(node('small', help));
    }
    return root;
}

/** 型と初期値を持つ入力欄を作成します。
 * @returns 呼出し元が使用する値です。
 */
export function input(value = '', type = 'text')
{
    const control = document.createElement('input');
    control.type = type;
    control.value = value;
    return control;
}

/** 複数行の文字列を編集する入力欄を作成します。
 * @returns 呼出し元が使用する値です。
 */
export function textarea(value = '', rows = 8)
{
    const control = document.createElement('textarea');
    control.value = value;
    control.rows = rows;
    control.spellcheck = false;
    return control;
}

/** 対象と選択状態を照合して切り替えます。
 * @returns 呼出し元が使用する値です。
 */
export function select(items, value = '')
{
    const control = document.createElement('select');
    for (const [key, label] of items)
    {
        const option = node('option', label);
        option.value = key;
        control.append(option);
    }
    control.value = value;
    return control;
}

/** 真偽値を切り替える選択欄を作成します。
 * @returns root・controlを含む結果です。
 */
export function checkbox(label, checked = false)
{
    const control = input('', 'checkbox');
    control.checked = checked;
    const root = node('label', '', 'hm-check');
    root.append(control, document.createTextNode(label));
    return { root, control };
}

/** 列と行を一覧表示する表を作成します。
 * @returns root・body・addを含む結果です。
 */
export function table(headings)
{
    const root = node('div', '', 'hm-table-wrap'), table = node('table'), head = node('thead'), row = node('tr'), body = node('tbody');
    headings.forEach(heading =>
    {
        const cell = node('th', heading);
        cell.scope = 'col';
        row.append(cell);
    });
    head.append(row);
    table.append(head, body);
    root.append(table);
    return { root, body,

        /** 新しい項目を現在の集合へ追加します。
         * @returns 呼出し元が使用する値です。
         */
        add(values)
        {
            const row = node('tr');
            values.forEach(value =>
            {
                const cell = node('td');
                cell.append(value instanceof Node ? value : document.createTextNode(String(value ?? '—')));
                row.append(cell);
            });
            body.append(row);
            return row;
        } };
}

/** 折り畳み可能な詳細表示を構成します。
 * @returns 呼出し元が使用する値です。
 */
export function details(label, content)
{
    const root = node('details');
    root.append(node('summary', label), content);
    return root;
}

/** 入力された項目を値の一覧へ変換します。
 * @returns 呼出し元が使用する値です。
 */
export function values(text)
{
    return text.split(/[\s,;]+/).map(value => value.trim()).filter(Boolean);
}

/** 入力値が許容範囲の整数か検証します。
 * @returns 呼出し元が使用する値です。
 */
export function integer(text, min, max)
{
    const value = Number(text);
    if (!/^\d+$/.test(text) || !Number.isSafeInteger(value) || value < min || (max) < (value))
    {
        throw new Error(`${min}–${max}`);
    }
    return value;
}

/** 比較する値が同一の内容かを判定します。
 * @returns 呼出し元が使用する判定結果です。
 */
export function same(left, right)
{
    return JSON.stringify(left) === JSON.stringify(right);
}

/** 指定名に対応するアイコン要素を作成します。
 * @returns 呼出し元が使用する値です。
 */
export function icon(name)
{
    return uiIcon(name);
}

/** 表示名とイベントを持つ操作ボタンを構成します。
 * @returns 呼出し元が使用する値です。
 */
export function action(ctx, label, glyph, run, primary = false)
{
    const button = ctx.button(label, run, primary ? 'hm-button primary' : 'hm-button');
    button.prepend(icon(glyph));
    return button;
}

/** 状態を短いラベルとして表示します。
 * @returns 呼出し元が使用する値です。
 */
export function badge(text, tone = '')
{
    return node('span', text, `hm-badge ${tone}`);
}

/** 項目が存在しない状態を表示します。
 * @returns 呼出し元が使用する値です。
 */
export function empty(title, description, glyph = 'monitor')
{
    const root = node('div', '', 'hm-empty');
    root.append(icon(glyph), node('h3', title));
    if (description)
    {
        root.append(node('p', description));
    }
    return root;
}

/** 画面の見出しを作成します。
 * @returns 呼出し元が使用する値です。
 */
export function heading(title, description = '')
{
    const root = node('div', '', 'hm-heading'), copy = node('div');
    copy.append(node('h2', title));
    if (description)
    {
        copy.append(node('p', description, 'hm-muted'));
    }
    root.append(copy);
    return root;
}

/** 関連する説明と操作を一つの表示領域へまとめます。
 * @returns 呼出し元が使用する値です。
 */
export function section(title, description = '')
{
    const root = node('section', '', 'hm-section');
    root.append(node('h3', title));
    if (description)
    {
        root.append(node('p', description, 'hm-muted'));
    }
    return root;
}

/** 候補を切り替える選択ボタンを構成します。
 * @returns 呼出し元が使用する値です。
 */
export function segmented(items, value, change, label)
{
    const root = node('div', '', 'hm-segmented');
    root.setAttribute('role', 'group');
    root.setAttribute('aria-label', label);
    root.selectValue = next =>
    {
        value = next;
        for (const item of root.children)
        {
            item.setAttribute('aria-pressed', String(item.dataset.value === value));
        }
    };
    for (const [key, caption] of items)
    {
        const button = node('button', caption);
        button.type = 'button';
        button.dataset.value = key;
        button.setAttribute('aria-pressed', String(key === value));
        button.onclick = () =>
        {
            if (key === value)
            {
                return;
            }
            root.selectValue(key);
            change(key);
        };
        root.append(button);
    }
    return root;
}
