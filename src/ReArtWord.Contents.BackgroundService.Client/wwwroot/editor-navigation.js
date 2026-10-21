import { centerGraphPoint, scrollGraphBy } from './graph-viewport.js';
import { attachMinimapPointer, fitMapRectangle } from './editor-minimap.js';

const svgNamespace = 'http://www.w3.org/2000/svg';
const keys = { map: 'm', find: 'f', error: 'b' };

/** 全体を表示している場合も、マップ内へ収まる表示範囲の枠を返します。
 * @returns マップと表示範囲の共通部分です。
 */
export function clippedMapScope(first, last, bounds, inset)
{
    const left = Math.max(bounds.x + inset, first.x), top = Math.max(bounds.y + inset, first.y);
    const right = Math.min(bounds.x + bounds.width - inset, last.x), bottom = Math.min(bounds.y + bounds.height - inset, last.y);
    return { x: left, y: top, width: Math.max(0, right - left), height: Math.max(0, bottom - top) };
}

/** 入力状態と修飾キーからエディタの移動操作を選びます。
 * @returns マップ0、検索1、エラー2、通常の入力はnullです。
 */
export function editorNavigationShortcut(event, background, textInput)
{
    if (event.defaultPrevented || event.isComposing || event.repeat || event.altKey || event.shiftKey)
    {
        return null;
    }
    const key = event.key.toLowerCase();
    if (event.ctrlKey || event.metaKey)
    {
        if (key === keys.find)
        {
            return 1;
        }
        if (key === keys.error && !textInput)
        {
            return 2;
        }
        return null;
    }
    return key === keys.map && background ? 0 : null;
}

/** 全体マップ、表示範囲、キーボードの移動操作を画面の寿命へ接続します。
 * @returns 表示同期、ノードへの移動、終了処理です。
 * @contract WorkflowEditorのナビゲーションコンポーネントから呼び出します。
 */
export function attachEditorNavigation(viewport, root, receiver)
{
    const svg = viewport.querySelector('svg.graph');
    const map = root.querySelector('.editor-minimap'), mapNodes = map.querySelector('[data-map-nodes]');
    const scope = map.querySelector('[data-map-viewport]');
    const nodes = new Map(), rectangles = new Map();
    let visible = false, disabled = false, disposed = false, dirty = true, frame = 0, highlightTimer = 0;
    let matches = new Set(), active = null, owner = null;

    /** 現在のノード配置だけを読み、テキストの更新では配置を再計算しません。 */
    function readNodes()
    {
        if (!dirty || !svg.getScreenCTM())
        {
            return;
        }
        dirty = false;
        nodes.clear();
        const inverse = svg.getScreenCTM().inverse();
        for (const node of svg.querySelectorAll('.graph-node'))
        {
            const box = node.querySelector('.node-box'), transform = node.getScreenCTM();
            if (!box || !transform)
            {
                continue;
            }
            const point = new DOMPoint(0, 0).matrixTransform(transform).matrixTransform(inverse);
            const value = { node, x: point.x, y: point.y, width: box.width.baseVal.value, height: box.height.baseVal.value };
            if ([value.x, value.y, value.width, value.height].every(Number.isFinite))
            {
                nodes.set(node.dataset.nodeId, value);
            }
        }
    }

    /** 検索の強調は実行状態の属性と分けて更新します。 */
    function highlight()
    {
        for (const [id, value] of nodes)
        {
            value.node.toggleAttribute('data-search-match', matches.has(id));
            value.node.toggleAttribute('data-search-active', matches.has(id) && id === active);
        }
    }

    /** 配置の左上から右下までを、余白付きのマップ座標へ設定します。 */
    function drawNodes()
    {
        for (const [id, rectangle] of rectangles)
        {
            if (!nodes.has(id))
            {
                rectangle.remove();
                rectangles.delete(id);
            }
        }
        let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity;
        for (const [id, value] of nodes)
        {
            let rectangle = rectangles.get(id);
            if (!rectangle)
            {
                rectangle = document.createElementNS(svgNamespace, 'rect');
                rectangle.setAttribute('class', 'map-node');
                rectangle.setAttribute('data-node-id', id);
                rectangle.setAttribute('vector-effect', 'non-scaling-stroke');
                mapNodes.append(rectangle);
                rectangles.set(id, rectangle);
            }
            for (const property of ['x', 'y', 'width', 'height'])
            {
                rectangle.setAttribute(property, String(value[property]));
            }
            rectangle.toggleAttribute('data-search-match', matches.has(id));
            rectangle.toggleAttribute('data-search-active', matches.has(id) && id === active);
            rectangle.toggleAttribute('data-running', value.node.hasAttribute('data-running'));
            left = Math.min(left, value.x);
            top = Math.min(top, value.y);
            right = Math.max(right, value.x + value.width);
            bottom = Math.max(bottom, value.y + value.height);
        }
        if (!nodes.size)
        {
            map.setAttribute('viewBox', '0 0 1 1');
            scope.setAttribute('visibility', 'hidden');
            return;
        }
        const padding = Math.max(12, Math.max(right - left, bottom - top) * 0.025);
        map.setAttribute('viewBox', `${left - padding} ${top - padding} ${Math.max(1, right - left) + padding * 2} ${Math.max(1, bottom - top) + padding * 2}`);
    }

    /** スクロール中は小さい表示範囲の矩形だけを更新します。 */
    function drawScope()
    {
        const matrix = svg.getScreenCTM();
        if (!matrix || !nodes.size)
        {
            return;
        }
        const box = viewport.getBoundingClientRect(), inverse = matrix.inverse();
        const first = new DOMPoint(box.left + viewport.clientLeft, box.top + viewport.clientTop).matrixTransform(inverse);
        const last = new DOMPoint(box.left + viewport.clientLeft + viewport.clientWidth, box.top + viewport.clientTop + viewport.clientHeight).matrixTransform(inverse);
        const mapScale = map.getScreenCTM()?.a || 1;
        const clipped = clippedMapScope(first, last, map.viewBox.baseVal, 1 / mapScale);
        for (const property of ['x', 'y', 'width', 'height'])
        {
            scope.setAttribute(property, String(clipped[property]));
        }
        scope.setAttribute('visibility', clipped.width && clipped.height ? 'visible' : 'hidden');
    }

    /** レイアウトと強調の更新を描画フレーム単位へまとめます。 */
    function paint()
    {
        frame = 0;
        if (disposed)
        {
            return;
        }
        const box = viewport.getBoundingClientRect();
        root.style.right = `${Math.max(8, document.documentElement.clientWidth - box.left - viewport.clientLeft - viewport.clientWidth + 8)}px`;
        root.style.top = `${box.top + 8}px`;
        root.style.maxWidth = `${Math.max(0, box.width - 16)}px`;
        root.setAttribute('data-navigation-ready', '');
        if (!visible && !matches.size)
        {
            return;
        }
        const changed = dirty;
        readNodes();
        if (changed)
        {
            highlight();
        }
        if (visible)
        {
            if (changed)
            {
                drawNodes();
            }
            drawScope();
        }
    }

    /** 所有する更新を一回だけ予約します。 */
    function schedule()
    {
        if (!disposed && !frame)
        {
            frame = requestAnimationFrame(paint);
        }
    }

    const pointer = attachMinimapPointer(map, map.querySelector('[data-map-selection]'), {
        enabled: () => !disposed && visible && !disabled && Boolean(nodes.size),

        version: () => svg.dataset.connectionVersion,

        move: point =>
        {
            centerGraphPoint(svg, viewport, point.x, point.y);
            schedule();
        },

        select: rectangle =>
        {
            const target = fitMapRectangle(rectangle, viewport.clientWidth, viewport.clientHeight);
            if (target)
            {
                receiver.invokeMethodAsync('FitMapAsync', target.x, target.y, target.zoom).catch(() =>
                {});
            }
        }
    });

    /** 文字入力とブラウザー操作を区別して、エディタ用のショートカットだけを扱います。 */
    const keyDown = event =>
    {
        if (disposed || disabled
            || document.querySelector('dialog[open],[role="dialog"][aria-modal="true"]'))
        {
            return;
        }
        const target = event.target;
        if (!(target instanceof Element) || target.closest('.cm-editor,.quick-add,textarea,[contenteditable="true"]'))
        {
            return;
        }
        if (target !== document.body && !viewport.closest('.studio')?.contains(target))
        {
            return;
        }
        const action = editorNavigationShortcut(event, target === viewport || target === svg, Boolean(target.closest('input,select')));
        if (action !== null)
        {
            event.preventDefault();
            event.stopPropagation();
            receiver.invokeMethodAsync('ShortcutAsync', action).catch(() =>
            {});
        }
    };

    const mutations = new MutationObserver(records =>
    {
        if (records.some(record => record.attributeName !== 'data-running' && (record.target === svg || record.target.matches?.('.graph-node,.node-box'))))
        {
            dirty = true;
            if (visible || matches.size)
            {
                schedule();
            }
        }
        for (const record of records.filter(item => item.attributeName === 'data-running'))
        {
            rectangles.get(record.target.dataset.nodeId)?.toggleAttribute('data-running', record.target.hasAttribute('data-running'));
        }
    });
    mutations.observe(svg, { childList: true, subtree: true, attributes: true, attributeFilter: ['transform', 'width', 'height', 'data-connection-version', 'data-running'] });
    const resize = new ResizeObserver(() =>
    {
        dirty = true;
        schedule();
    });
    resize.observe(viewport);
    viewport.addEventListener('scroll', schedule, { passive: true });
    window.addEventListener('resize', schedule);
    document.addEventListener('keydown', keyDown, true);
    schedule();
    return {
        /** 現在の表示設定と検索結果を反映します。
         * @contract C#のコンポーネントから呼び出します。
         */
        update(showMap, blocked, matching, current, documentOwner)
        {
            if (!showMap || blocked || owner !== documentOwner)
            {
                pointer.cancel();
            }
            owner = documentOwner;
            dirty ||= visible !== showMap;
            visible = showMap;
            disabled = blocked;
            matches = new Set(matching);
            active = current;
            if (!visible && !matches.size)
            {
                for (const value of nodes.values())
                {
                    value.node.removeAttribute('data-search-match');
                    value.node.removeAttribute('data-search-active');
                }
                nodes.clear();
                rectangles.clear();
                mapNodes.replaceChildren();
                dirty = true;
                schedule();
                return;
            }
            readNodes();
            highlight();
            if (visible)
            {
                drawNodes();
            }
            schedule();
        },

        /** 新しい倍率の描画後に、選択矩形の中心へ移動します。
         * @contract C#の倍率変更後に呼び出します。
         */
        centerPoint(x, y)
        {
            if (!disposed && !disabled)
            {
                centerGraphPoint(svg, viewport, x, y);
                schedule();
            }
        },

        /** 指定ノードを表示中央へ移し、実行中とは別の枠で示します。
         * @returns ノードが現在の図に存在するかです。
         * @contract ログ・検索・エラーへの移動から呼び出します。
         */
        focusNode(id)
        {
            const node = svg.querySelector(`.graph-node[data-node-id="${CSS.escape(id)}"]`);
            const box = node?.querySelector('.node-box');
            if (!box)
            {
                return false;
            }
            const bounds = box.getBoundingClientRect(), host = viewport.getBoundingClientRect();
            scrollGraphBy(svg, viewport, bounds.left + bounds.width / 2 - host.left - viewport.clientLeft - viewport.clientWidth / 2,
                bounds.top + bounds.height / 2 - host.top - viewport.clientTop - viewport.clientHeight / 2);
            for (const previous of svg.querySelectorAll('[data-navigation-target]'))
            {
                previous.removeAttribute('data-navigation-target');
            }
            node.setAttribute('data-navigation-target', '');
            clearTimeout(highlightTimer);
            highlightTimer = setTimeout(() => node.removeAttribute('data-navigation-target'), 2500);
            schedule();
            return true;
        },

        /** 画面を離れたらイベントと観測を解放します。
         * @contract コンポーネントの破棄時に呼び出します。
         */
        dispose()
        {
            disposed = true;
            mutations.disconnect();
            resize.disconnect();
            cancelAnimationFrame(frame);
            clearTimeout(highlightTimer);
            viewport.removeEventListener('scroll', schedule);
            window.removeEventListener('resize', schedule);
            document.removeEventListener('keydown', keyDown, true);
            pointer.dispose();
            for (const node of svg.querySelectorAll('.graph-node'))
            {
                node.removeAttribute('data-search-match');
                node.removeAttribute('data-search-active');
                node.removeAttribute('data-navigation-target');
            }
        }
    };
}
