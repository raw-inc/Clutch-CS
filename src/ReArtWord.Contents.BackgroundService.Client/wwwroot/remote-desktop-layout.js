// Size the workspace after page chrome, then fit every displayed monitor at one
// scale. Canvas backing buffers stay at capture resolution for decoding/input.
import { followDesktopPointer } from './remote-desktop-input.js';

/** リモート画面の配置・縮尺・表示範囲を管理します。
 * @returns update・followPointer・disposeを含む結果です。
 */
export function createDesktopLayout(root, viewport, getViews, signal, getScaleMode = () => 'fit')
{
    const insets = new Float64Array(4);

    /** ページのヘッダーとフッターを除いた利用可能な高さを反映します。 */
    function fitWorkspace()
    {
        const fullscreen = document.fullscreenElement === root;
        const bottom = fullscreen ? 0 : (document.querySelector('.app-statusbar')?.getBoundingClientRect().height ?? 0) + 12;
        const top = fullscreen ? 0 : Math.max(0, root.getBoundingClientRect().top + window.scrollY);
        const visible = window.visualViewport;
        const bottomEdge = visible ? visible.height + visible.offsetTop + window.scrollY : window.innerHeight;
        root.style.height = Math.max(0, Math.min(window.innerHeight, bottomEdge) - top - bottom) + 'px';
    }

    /** 現在の設定と状態から表示を更新します。 */
    function update()
    {
        const expectedNative = 'native';

        const mode = getScaleMode();
        if (viewport.dataset.scale !== mode)
        {
            viewport.dataset.scale = mode;
            viewport.scrollLeft = viewport.scrollTop = 0;
        }
        fitWorkspace();
        const views = getViews().filter(Boolean);
        if (!views.length)
        {
            return;
        }
        const style = getComputedStyle(viewport), gap = parseFloat(style.columnGap) || 0;
        const labels = Math.max(...views.map(view => view.name.getBoundingClientRect().height));
        insets[0] = parseFloat(style.paddingLeft) || 0;
        insets[1] = (parseFloat(style.paddingTop) || 0) + labels;
        insets[2] = parseFloat(style.paddingRight) || 0;
        insets[3] = parseFloat(style.paddingBottom) || 0;
        const width = Math.max(0, viewport.clientWidth - (parseFloat(style.paddingLeft) || 0) - (parseFloat(style.paddingRight) || 0) - gap * (views.length - 1));
        const height = Math.max(0, viewport.clientHeight - (parseFloat(style.paddingTop) || 0) - (parseFloat(style.paddingBottom) || 0) - labels);
        const scale = mode === expectedNative ? 1 / (window.devicePixelRatio || 1) : Math.max(0, Math.min(width / views.reduce((sum, view) => sum + view.monitor.width, 0),
            height / Math.max(...views.map(view => view.monitor.height))));
        for (const view of views)
        {
            const width = view.monitor.width * scale, height = view.monitor.height * scale;
            view.panel.style.width = view.canvas.style.width = width + 'px';
            view.canvas.style.height = height + 'px';
            if (view.label)
            {
                view.label.style.maxWidth = Math.max(0, Math.min(width, viewport.clientWidth) - 16) + 'px';
            }
            if (view.annotationCanvas)
            {
                view.annotationCanvas.style.width = width + 'px';
                view.annotationCanvas.style.height = height + 'px';
            }
        }
    }

    const observer = new ResizeObserver(update);
    observer.observe(root);
    observer.observe(root.parentElement);
    observer.observe(viewport);
    for (const child of root.children)
    {
        if (child !== viewport)
        {
            observer.observe(child);
        }
    }
    window.addEventListener('resize', update, { signal });
    window.visualViewport?.addEventListener('resize', update, { signal });
    window.visualViewport?.addEventListener('scroll', update, { signal });
    document.addEventListener('fullscreenchange', update, { signal });
    signal.addEventListener('abort', () => observer.disconnect(), { once: true });
    update();
    return { update,

        /** 保持するポインター位置へ表示を追従させます。
         * @returns 呼出し元が使用する値です。
         */
        followPointer(x, y)
        {
            return followDesktopPointer(viewport, x, y, insets);
        },

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            observer.disconnect();
        } };
}
