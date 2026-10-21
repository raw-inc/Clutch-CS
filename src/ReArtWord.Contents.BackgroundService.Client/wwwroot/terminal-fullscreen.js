// Own only Terminal's fullscreen state; leaving it never closes the shared shell.
/** ターミナルの全画面表示と元の表示への復帰を構成します。
 * @returns buttons・update・exit・disposeを含む結果です。
 */
export function createTerminalFullscreen(root, screen, ctx, options)
{
    let disposed = false, revision = 0;

    /** 保存キーがこの領域の所有範囲かを判定します。
     * @returns 呼出し元が使用する判定結果です。
     */
    const owns = () => document.fullscreenElement === root || document.fullscreenElement === screen;

    /** 現在の表示または操作モードを終了します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function exitAsync()
    {
        revision++;
        if (owns())
        {
            await document.exitFullscreen();
        }
    }

    /** 選択された表示または操作モードへ入ります。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function enterAsync(target)
    {
        if (!options.active())
        {
            return;
        }
        if (document.fullscreenElement === target)
        {
            await exitAsync();
            return;
        }
        const run = ++revision;

        // Do not stack the two modes: native Escape must return to the normal page.
        if (owns())
        {
            await document.exitFullscreen();
        }
        if (disposed || run !== revision || !options.active())
        {
            return;
        }
        await target.requestFullscreen();
        if (disposed || run !== revision || !options.active())
        {
            if (document.fullscreenElement === target)
            {
                await document.exitFullscreen();
            }
            return;
        }
        options.focus();
        options.fit();
    }

    const full = ctx.button(ctx.t('Fullscreen', '全画面表示'), () => enterAsync(root));
    const content = ctx.button(ctx.t('Terminal fullscreen', '端末のみ全画面'), () => enterAsync(screen));
    for (const [button, icon] of [[full, 'fullscreen'], [content, 'video-fullscreen']])
    {
        button.className = 'terminal-action';
        button.prepend(ctx.icon(icon));
    }
    full.className += ' terminal-fullscreen';

    /** 現在の設定と状態から表示を更新します。 */
    function update()
    {
        full.disabled = content.disabled = !options.active() || typeof root.requestFullscreen !== 'function';
        full.setAttribute('aria-pressed', String(document.fullscreenElement === root));
        content.setAttribute('aria-pressed', String(document.fullscreenElement === screen));
        const label = document.fullscreenElement === root ? ctx.t('Exit fullscreen', '全画面表示を終了') : ctx.t('Fullscreen', '全画面表示');
        full.title = label;
        full.setAttribute('aria-label', label);
        const contentLabel = ctx.t('Terminal fullscreen', '端末のみ全画面');
        content.title = contentLabel + ' · Esc';
        content.setAttribute('aria-label', contentLabel);
    }

    document.addEventListener('fullscreenchange', () =>
    {
        update();
        options.fit();
        if (owns())
        {
            options.focus();
        }
    }, { signal: ctx.signal });
    document.addEventListener('keydown', event =>
    {
        const expectedEscape = 'Escape';

        if (event.key === expectedEscape && owns())
        {
            event.preventDefault();
            event.stopPropagation();
            void exitAsync().catch(() =>
            {});
        }
    }, { capture: true, signal: ctx.signal });
    update();
    return { buttons: [full, content], update, exit: exitAsync,

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            disposed = true;
            void exitAsync().catch(() =>
            {});
        } };
}
