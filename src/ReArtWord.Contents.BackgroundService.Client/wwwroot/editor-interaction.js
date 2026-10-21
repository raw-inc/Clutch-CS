// DOM events and viewport geometry only; catalog decisions remain in the component.
/** ノード追加メニューのキー操作と表示寿命を接続します。
 * @returns setEnabled・show・close・disposeを含む結果です。
 */
export function attachQuickAdd(host, receiver)
{
    let enabled = true, disposed = false, opening = false;
    let popup = null, previousFocus = null, pointer = null;
    const hadTabIndex = host.hasAttribute('tabindex');
    if (!hadTabIndex)
    {
        host.setAttribute('tabindex', '0');
    }

    /** 現在の状態に対応する呼出し先へ操作を渡します。
     * @returns 呼出し元が使用する値です。
     */
    const invoke = (name, ...args) => receiver.invokeMethodAsync(name, ...args).catch(() =>
    {});

    /** 要素が通常の文字入力を優先する対象か判定します。
     * @returns 呼出し元が使用する値です。
     */
    const editable = target => target instanceof Element && target.closest('input,textarea,select,[contenteditable]:not([contenteditable="false"]),dialog,[role="dialog"]');

    /** ポインター位置を対象の操作へ反映します。 */
    const onPointer = event =>
    {
        pointer = { x: event.clientX, y: event.clientY };
    };

    /** ポインター操作の開始位置と対象を確認します。 */
    const onPointerDown = event =>
    {
        if (!editable(event.target) && event.target instanceof Element && !event.target.closest('button,a,[role="button"]'))
        {
            host.focus({ preventScroll: true });
        }
    };

    /** 追加メニュー内での確定・移動・終了キーを処理します。 */
    function handlePopupKey(event)
    {
        const expectedEscape = 'Escape';
        const expectedTab = 'Tab';
        const expectedEnter = 'Enter';
        const expectedArrowup = 'ArrowUp';
        const expectedArrowdown = 'ArrowDown';
        const expectedArrowleft = 'ArrowLeft';
        const expectedArrowright = 'ArrowRight';

        if (!popup.contains(event.target))
        {
            return;
        }
        if (event.key === expectedEscape || event.key === expectedTab)
        {
            event.preventDefault();
            event.stopPropagation();
            invoke('HandleKeyAsync', event.key, popup.querySelector('input')?.value ?? '');
            return;
        }
        const input = event.target instanceof HTMLInputElement;
        if ((event.key === expectedEnter && (input || event.target.closest('[role="option"]'))) || event.key === expectedArrowup || event.key === expectedArrowdown ||
                ((event.key === expectedArrowleft || event.key === expectedArrowright) && (!input || event.target.value.length === 0)))
        {
            event.preventDefault();
            event.stopPropagation();
            invoke('HandleKeyAsync', event.key, popup.querySelector('input')?.value ?? '');
        }
        return;
    }

    /** 押されたキーを画面操作へ振り分けます。 */
    const onKey = event =>
    {
        const expectedTab2 = 'Tab';
        const expectedKeya = 'KeyA';

        if (disposed || event.isComposing)
        {
            return;
        }
        if (popup)
        {
            handlePopupKey(event);
            return;
        }
        if (!enabled || opening || !host.contains(event.target) || editable(event.target) || event.ctrlKey || event.metaKey || event.altKey)
        {
            return;
        }
        if (!(event.key === expectedTab2 && !event.shiftKey) && !(event.code === expectedKeya && event.shiftKey))
        {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        previousFocus = document.activeElement;
        const rect = host.getBoundingClientRect();
        const location = pointer ?? { x: (Math.max(0, rect.left) + Math.min(innerWidth, rect.right)) / 2, y: (Math.max(0, rect.top) + Math.min(innerHeight, rect.bottom)) / 2 };
        opening = true;
        invoke('OpenAtAsync', location.x, location.y).finally(() =>
        {
            opening = false;
        });
    };

    /** 表示領域外の操作を確認してメニューを閉じます。 */
    const onOutside = event =>
    {
        if (popup && !popup.contains(event.target))
        {
            previousFocus = null;
            invoke('CloseAsync');
        }
    };

    /** 表示領域の変更に合わせて配置を再計算します。 */
    const onResize = () =>
    {
        if (popup)
        {
            invoke('CloseAsync');
        }
    };

    host.addEventListener('pointermove', onPointer, { passive: true });
    host.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKey, true);
    document.addEventListener('pointerdown', onOutside, true);
    window.addEventListener('resize', onResize);

    /** 対象を閉じ、関連する状態を整理します。 */
    const close = () =>
    {
        popup = null;
        if (previousFocus?.isConnected)
        {
            previousFocus.focus({ preventScroll: true });
        }
        previousFocus = null;
    };

    return {
        /** 操作の有効・無効を状態と表示に反映します。 */
        setEnabled(value)
        {
            enabled = value;
        },

        /** 対象を表示し、現在の操作位置へ配置します。 */
        show(element, input, x, y, focus)
        {
            popup = element;
            const rect = popup.getBoundingClientRect();
            popup.style.left = `${Math.max(8, Math.min(x, innerWidth - rect.width - 8))}px`;
            popup.style.top = `${Math.max(8, Math.min(y, innerHeight - rect.height - 8))}px`;
            if (focus)
            {
                input.focus({ preventScroll: true });
            }
            popup.querySelector('[data-quick-selected="true"]')?.scrollIntoView({ block: 'nearest' });
        },

        close,

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            disposed = true;
            close();
            host.removeEventListener('pointermove', onPointer);
            host.removeEventListener('pointerdown', onPointerDown);
            document.removeEventListener('keydown', onKey, true);
            document.removeEventListener('pointerdown', onOutside, true);
            window.removeEventListener('resize', onResize);
            if (!hadTabIndex)
            {
                host.removeAttribute('tabindex');
            }
        }
    };
}
