// Focus a real editing control synchronously from the user's tap. VirtualKeyboard
// is optional; focusing/blurring the textarea also works without that API.
/** 画面内のキーボード入力を送信・解除の操作へ接続します。
 * @returns 呼出し元が使用する値です。
 */
export function createDesktopKeyboard(viewport, button, ctx, options)
{
    const expectedDeletecontentbackward = 'deleteContentBackward';
    const expectedDeletecontentforward = 'deleteContentForward';
    const expectedInsertlinebreak = 'insertLineBreak';
    const expectedInsertparagraph = 'insertParagraph';

    const input = document.createElement('textarea');
    input.className = 'desktop-text-input';
    input.tabIndex = -1;
    input.inputMode = 'text';
    input.spellcheck = false;
    input.setAttribute('aria-label', ctx.t('Remote text input', 'リモートへの文字入力'));
    for (const [key, value] of Object.entries({ autocomplete: 'off', autocapitalize: 'off', autocorrect: 'off', enterkeyhint: 'enter' }))
    {
        input.setAttribute(key, value);
    }
    input.hidden = true;
    viewport.append(input);
    let active = false, composing = false, lastCommit = null, frame = 0;
    const marker = '\u200b', pending = [];

    /** 一時状態を初期値へ戻します。 */
    function reset()
    {
        input.value = marker;
        input.setSelectionRange?.(1, 1);
    }

    /** 入力欄が保持している本文を読み取ります。
     * @returns 呼出し元が使用する値です。
     */
    function contents()
    {
        return input.value.startsWith(marker) ? input.value.slice(1) : input.value;
    }

    /** 操作に対応する説明文を更新します。 */
    function label()
    {
        const value = active ? ctx.t('Hide software keyboard', 'ソフトウェアキーボードを隠す') : ctx.t('Show software keyboard', 'ソフトウェアキーボードを表示');
        button.setAttribute('aria-label', value);
        button.title = value;
        button.setAttribute('aria-pressed', String(active));
    }

    /** 待機している項目を順序に従って処理します。 */
    function drain()
    {
        frame = 0;
        if (!active || !options.available())
        {
            pending.length = 0;
            return;
        }

        // Bound the burst into the existing ordered input stream. Never replay
        // queued text into a different connection or after hiding the keyboard.
        for (let count = 0; count < 32 && pending.length && options.available(); count++)
        {
            const item = pending.shift();
            if (item.key)
            {
                options.key(item.key);
            }
            else
            {
                options.text(item.code);
            }
        }
        if (pending.length && active && options.available())
        {
            frame = requestAnimationFrame(drain);
        }
    }

    /** 送信または処理する項目を待機列へ追加します。 */
    function enqueue(items)
    {
        if (!active || !options.available())
        {
            return;
        }
        if ((4096) < (items.length + pending.length))
        {
            ctx.say(ctx.t('Text input is too large. Send a smaller selection.', '入力が大きすぎます。短い範囲に分けて入力してください。'));
            return;
        }
        pending.push(...items);
        if (!frame)
        {
            drain();
        }
    }

    /** 入力された文字列を対象の処理へ渡します。 */
    function text(value)
    {
        if ((4096) < (value.length))
        {
            ctx.say(ctx.t('Text input is too large. Send a smaller selection.', '入力が大きすぎます。短い範囲に分けて入力してください。'));
            return;
        }
        enqueue(Array.from(value.replace(/\r\n?/g, '\n'), symbol =>
        {
            const expectedValue = '\n';
            const expectedValue2 = '\t';

            if (symbol === expectedValue || symbol === expectedValue2)
            {
                const expectedValue3 = '\n';

                return { key: symbol === expectedValue3 ? 13 : 9 };
            }
            const code = symbol.codePointAt(0);
            return { code: code === 0 || (0xd800) <= (code) && code <= 0xdfff ? 0xfffd : code };
        }));
    }

    /** 操作対象の入力欄へフォーカスを移します。 */
    function focus()
    {
        if (!active)
        {
            return;
        }
        input.focus({ preventScroll: true });
        try
        {
            globalThis.navigator?.virtualKeyboard?.show();
        }
        catch
        { /* 追加APIが拒否されても、フォーカスした通常の入力欄を使用できます。 */ }
    }

    /** 一時表示を非表示へ切り替えます。 */
    function hide()
    {
        active = false;
        composing = false;
        lastCommit = null;
        pending.length = 0;
        cancelAnimationFrame(frame);
        frame = 0;
        input.blur();
        input.hidden = true;
        input.value = '';
        label();
        try
        {
            globalThis.navigator?.virtualKeyboard?.hide();
        }
        catch
        { /* 追加APIが利用できなくても、入力欄のフォーカス解除で操作を終了します。 */ }
    }

    /** 進行中の入力を終了して保持状態を解除します。 */
    function interrupt()
    {
        if (!pending.length)
        {
            return;
        }
        pending.length = 0;
        cancelAnimationFrame(frame);
        frame = 0;
        ctx.say(ctx.t('Input changed before all text was sent. Remaining text was cancelled.', '操作を切り替えたため、まだ送信していない文字の入力を取り消しました。'));
    }

    /** 選択値を反転して表示へ反映します。 */
    function toggle()
    {
        if (active)
        {
            hide();
            options.release();
            return;
        }
        if (!options.available())
        {
            return;
        }
        active = true;
        input.hidden = false;
        reset();
        label();
        focus();
    }

    button.addEventListener('pointerdown', event =>
    {
        if (active)
        {
            event.preventDefault();
        }
    }, { signal: ctx.signal });
    input.addEventListener('compositionstart', () =>
    {
        composing = true;
        lastCommit = null;
    }, { signal: ctx.signal });
    input.addEventListener('compositionend', event =>
    {
        if (!active)
        {
            return;
        }
        composing = false;
        const value = event.data ?? contents();
        text(value);
        reset();
        lastCommit = value;
        queueMicrotask(() =>
        {
            lastCommit = null;
        });
    }, { signal: ctx.signal });

    /** 編集キーを現在の入力操作へ反映します。
     * @returns 呼出し元が使用する値です。
     */
    const editKey = type => type === expectedDeletecontentbackward ? 8 : type === expectedDeletecontentforward ? 46 : type === expectedInsertlinebreak || type === expectedInsertparagraph ? 13 : 0;

    input.addEventListener('beforeinput', event =>
    {
        if (composing || event.isComposing || !event.cancelable)
        {
            return;
        }
        const key = editKey(event.inputType);
        if (key)
        {
            event.preventDefault();
            enqueue([{ key }]);
            reset();
        }
    }, { signal: ctx.signal });
    input.addEventListener('input', event =>
    {
        if (composing || event.isComposing)
        {
            return;
        }
        const value = contents(), key = editKey(event.inputType);
        if (key)
        {
            enqueue([{ key }]);
        }
        else if (value && value !== lastCommit)
        {
            text(value);
        }
        reset();
    }, { signal: ctx.signal });
    const controls = { Enter: 13, Backspace: 8, Delete: 46, Tab: 9, Escape: 27, ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40, Home: 36, End: 35, PageUp: 33, PageDown: 34 };
    input.addEventListener('keydown', event =>
    {
        event.stopPropagation();
        if (composing || event.isComposing || event.keyCode === 229)
        {
            return;
        }
        if (options.physical?.(event, true))
        {
            return;
        }
        const key = controls[event.key];
        if (key)
        {
            event.preventDefault();
            enqueue([{ key }]);
            reset();
        }
    }, { signal: ctx.signal });
    input.addEventListener('keyup', event =>
    {
        event.stopPropagation();
        if (!composing && !event.isComposing)
        {
            options.physical?.(event, false);
        }
    }, { signal: ctx.signal });
    input.addEventListener('blur', event =>
    {
        if (active && !options.owns(event.relatedTarget))
        {
            hide();
            options.release();
        }
    }, { signal: ctx.signal });
    ctx.signal.addEventListener('abort', hide, { once: true });
    label();
    return { input,

        get active()
        {
            return active;
        },

        focus, hide, toggle, interrupt,

        /** 現在の設定と状態から表示を更新します。 */
        update()
        {
            button.disabled = !options.available();
            if (button.disabled && active)
            {
                hide();
            }
        } };
}
