// Every animation uses epoch zero on the document timeline, including newly mounted cards.
/** ホストの電源操作を確認と取消しの寿命へ接続します。
 * @returns update・disposeを含む結果です。
 */
export function attachHostPowerControl(root, receiver)
{
    const doc = root.ownerDocument, win = doc.defaultView, events = new AbortController();
    const trigger = root.querySelector('[data-power-trigger]'), panel = root.querySelector('.power-panel');
    const choices = root.querySelector('[data-power-choices]'), confirmation = root.querySelector('[data-power-confirmation]');
    const actions = [...root.querySelectorAll('[data-power-action]')], confirms = [...root.querySelectorAll('[data-power-confirm]')];
    const motion = win.matchMedia('(prefers-reduced-motion: reduce)');
    let current = null, selected = null, hovered = false, timer = null, submitting = false, disposed = false, animation = null, animatedElement = null, animationKind = null;

    /** イベントの購読と対応する後処理を登録します。
     * @returns 呼出し元が使用する値です。
     */
    const listen = (target, name, handler) => target.addEventListener(name, handler, { signal: events.signal });

    /** 現在の条件で操作が許可されるか確認します。
     * @returns 呼出し元が使用する値です。
     */
    const allowed = action => current && (current.offline ? ['remove', 'wake'].includes(action) : ['Ready', 'Syncing'].includes(current.status) && ['restart', 'shutdown'].includes(action));

    /** 所有するタイマーを停止します。 */
    function cancelTimer()
    {
        if (timer !== null)
        {
            win.clearTimeout(timer);
            timer = null;
        }
    }

    /** 対象を閉じ、関連する状態を整理します。 */
    function close(restore = false)
    {
        cancelTimer();
        panel.hidden = true;
        selected = null;
        choices.hidden = false;
        confirmation.hidden = true;
        trigger.setAttribute('aria-expanded', 'false');
        if (restore)
        {
            trigger.focus();
        }
    }

    /** 有効期限を過ぎた操作状態を解除します。 */
    function expire()
    {
        cancelTimer();
        if (!hovered && !panel.hidden)
        {
            timer = win.setTimeout(() => close(panel.contains(doc.activeElement)), 10000);
        }
    }

    /** 指定した対象を開き、表示状態を更新します。 */
    function open()
    {
        if (!current || current.busy || submitting || !actions.some(b => allowed(b.dataset.powerAction)))
        {
            return;
        }
        panel.hidden = false;
        trigger.setAttribute('aria-expanded', 'true');
        expire();
    }

    /** 現在の状態に合わせてアニメーションを更新します。 */
    function syncAnimation()
    {
        const expectedReady = 'ready';

        const element = root.querySelector('[data-power-animation]'), kind = element?.dataset.powerAnimation;
        if (element === animatedElement && kind === animationKind && !!animation === (!motion.matches && kind === expectedReady))
        {
            return;
        }
        animation?.cancel();
        animation = null;
        animatedElement = element;
        animationKind = kind;
        if (!element || motion.matches)
        {
            return;
        }
        if (kind === expectedReady)
        {
            animation = element.animate([{ opacity: .84, filter: 'drop-shadow(0 0 0px transparent)' }, { opacity: .94, filter: 'drop-shadow(0 0 1.3px color-mix(in srgb, currentColor 25%, transparent))' }, { opacity: .84, filter: 'drop-shadow(0 0 0px transparent)' }], { duration: 4200, iterations: Infinity, easing: 'ease-in-out' });
        }
        if (animation)
        {
            animation.startTime = 0;
        }
    }

    listen(root, 'pointerenter', () =>
    {
        hovered = true;
        cancelTimer();
    });
    listen(root, 'pointerleave', () =>
    {
        hovered = false;
        expire();
    });
    listen(trigger, 'click', () =>
    {
        if (panel.hidden)
        {
            open();
        }
        else
        {
            close();
        }
    });
    for (const button of actions)
    {
        listen(button, 'click', () =>
        {
            const action = button.dataset.powerAction;
            if (current.busy || submitting || !allowed(action))
            {
                return;
            }
            selected = action;
            choices.hidden = true;
            confirmation.hidden = false;
            for (const confirm of confirms)
            {
                confirm.hidden = confirm.dataset.powerConfirm !== action;
            }
            confirms.find(b => b.dataset.powerConfirm === action)?.focus();
            expire();
        });
    }
    for (const button of confirms)
    {
        listen(button, 'click', () =>
        {
            const action = button.dataset.powerConfirm;
            if (panel.hidden || selected !== action || current.busy || submitting || !allowed(action))
            {
                return;
            }
            const { hostId, targetIdentity } = current;
            submitting = true;
            close(true);
            Promise.resolve().then(() => receiver.invokeMethodAsync('ConfirmAsync', action, hostId, targetIdentity)).catch(() =>
            {}).finally(() =>
            {
                submitting = false;
                if (!disposed)
                {
                    for (const button of [...actions, ...confirms])
                    {
                        button.disabled = !!current?.busy;
                    }
                }
            });
        });
    }
    listen(doc, 'pointerdown', e =>
    {
        if (!root.contains(e.target))
        {
            close();
        }
    });
    listen(root, 'keydown', e =>
    {
        const expectedEscape = 'Escape';
        const expectedHome = 'Home';
        const expectedEnd = 'End';
        const expectedArrowup = 'ArrowUp';

        if (e.key === expectedEscape && !panel.hidden)
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
        open();
        if (panel.hidden)
        {
            return;
        }
        e.preventDefault();
        const visible = selected ? confirms.filter(b => !b.hidden) : actions.filter(b => allowed(b.dataset.powerAction));
        const index = visible.indexOf(doc.activeElement);
        const next = e.key === expectedHome ? 0 : e.key === expectedEnd ? visible.length - 1 : e.key === expectedArrowup ? (index < 0 ? visible.length - 1 : (index + visible.length - 1) % visible.length) : (index + 1) % visible.length;
        visible[next]?.focus();
    });
    listen(root, 'focusout', e =>
    {
        if (!root.contains(e.relatedTarget))
        {
            expire();
        }
    });
    listen(motion, 'change', syncAnimation);
    return {
        /** 現在の設定と状態から表示を更新します。 */
        update(value)
        {
            if (disposed)
            {
                return;
            }
            if (!current || current.hostId !== value.hostId || current.targetIdentity !== value.targetIdentity || current.status !== value.status || current.offline !== value.offline || current.stale !== value.stale)
            {
                close();
            }
            current = value;
            for (const button of [...actions, ...confirms])
            {
                button.disabled = value.busy || submitting;
            }
            if (value.busy)
            {
                close();
            }
            syncAnimation();
        },

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            disposed = true;
            events.abort();
            close();
            animation?.cancel();
        }
    };
}
