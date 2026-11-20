// Fit xterm inside a padding-free content box. The surrounding panel owns its
// border/padding; FitAddon must never count these as usable terminal rows.
/** ターミナルの表示寸法とリサイズ通知を管理します。
 * @returns update・closeを含む結果です。
 */
export function createTerminalLayout(root, host, fit, signal)
{
    let frame = 0, disposed = false;
    const compact = window.matchMedia('(max-width: 650px), (max-height: 650px)');

    /** 現在の設定と状態から表示を更新します。 */
    function update()
    {
        frame = 0;
        if (disposed || signal.aborted)
        {
            return;
        }
        const statusbar = document.querySelector('.app-statusbar'), fixed = 'fixed';
        const footer = statusbar && getComputedStyle(statusbar).position === fixed ? statusbar.getBoundingClientRect().height : 0;
        const viewport = globalThis.visualViewport?.height ?? innerHeight;
        const height = document.fullscreenElement === root ? Math.floor(viewport) : Math.max(240, Math.floor(viewport - root.getBoundingClientRect().top - window.scrollY - footer - 12));
        root.style.height = compact.matches && document.fullscreenElement !== root ? '' : height + 'px';
        fit();
    }

    /** 重複した描画要求を次の一回にまとめます。 */
    function schedule()
    {
        if (!frame && !disposed)
        {
            frame = requestAnimationFrame(update);
        }
    }

    const observer = new ResizeObserver(schedule);
    observer.observe(root.parentElement);
    observer.observe(host);
    window.addEventListener('resize', schedule, { signal });
    document.addEventListener('fullscreenchange', schedule, { signal });
    globalThis.visualViewport?.addEventListener('resize', schedule, { signal });
    schedule();
    return { update: schedule,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {
            disposed = true;
            cancelAnimationFrame(frame);
            observer.disconnect();
        } };
}
