/** DOMの親子関係と入力操作を試験用に再現します。 */
export class DomNode
{
    /** 試験で操作する状態とブラウザー境界を初期化します。 */
    constructor(tag = '#text', text = '')
    {
        this.tag = tag;
        this.childNodes = [];
        this.parentElement = null;
        this.dataset = {};
        this.attributes = {};
        this.style = {};
        this.value = '';
        this.disabled = false;
        this.hidden = false;
        this.events = {};
        this.className = '';
        this.text = text;
    }

    /** 試験用DOMの子要素を返します。
     * @returns 呼出し元が使用する値です。
     */
    get children()
    {
        return this.childNodes.filter(item => item.tag !== '#text');
    }

    /** 受け取った項目を保持する順序へ追加します。 */
    append(...items)
    {
        for (let item of items)
        {
            if (typeof item === 'string')
            {
                item = new DomNode('#text', item);
            }
            item.remove();
            item.parentElement = this;
            this.childNodes.push(item);
        }
    }

    /** 子要素を表示順の先頭へ追加します。 */
    prepend(...items)
    {
        const previous = [...this.childNodes];
        this.childNodes = [];
        this.append(...items, ...previous);
    }

    /** 現在の子要素を指定された内容へ置き換えます。 */
    replaceChildren(...items)
    {
        for (const item of this.childNodes)
        {
            item.parentElement = null;
        }
        this.childNodes = [];
        this.text = '';
        this.append(...items);
    }

    /** 親との関係を解除して試験用DOMから取り除きます。 */
    remove()
    {
        if (this.parentElement)
        {
            this.parentElement.childNodes = this.parentElement.childNodes.filter(item => item !== this);
        }
        this.parentElement = null;
    }

    /** 試験用DOMの文字列内容を読み書きします。 */
    set textContent(value)
    {
        this.replaceChildren();
        this.text = String(value ?? '');
    }

    /** 試験用DOMの文字列内容を読み書きします。
     * @returns 呼出し元が使用する値です。
     */
    get textContent()
    {
        return this.text + this.childNodes.map(item => item.textContent).join('');
    }

    /** 属性の値を試験用DOMへ記録します。 */
    setAttribute(name, value)
    {
        this.attributes[name] = String(value);
        if (name === 'class')
        {
            this.className = String(value);
        }
    }

    /** 試験用DOMに保持された属性値を読み取ります。
     * @returns 呼出し元が使用する値です。
     */
    getAttribute(name)
    {
        return name === 'class' ? this.className : this.attributes[name] ?? null;
    }

    /** 試験用DOMから指定の属性を取り除きます。 */
    removeAttribute(name)
    {
        delete this.attributes[name];
    }

    /** 試験用DOMのクラス属性に対応する操作を返します。
     * @returns addを含む結果です。
     */
    get classList()
    {
        return { add: (...names) =>
        {
            this.className = [...new Set([...this.className.split(' ').filter(Boolean), ...names])].join(' ');
        } };
    }

    /** 先頭の子要素を返します。
     * @returns 呼出し元が使用する値です。
     */
    get firstElementChild()
    {
        return this.children[0] ?? null;
    }

    /** 末尾の子要素を返します。
     * @returns 呼出し元が使用する値です。
     */
    get lastElementChild()
    {
        return this.children.at(-1) ?? null;
    }

    /** 試験用DOMに接続されているかを返します。
     * @returns 呼出し元が使用する判定結果です。
     */
    get isConnected()
    {
        return this.connected === true || this.parentElement?.isConnected === true;
    }

    /** 指定した要素が自分または子孫かを判定します。
     * @returns 呼出し元が使用する値です。
     */
    contains(value)
    {
        return this === value || this.childNodes.some(item => item.contains(value));
    }

    /** 試験で呼び出せるイベントハンドラーを登録します。 */
    addEventListener(name, callback)
    {
        (this.events[name] ??= []).push(callback);
    }

    /** 試験で指定したイベントを発火させます。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async fire(name, event = {})
    {
        await this['on' + name]?.(event);
        for (const callback of this.events[name] ?? [])
        {
            await callback(event);
        }
    }

    /** クリック対象を確認して選択または操作を行います。
     * @returns 呼出し元が使用する値です。
     */
    click()
    {
        if (!this.disabled)
        {
            return this.fire('click');
        }
    }

    /** 操作対象の入力欄へフォーカスを移します。 */
    focus()
    {
        globalThis.document.activeElement = this;
    }

    /** 試験用ダイアログを開いた状態にします。 */
    showModal()
    {
        this.open = true;
        this.focus();
    }

    /** 対象を閉じ、関連する状態を整理します。 */
    close()
    {
        this.open = false;
    }

    /** 試験用DOMが指定した選択条件に一致するか判定します。
     * @returns 呼出し元が使用する値です。
     */
    matches(selector)
    {
        if (selector.includes(':not(:disabled)') && this.disabled)
        {
            return false;
        }
        selector = selector.replace(':not(:disabled)', '');
        if (selector.startsWith('.'))
        {
            return this.className.split(' ').includes(selector.slice(1));
        }
        const match = selector.match(/^([^[]*)\[([^\]]+)\]$/);
        if (match)
        {
            const property = match[2].startsWith('data-') ? this.dataset[match[2].slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase())] : this.getAttribute(match[2]);
            return (!match[1] || this.tag === match[1]) && property != null;
        }
        return this.tag === selector;
    }

    /** 条件に一致する試験用DOM要素を列挙します。
     * @returns 呼出し元が使用する値です。
     */
    querySelectorAll(selector)
    {
        return descendants(this).slice(1).filter(item => selector.split(',').some(part => item.matches(part.trim())));
    }

    /** 条件に一致する試験用DOM要素を取得します。
     * @returns 呼出し元が使用する値です。
     */
    querySelector(selector)
    {
        return this.querySelectorAll(selector)[0] ?? null;
    }
}

/** 自分と配下の試験用要素を列挙します。
 * @returns 呼出し元が使用する値です。
 */
export const descendants = root => [root, ...root.childNodes.flatMap(descendants)];

/** 試験対象のDOMとブラウザー境界を構成します。
 * @returns 呼出し元が使用する値です。
 */
export function createDocument()
{
    const head = new DomNode('head'), body = new DomNode('body');
    head.connected = body.connected = true;
    return { head, body, activeElement: body,

        createElement: tag => new DomNode(tag),

        createElementNS: (_, tag) => new DomNode(tag),

        createTextNode: text => new DomNode('#text', text),

        querySelector: selector => head.querySelector(selector) ?? body.querySelector(selector) };
}
