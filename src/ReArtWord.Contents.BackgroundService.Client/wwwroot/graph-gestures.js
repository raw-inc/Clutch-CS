// Pan uses the existing scroll viewport. Zoom shares the SVG screen transform used by drag and drop.
import { scrollGraphEdge } from './graph-edge-scroll.js';
import { scrollGraphBy } from './graph-viewport.js';
import { attachGraphScopeHighlight } from './graph-scope-highlight.js';

/** ノード移動・選択・拡縮のイベントを接続します。
 * @returns update・disposeを含む結果です。
 */
export function attachGraphGestures(svg, receiver, initialZoom)
{
    const scopeHighlight = attachGraphScopeHighlight(svg);
    const viewport = svg.parentElement;
    let zoom = initialZoom, pan = null, anchor = null, delta = 0, pointer = null;
    let frame = 0, inFlight = false, disposed = false;
    let pointerFrame = 0, pointerTimer = 0, pointerBusy = false, latestPointer = null, lastPointer = 0;
    let nodePointer = null, edgeFrame = 0, edgeTime = 0, lastCoordinates = null, clickNode = null;
    const originalTouchAction = svg.style.touchAction;
    const originalMargins = [svg.style.marginLeft, svg.style.marginTop, svg.style.marginRight, svg.style.marginBottom];
    svg.style.touchAction = 'none';

    /** 要素が通常の文字入力を優先する対象か判定します。
     * @returns 呼出し元が使用する値です。
     */
    const editable = target => target instanceof Element && target.closest('input,textarea,select,[contenteditable]:not([contenteditable="false"]),dialog,[role="dialog"]');

    /** 現在の状態に対応する呼出し先へ操作を渡します。
     * @returns 呼出し元が使用する値です。
     */
    const invoke = (method, ...args) => receiver.invokeMethodAsync(method, ...args);

    /** 同じ表示座標系で、パン・検索・マップの移動を反映します。 */
    const scrollBy = (dx, dy) =>
    {
        scrollGraphBy(svg, viewport, dx, dy);
    };

    /** キャンバス移動を終了し、一時状態を解除します。 */
    const finishPan = () =>
    {
        if (!pan)
        {
            return;
        }
        const current = pan;
        pan = null;
        current.rectangle?.remove();
        if (viewport.hasPointerCapture(current.id))
        {
            viewport.releasePointerCapture(current.id);
        }
        viewport.style.cursor = current.cursor;
    };

    /** ノード移動に伴う自動スクロールを停止します。 */
    const stopNodeScroll = () =>
    {
        if (edgeFrame)
        {
            cancelAnimationFrame(edgeFrame);
        }
        edgeFrame = edgeTime = 0;
        nodePointer = null;
    };

    /** 現在のドラッグがこのグラフのノード操作かを確認します。
     * @returns 呼出し元が使用する値です。
     */
    const acceptedNodeDrag = () => nodePointer?.moved && !disposed && !pan && !document.hidden
        && svg.dataset.nodeDragging === 'true' && svg.dataset.connectionDragging !== 'true'
        && nodePointer.version === Number(svg.dataset.connectionVersion) && svg.hasPointerCapture(nodePointer.id);

    /** 移動中のノードに合わせて表示位置を調整します。 */
    const scrollNode = time =>
    {
        edgeFrame = 0;
        if (!acceptedNodeDrag())
        {
            edgeTime = 0;
            return;
        }
        const elapsed = edgeTime ? Math.min(32, time - edgeTime) / 1000 : 1 / 60;
        edgeTime = time;
        if (!scrollGraphEdge(svg, nodePointer.x, nodePointer.y, elapsed))
        {
            edgeTime = 0;
            return;
        }

        // Scrolling changes the SVG transform even when the physical pointer has stopped moving.
        latestPointer = { ...nodePointer };
        schedulePointer();
        edgeFrame = requestAnimationFrame(scrollNode);
    };

    /** ノード移動中のスクロールを次の描画へ予約します。 */
    const scheduleNodeScroll = () =>
    {
        if (!edgeFrame && acceptedNodeDrag())
        {
            edgeFrame = requestAnimationFrame(scrollNode);
        }
    };

    /** ノードのクリック候補とドラッグ開始位置を記録します。 */
    function beginNodePointer(event)
    {
        const node = event.target instanceof Element ? event.target.closest('.graph-node') : null;
        clickNode = null;
        if (event.button === 0 && node && !event.ctrlKey && !event.shiftKey && !editable(event.target) && !event.target.closest('.node-resize-handle'))
        {
            clickNode = { id: node.dataset.nodeId, version: Number(svg.dataset.connectionVersion),
                label: event.target.closest('.node-name')?.querySelector('.node-title') ?? null };
        }
        if (event.button === 0 && !event.ctrlKey && event.isPrimary && !pan && event.target instanceof Element
            && event.target.closest('.graph-node') && !editable(event.target) && !event.target.closest('.node-resize-handle'))
        {
            stopNodeScroll();
            nodePointer = { id: event.pointerId, x: event.clientX, y: event.clientY, startX: event.clientX, startY: event.clientY,
                moved: false, version: Number(svg.dataset.connectionVersion) };
        }
    }

    /** ポインターの押下から操作状態を開始します。 */
    const down = event =>
    {
        beginNodePointer(event);
        if (event.button !== 0 || !event.isPrimary || pan || inFlight || (event.target !== svg && event.target !== viewport))
        {
            return;
        }
        pan = { id: event.pointerId, x: event.clientX, y: event.clientY, startX: event.clientX, startY: event.clientY, moved: false, cursor: viewport.style.cursor, selection: event.shiftKey, version: Number(svg.dataset.connectionVersion) };
        viewport.setPointerCapture(event.pointerId);
        if (pan.selection)
        {
            pan.rectangle = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
            pan.rectangle.setAttribute('fill', '#62aee833');
            pan.rectangle.setAttribute('stroke', '#62aee8');
            pan.rectangle.setAttribute('stroke-dasharray', '5 3');
            pan.rectangle.setAttribute('pointer-events', 'none');
            svg.append(pan.rectangle);
        }
        viewport.style.cursor = pan.selection ? 'crosshair' : 'grabbing';
        viewport.focus({ preventScroll: true });
        event.preventDefault();
        event.stopPropagation();
    };

    /** ポインターの移動を進行中の操作へ反映します。 */
    const move = event =>
    {
        if (!pan || event.pointerId !== pan.id)
        {
            return;
        }
        const dx = event.clientX - pan.x, dy = event.clientY - pan.y;
        pan.moved ||= (4) <= (Math.hypot(event.clientX - pan.startX, event.clientY - pan.startY));
        if (pan.selection)
        {
            const start = window.contentsBrowser.point(svg, pan.startX, pan.startY);
            const end = window.contentsBrowser.point(svg, event.clientX, event.clientY);
            for (const [name, value] of Object.entries({ x: Math.min(start.x, end.x), y: Math.min(start.y, end.y), width: Math.abs(end.x - start.x), height: Math.abs(end.y - start.y) }))
            {
                pan.rectangle.setAttribute(name, value);
            }
            pan.x = event.clientX;
            pan.y = event.clientY;
        }
        else if (pan.moved)
        {
            scrollBy(-dx, -dy);
            pan.x = event.clientX;
            pan.y = event.clientY;
        }
        event.preventDefault();
        event.stopPropagation();
    };

    /** ポインターの解放時に操作を確定または終了します。 */
    const up = event =>
    {
        if (!pan || event.pointerId !== pan.id)
        {
            return;
        }
        const click = !pan.moved && !pan.selection;
        if (pan.selection)
        {
            const left = Math.min(pan.startX, event.clientX), right = Math.max(pan.startX, event.clientX);
            const top = Math.min(pan.startY, event.clientY), bottom = Math.max(pan.startY, event.clientY);
            const ids = [...svg.querySelectorAll('.graph-node')].filter(node =>
            {
                const bounds = node.querySelector('.node-box').getBoundingClientRect();
                return left <= bounds.left && bounds.right <= right && top <= bounds.top && bounds.bottom <= bottom;
            }).map(node => node.dataset.nodeId);
            invoke('SelectRangeAsync', ids, pan.version).catch(() =>
            {});
        }
        finishPan();
        event.preventDefault();
        event.stopPropagation();
        if (click)
        {
            invoke('BackgroundClickAsync').catch(() =>
            {});
        }
    };

    /** 進行中の操作を取り消して一時状態を片付けます。 */
    const cancel = () =>
    {
        finishPan();
        stopNodeScroll();
    };

    // SVG pointer capture retargets click/dblclick to the canvas. Keep the pressed card identity.
    /** ダブルクリックに対応する画面操作を通知します。 */
    const doubleClick = event =>
    {
        const clicked = clickNode;
        clickNode = null;
        if (!disposed && clicked)
        {
            if (clicked.label)
            {
                // Capture can move the event to SVG; keep editing on the originally pressed title.
                if (clicked.label.isConnected && clicked.version === Number(svg.dataset.connectionVersion) && event.target !== clicked.label)
                {
                    clicked.label.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
                }
                return;
            }
            invoke('OpenRecipePreviewAsync', clicked.id, clicked.version).catch(() =>
            {});
        }
    };

    /** ノードのポインター操作を終了します。 */
    const endNodePointer = event =>
    {
        if (nodePointer && (event.pointerId === undefined || event.pointerId === nodePointer.id))
        {
            stopNodeScroll();
            latestPointer = null;
        }
    };

    /** 画面の可視状態に応じて進行中の操作を整理します。 */
    const visibility = () =>
    {
        if (document.hidden)
        {
            cancel();
        }
    };

    /** グラフの操作状態と修飾キーからコピー・貼り付けを許可するか判定します。
     * @returns 操作を開始できる場合はtrueです。 */
    function canUseClipboard(event)
    {
        const expectedTrue = 'true';
        return !(disposed || event.defaultPrevented || event.isComposing || event.repeat || event.altKey || event.shiftKey
            || !(event.ctrlKey || event.metaKey) || editable(event.target) || pan || inFlight
            || svg.dataset.nodeDragging === expectedTrue || svg.dataset.connectionDragging === expectedTrue);
    }

    /** DOM上のノードまたは接続を、現在の表示世代とともに操作対象へ変換します。
     * @returns フォーカス位置のノードIDと接続番号です。 */
    function graphTarget(target)
    {
        const node = target instanceof Element ? target.closest('.graph-node')?.dataset.nodeId ?? null : null;
        const index = target instanceof Element ? target.closest('.wire-group')?.dataset.wireIndex : undefined;
        const wire = index === undefined ? null : Number(index);
        return { node, wire: Number.isSafeInteger(wire) ? wire : null };
    }

    /** 子要素やブラウザーに渡る前に、グラフの右クリックをWFメニューへ渡します。 */
    const contextMenu = event =>
    {
        if (disposed || !(event.target instanceof Element) || event.target.closest('.graph-context-menu,.graph-menu-shade'))
        {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        const target = graphTarget(event.target);
        invoke('OpenContextMenuAsync', target.node, target.wire, event.clientX, event.clientY, Number(svg.dataset.connectionVersion)).catch(() =>
        {});
    };

    /** 文字入力、メニュー、進行中の操作をDeleteの対象から除外します。
     * @returns グラフの選択を削除できる入力ならtrueです。 */
    function canDelete(event)
    {
        const expectedTrue = 'true';
        return !(disposed || event.defaultPrevented || event.isComposing || event.repeat
            || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey || pan || inFlight
            || editable(event.target) || event.target.closest?.('.graph-context-menu,.graph-menu-shade')
            || svg.dataset.nodeDragging === expectedTrue || svg.dataset.connectionDragging === expectedTrue);
    }

    /** 論理名のIME変換確定を、編集欄を閉じるキーから区別します。
     * @returns IMEが所有する論理名のキーならtrueです。 */
    function composingName(event)
    {
        return (event.isComposing || event.keyCode === 229) && event.target instanceof Element && event.target.closest('.node-name-input');
    }

    /** キーの押下を文字入力と画面操作に振り分けます。 */
    const keyDown = event =>
    {
        const expectedEscape = 'Escape';
        const expectedTrue = 'true';
        const expectedC = 'c';
        const expectedV = 'v';
        const expectedDelete = 'Delete';

        if (composingName(event))
        {
            event.stopPropagation();
            return;
        }

        if (event.key === expectedDelete && canDelete(event))
        {
            const target = graphTarget(event.target);
            event.preventDefault();
            event.stopPropagation();
            invoke('DeleteSelectionAsync', target.node, target.wire, Number(svg.dataset.connectionVersion)).catch(() =>
            {});
            return;
        }

        if (event.key === expectedEscape)
        {
            cancel();
        }
        if (event.target instanceof Element && event.target.closest('.node-resize-handle') && ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key))
        {
            event.preventDefault();
        }
        if (!canUseClipboard(event))
        {
            return;
        }
        const key = event.key?.toLowerCase();
        if ((key !== expectedC && key !== expectedV) || (key === expectedC && svg.dataset.canCopy !== expectedTrue))
        {
            return;
        }
        const bounds = viewport.getBoundingClientRect();
        const at = lastCoordinates ?? { x: (bounds.left + bounds.right) / 2, y: (bounds.top + bounds.bottom) / 2 };
        const point = window.contentsBrowser.point(svg, at.x, at.y);
        if (!point || !Number.isFinite(point.x) || !Number.isFinite(point.y))
        {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        invoke('ClipboardShortcutAsync', key === 'v', point.x, point.y, Number(svg.dataset.connectionVersion)).catch(() =>
        {});
    };

    /** 重複した描画要求を次の一回にまとめます。 */
    const schedule = () =>
    {
        if (frame || inFlight || disposed || !delta)
        {
            return;
        }
        frame = requestAnimationFrame(() =>
        {
            const expectedTrue2 = 'true';

            frame = 0;
            if (disposed || !pointer)
            {
                return;
            }
            if (svg.dataset.nodeDragging === expectedTrue2)
            {
                delta = 0;
                return;
            }
            const next = Math.max(.05, Math.min(1.5, zoom * Math.exp(-delta * .0015)));
            delta = 0;
            if (Math.abs(next - zoom) < .000001)
            {
                return;
            }
            const point = window.contentsBrowser.point(svg, pointer.x, pointer.y);
            anchor = { point, x: pointer.x, y: pointer.y, zoom: next };
            inFlight = true;
            invoke('ZoomAtAsync', next).catch(() =>
            {
                anchor = null;
                inFlight = false;
                delta = 0;
            });
        });
    };

    /** ホイールの移動量を拡縮またはスクロールに反映します。 */
    const wheel = event =>
    {
        const expectedTrue3 = 'true';

        if (!event.ctrlKey)
        {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        if (pan || disposed || editable(event.target))
        {
            return;
        }

        // Avoid mixing an accepted node drag with a changing screen transform.
        if (svg.dataset.nodeDragging === expectedTrue3 || svg.dataset.connectionDragging === expectedTrue3)
        {
            return;
        }
        const factor = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? viewport.clientHeight : 1;
        delta += event.deltaY * factor;
        pointer = { x: event.clientX, y: event.clientY };
        schedule();
    };

    // Keep only the newest position and one .NET call in flight. Presence never renders the graph.
    /** 最新のポインター位置を次の表示更新へ予約します。 */
    const schedulePointer = () =>
    {
        const expectedTrue4 = 'true';

        if (disposed || pointerBusy || pointerTimer || pointerFrame || !latestPointer)
        {
            return;
        }
        const interval = svg.dataset.nodeDragging === expectedTrue4 ? 32 : 150;
        pointerTimer = setTimeout(() =>
        {
            pointerTimer = 0;
            pointerFrame = requestAnimationFrame(() =>
            {
                pointerFrame = 0;
                if (disposed || !latestPointer)
                {
                    return;
                }
                const latest = latestPointer;
                latestPointer = null;
                const point = window.contentsBrowser.point(svg, latest.x, latest.y);
                lastPointer = performance.now();
                pointerBusy = true;
                invoke('MovePointer', point.x, point.y, latest.version).catch(() =>
                {}).finally(() =>
                {
                    pointerBusy = false;
                    schedulePointer();
                });
            });
        }, Math.max(0, interval - (performance.now() - lastPointer)));
    };

    /** ポインターの最新位置を保持します。 */
    const trackPointer = event =>
    {
        const expectedTrue5 = 'true';

        if (pan || disposed || svg.dataset.connectionDragging === expectedTrue5)
        {
            return;
        }
        if (nodePointer)
        {
            if (event.pointerId !== nodePointer.id)
            {
                return;
            }
            if (event.buttons === 0)
            {
                stopNodeScroll();
            }
            else
            {
                nodePointer.moved ||= (4) <= (Math.hypot(event.clientX - nodePointer.startX, event.clientY - nodePointer.startY));
                if (nodePointer.moved)
                {
                    clickNode = null;
                }
                nodePointer.x = event.clientX;
                nodePointer.y = event.clientY;
                scheduleNodeScroll();
            }
        }
        lastCoordinates = { x: event.clientX, y: event.clientY, version: Number(svg.dataset.connectionVersion) };
        if (svg.dataset.nodeDragging === expectedTrue5 || svg.dataset.realtimePointer === expectedTrue5)
        {
            latestPointer = lastCoordinates;
            schedulePointer();
        }
    };

    /** ポインターが離れた際の一時表示を解除します。 */
    const leavePointer = () =>
    {
        const expectedTrue6 = 'true';

        if (acceptedNodeDrag())
        {
            return;
        }
        lastCoordinates = null;
        latestPointer = null;
        if (!disposed && svg.dataset.realtimePointer === expectedTrue6)
        {
            invoke('LeavePointer').catch(() =>
            {});
        }
    };

    /** 現在進行中の操作状態を呼出し元へ伝えます。 */
    const interaction = event =>
    {
        if (disposed)
        {
            return;
        }
        if (Number.isFinite(event.clientX) && Number.isFinite(event.clientY))
        {
            lastCoordinates = { x: event.clientX, y: event.clientY };
        }
        if (!lastCoordinates)
        {
            return;
        }
        const point = window.contentsBrowser.point(svg, lastCoordinates.x, lastCoordinates.y);
        invoke('InteractPointerAsync', point.x, point.y, Number(svg.dataset.connectionVersion), ['input', 'change'].includes(event.type) ? event.target?.closest?.('.graph-node')?.dataset.nodeId ?? null : null).catch(() =>
        {});
    };

    svg.addEventListener('pointerdown', interaction, true);
    svg.addEventListener('pointerup', interaction, true);
    svg.addEventListener('input', interaction, true);
    svg.addEventListener('change', interaction, true);
    svg.addEventListener('pointermove', trackPointer);
    svg.addEventListener('pointerleave', leavePointer);
    svg.addEventListener('dblclick', doubleClick);
    viewport.addEventListener('pointerdown', down, true);
    viewport.addEventListener('pointermove', move, true);
    viewport.addEventListener('pointerup', up, true);
    viewport.addEventListener('pointercancel', cancel);
    viewport.addEventListener('lostpointercapture', cancel);
    viewport.addEventListener('wheel', wheel, { passive: false, capture: true });
    viewport.addEventListener('keydown', keyDown, true);
    viewport.addEventListener('contextmenu', contextMenu, true);
    window.addEventListener('blur', cancel);
    window.addEventListener('pointerup', endNodePointer, true);
    window.addEventListener('pointercancel', endNodePointer, true);
    svg.addEventListener('lostpointercapture', endNodePointer);
    document.addEventListener('visibilitychange', visibility);
    return {
        /** 現在の設定と状態から表示を更新します。 */
        update(currentZoom)
        {
            scopeHighlight.update();
            zoom = currentZoom;
            if (nodePointer && nodePointer.version !== Number(svg.dataset.connectionVersion))
            {
                stopNodeScroll();
            }
            scheduleNodeScroll();
            if (anchor && Math.abs(currentZoom - anchor.zoom) < .000001)
            {
                const current = new DOMPoint(anchor.point.x, anchor.point.y).matrixTransform(svg.getScreenCTM());
                scrollBy(current.x - anchor.x, current.y - anchor.y);
                anchor = null;
                inFlight = false;
                schedule();
            }
        },

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            scopeHighlight.dispose();
            viewport.removeEventListener('keydown', keyDown, true);
            viewport.removeEventListener('contextmenu', contextMenu, true);
            disposed = true;
            stopNodeScroll();
            if (frame)
            {
                cancelAnimationFrame(frame);
            }
            if (pointerFrame)
            {
                cancelAnimationFrame(pointerFrame);
            }
            if (pointerTimer)
            {
                clearTimeout(pointerTimer);
            }
            latestPointer = null;
            svg.removeEventListener('pointerdown', interaction, true);
            svg.removeEventListener('pointerup', interaction, true);
            svg.removeEventListener('input', interaction, true);
            svg.removeEventListener('change', interaction, true);
            svg.removeEventListener('pointermove', trackPointer);
            svg.removeEventListener('pointerleave', leavePointer);
            svg.removeEventListener('dblclick', doubleClick);
            finishPan();
            svg.style.touchAction = originalTouchAction;
            [svg.style.marginLeft, svg.style.marginTop, svg.style.marginRight, svg.style.marginBottom] = originalMargins;
            viewport.removeEventListener('pointerdown', down, true);
            viewport.removeEventListener('pointermove', move, true);
            viewport.removeEventListener('pointerup', up, true);
            viewport.removeEventListener('pointercancel', cancel);
            viewport.removeEventListener('lostpointercapture', cancel);
            viewport.removeEventListener('wheel', wheel, true);
            window.removeEventListener('blur', cancel);
            window.removeEventListener('pointerup', endNodePointer, true);
            window.removeEventListener('pointercancel', endNodePointer, true);
            svg.removeEventListener('lostpointercapture', endNodePointer);
            document.removeEventListener('visibilitychange', visibility);
        }
    };
}
