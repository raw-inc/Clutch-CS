// Keep pointer/focus state local to the menu; choosing a language still uses Blazor's persisted UI preference.
/** 言語メニューの入力操作と表示寿命を接続します。
 * @returns disposeを含む結果です。
 */
export function attachLanguageMenu(root)
{
    const trigger = root.querySelector('[data-language-trigger]');
    const menu = root.querySelector('[role="menu"]');
    const items = [...menu.querySelectorAll('[role="menuitemradio"]')];
    const doc = root.ownerDocument;
    const events = new AbortController();
    let pinned = false;

    /** イベントの購読と対応する後処理を登録します。
     * @returns 呼出し元が使用する値です。
     */
    const listen = (target, name, handler) => target.addEventListener(name, handler, { signal: events.signal });

    /** 対象を表示し、現在の操作位置へ配置します。 */
    function show()
    {
        menu.hidden = false;
        trigger.setAttribute('aria-expanded', 'true');
    }

    /** 対象を閉じ、関連する状態を整理します。 */
    function close(restoreFocus = false)
    {
        menu.hidden = true;
        pinned = false;
        trigger.setAttribute('aria-expanded', 'false');
        if (restoreFocus)
        {
            trigger.focus();
        }
    }

    listen(root, 'pointerenter', e =>
    {
        const expectedMouse = 'mouse';

        if (e.pointerType === expectedMouse)
        {
            show();
        }
    });
    listen(root, 'pointerleave', () =>
    {
        if (!pinned && !menu.contains(doc.activeElement))
        {
            close();
        }
    });
    listen(trigger, 'click', () =>
    {
        if (!menu.hidden && pinned)
        {
            close();
        }
        else
        {
            pinned = true;
            show();
        }
    });
    listen(menu, 'click', e =>
    {
        if (items.some(item => item.contains(e.target)))
        {
            close(true);
        }
    });
    listen(doc, 'pointerdown', e =>
    {
        if (!root.contains(e.target))
        {
            close();
        }
    });
    listen(root, 'focusout', e =>
    {
        if (!root.contains(e.relatedTarget))
        {
            close();
        }
    });
    listen(root, 'keydown', e =>
    {
        const expectedEscape = 'Escape';
        const expectedArrowup = 'ArrowUp';
        const expectedHome = 'Home';
        const expectedEnd = 'End';

        if (e.key === expectedEscape && !menu.hidden)
        {
            e.preventDefault();
            e.stopPropagation();
            close(true);
            return;
        }
        if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(e.key))
        {
            return;
        }
        e.preventDefault();
        show();
        pinned = true;
        const current = items.indexOf(doc.activeElement);
        let next = e.key === expectedArrowup ? (current < 0 ? items.length - 1 : (current + items.length - 1) % items.length) : (current + 1) % items.length;
        if (e.key === expectedHome)
        {
            next = 0;
        }
        if (e.key === expectedEnd)
        {
            next = items.length - 1;
        }
        items[next].focus();
    });
    return {
        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            events.abort();
            close();
        } };
}
