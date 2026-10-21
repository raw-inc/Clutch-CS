// VT encodes modifier combinations in each input message; no remote modifier
// is held down. Leave paste and terminal capability responses unchanged.
/** 修飾キーを含む入力を端末用の文字列へ変換します。
 * @returns 呼出し元が使用する値です。
 */
export function terminalModifiedInput(data, modifiers)
{
    const expectedValue = '\t';

    const { ctrl, alt, shift } = modifiers;
    const parameter = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
    if (parameter === 1)
    {
        return data;
    }
    const escapeSequence = data.startsWith('\x1b') ? data.slice(1) : '';
    const cursor = /^(?:\[|O)([ABCDHF])$/.exec(escapeSequence);
    if (cursor)
    {
        return `\x1b[1;${parameter}${cursor[1]}`;
    }
    const navigation = /^\[([356])~$/.exec(escapeSequence);
    if (navigation)
    {
        return `\x1b[${navigation[1]};${parameter}~`;
    }
    if (data === expectedValue && shift)
    {
        return (alt ? '\x1b' : '') + '\x1b[Z';
    }
    if (data.length !== 1)
    {
        return data;
    }
    if (shift && /^[a-z]$/.test(data))
    {
        data = data.toUpperCase();
    }
    if (ctrl)
    {
        const expectedValue2 = ' ';
        const expectedValue3 = '?';
        const expectedValue4 = '\x7f';

        const code = data.toUpperCase().charCodeAt(0);
        switch (true)
        {
            case Boolean(data === expectedValue2):
                {
                    data = '\0';
                }
                break;
            case Boolean(data === expectedValue3):
                {
                    data = '\x7f';
                }
                break;
            case Boolean(data === expectedValue4):
                {
                    data = '\b';
                }
                break;
            case Boolean((64) <= (code) && code <= 95):
                {
                    data = String.fromCharCode(code & 31);
                }
                break;
        }
    }
    return (alt ? '\x1b' : '') + data;
}

/** 修飾キーの状態と画面内キーの送信を構成します。
 * @returns element・refresh・reset・transformを含む結果です。
 */
export function createTerminalKeys(ctx, options)
{
    const element = document.createElement('div');
    element.className = 'terminal-keys';
    element.setAttribute('role', 'toolbar');
    element.setAttribute('aria-label', ctx.t('Terminal keys', 'ターミナル補助キー'));
    const modifiers = { ctrl: false, alt: false, shift: false }, buttons = [], toggles = [];
    const modifierRow = document.createElement('div'), navigationRow = document.createElement('div');
    element.append(modifierRow, navigationRow);

    /** 現在の状態を再確認して表示と操作を更新します。 */
    function refresh()
    {
        const enabled = options.ready();
        if (!enabled)
        {
            Object.keys(modifiers).forEach(key =>
            {
                const assignedValue = modifiers[key] = false;
                return assignedValue;
            });
        }
        for (const button of buttons)
        {
            button.disabled = !enabled;
        }
        for (const [key, button] of toggles)
        {
            button.setAttribute('aria-pressed', String(modifiers[key]));
        }
    }

    /** 一時状態を初期値へ戻します。 */
    function reset()
    {
        Object.keys(modifiers).forEach(key =>
        {
            const assignedValue2 = modifiers[key] = false;
            return assignedValue2;
        });
        refresh();
    }

    /** 入力値を処理先の形式へ変換します。
     * @returns 呼出し元が使用する値です。
     */
    function transform(data)
    {
        if (!options.ready())
        {
            return data;
        }
        return terminalModifiedInput(data, modifiers);
    }

    /** 新しい項目を現在の集合へ追加します。
     * @returns 呼出し元が使用する値です。
     */
    function add(row, label, description, action)
    {
        const button = ctx.button(label, () =>
        {
            if (options.ready())
            {
                action();
                options.focus();
            }
        });
        button.title = description;
        button.setAttribute('aria-label', description);
        button.addEventListener('pointerdown', event => event.preventDefault(), { signal: ctx.signal });
        buttons.push(button);
        row.append(button);
        return button;
    }

    for (const [key, label] of [['ctrl', 'Ctrl'], ['alt', 'Alt'], ['shift', 'Shift']])
    {
        const button = add(modifierRow, label, label + ctx.t(' toggle', '切替'), () =>
        {
            modifiers[key] = !modifiers[key];
            refresh();
        });
        toggles.push([key, button]);
    }
    for (const [label, data, description] of [['Esc', '\x1b', 'Escape'], ['Tab', '\t', 'Tab'], ['Del', '\x1b[3~', 'Delete'], ['⌫', '\x7f', 'Backspace'], ['↵', '\r', 'Enter']])
    {
        add(modifierRow, label, description, () => options.send(transform(data)));
    }
    for (const [label, code, description] of [['←', 'D', 'Left'], ['↑', 'A', 'Up'], ['↓', 'B', 'Down'], ['→', 'C', 'Right'], ['Home', 'H', 'Home'], ['End', 'F', 'End']])
    {
        add(navigationRow, label, description, () => options.send(transform('\x1b' + (options.applicationCursor() ? 'O' : '[') + code)));
    }
    for (const [label, code] of [['PgUp', '5'], ['PgDn', '6']])
    {
        add(navigationRow, label, label, () => options.send(transform(`\x1b[${code}~`)));
    }
    window.addEventListener('blur', reset, { signal: ctx.signal });
    refresh();
    return { element, refresh, reset, transform };
}
