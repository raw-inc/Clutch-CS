// Pointer gestures only. The .NET editor owns validation, locks, history and persistence.
import { scrollGraphEdge } from './graph-edge-scroll.js';
import { createPortSnap } from './graph-port-snap.js';

/** ポート間の配線操作と表示の寿命を接続します。
 * @returns update・disposeを含む結果です。
 */
export function attachGraphConnections(svg, receiver)
{
    let pending = null;
    let preview = null;
    let snapping = null;
    let suppressClick = null;
    let cancelledPointer = null;
    let disposed = false;
    let edgeFrame = 0, edgeTime = 0;
    let hoveredPorts = [];
    const visibleWires = new Map();
    const visibility = typeof IntersectionObserver === 'function' ? new IntersectionObserver(entries =>
    {
        if (disposed)
        {
            return;
        }
        for (const entry of entries)
        {
            if (!visibleWires.has(entry.target))
            {
                continue;
            }
            visibleWires.set(entry.target, entry.isIntersecting);
            entry.target.classList.toggle('wire-visible', entry.isIntersecting);
        }
    }, { root: svg.parentElement, rootMargin: '32px' }) : null;

    /** 表示設定に従って配線アニメーションを更新します。 */
    const updateAnimation = () =>
    {
        const current = new Set(svg.querySelectorAll('.wire-group.animate'));
        for (const wire of visibleWires.keys())
        {
            if (!current.has(wire))
            {
                visibility?.unobserve(wire);
                visibleWires.delete(wire);
                wire.classList.remove('wire-visible');
            }
        }
        for (const wire of current)
        {
            if (!visibleWires.has(wire))
            {
                visibleWires.set(wire, !visibility);
                visibility?.observe(wire);
            }
            wire.classList.toggle('wire-visible', visibleWires.get(wire));
        }
    };

    /** 配線候補の強調表示を解除します。 */
    const clearHover = () =>
    {
        for (const circle of hoveredPorts)
        {
            circle.classList.remove('wire-endpoint-hover');
        }
        hoveredPorts = [];
    };

    /** 現在のポインター下にある接続候補を表示します。 */
    const hover = event =>
    {
        const wire = event.target?.closest?.('.wire-group');
        if (wire === event.relatedTarget?.closest?.('.wire-group'))
        {
            return;
        }
        clearHover();
        if (!wire || !svg.contains(wire))
        {
            return;
        }
        for (const port of svg.querySelectorAll('[data-wire-port]'))
        {
            const data = port.dataset, output = data.wireOutput === 'true';
            if (data.wireNode === (output ? wire.dataset.fromNode : wire.dataset.toNode)
                && data.wirePort === (output ? wire.dataset.fromPort : wire.dataset.toPort))
            {
                const circle = port.querySelector('circle');
                if (circle)
                {
                    circle.classList.add('wire-endpoint-hover');
                    hoveredPorts.push(circle);
                }
            }
        }
    };

    /** イベント位置が操作対象外へ移ったか確認します。 */
    const eventOut = event =>
    {
        if (event.target?.closest?.('.wire-group') !== event.relatedTarget?.closest?.('.wire-group'))
        {
            clearHover();
        }
    };

    /** 現在の状態に対応する呼出し先へ操作を渡します。
     * @returns 呼出し元が使用する値です。
     */
    const invoke = (name, ...args) => receiver.invokeMethodAsync(name, ...args).catch(() =>
    {});

    /** 指定座標の接続ポートを取得します。
     * @returns 呼出し元が使用する値です。
     */
    const portAt = target =>
    {
        const port = target?.closest?.('[data-wire-port]');
        return port && svg.contains(port) ? port : null;
    };

    /** ポート要素から接続先の識別情報を読み取ります。
     * @returns node・port・outputを含む結果です。
     */
    const endpoint = port => ({ node: port.dataset.wireNode, port: port.dataset.wirePort, output: port.dataset.wireOutput === 'true' });

    /** 画面座標を対象の座標系へ変換します。
     * @returns 呼出し元が使用する値です。
     */
    const point = (x, y) => new DOMPoint(x, y).matrixTransform(svg.getScreenCTM().inverse());

    /** 直前のドラッグに付随するクリックを抑制します。 */
    const suppressTerminalClick = event =>
    {
        const terminal = { pointer: event.pointerId, x: event.clientX, y: event.clientY };
        suppressClick = terminal;
        setTimeout(() =>
        {
            if (suppressClick === terminal)
            {
                suppressClick = null;
            }
        }, 0);
    };

    /** 保持しているポインター位置の有効性を確認します。 */
    const freshPointer = () =>
    {
        if (pending?.released)
        {
            cancel();
        }
        cancelledPointer = null;
        suppressClick = null;
    };

    /** 処理結果を確定し、待機と監視を終了します。
     * @returns 呼出し元が使用する値です。
     */
    const finish = () =>
    {
        if (edgeFrame)
        {
            cancelAnimationFrame(edgeFrame);
        }
        edgeFrame = edgeTime = 0;
        const old = pending;
        pending = null;
        snapping?.dispose();
        snapping = null;
        preview?.remove();
        preview = null;
        svg.dataset.connectionDragging = 'false';
        if (old && svg.hasPointerCapture(old.pointer))
        {
            svg.releasePointerCapture(old.pointer);
        }
        return old;
    };

    /** 確定前の接続候補を表示します。 */
    const drawPreview = () =>
    {
        if (!pending?.moved || !preview)
        {
            return;
        }
        const target = snapping?.pick(pending.currentX, pending.currentY);
        const current = point(target?.x ?? pending.currentX, target?.y ?? pending.currentY);
        const from = pending.output ? pending.origin : current;
        const to = pending.output ? current : pending.origin;
        preview.setAttribute('d', `M ${from.x} ${from.y} C ${from.x + 80} ${from.y}, ${to.x - 80} ${to.y}, ${to.x} ${to.y}`);
    };

    /** 接続操作中の端への移動に合わせてスクロールします。 */
    const scrollConnection = time =>
    {
        edgeFrame = 0;
        if (disposed || !pending?.moved || pending.released)
        {
            return;
        }
        if (document.hidden || pending.version !== Number(svg.dataset.connectionVersion) || !svg.hasPointerCapture(pending.pointer))
        {
            cancel();
            return;
        }
        const elapsed = edgeTime ? Math.min(32, time - edgeTime) / 1000 : 1 / 60;
        edgeTime = time;
        if (!scrollGraphEdge(svg, pending.currentX, pending.currentY, elapsed))
        {
            edgeTime = 0;
            return;
        }
        drawPreview();
        edgeFrame = requestAnimationFrame(scrollConnection);
    };

    /** ポインターの押下から操作状態を開始します。 */
    const down = event =>
    {
        const expectedTrue = 'true';

        if (disposed || pending || event.button !== 0 || event.isPrimary === false || svg.dataset.nodeDragging === expectedTrue)
        {
            return;
        }
        const port = portAt(event.target);
        if (!port)
        {
            return;
        }
        const circle = port.querySelector('circle') || port;
        const rect = circle.getBoundingClientRect();
        pending = { ...endpoint(port), pointer: event.pointerId, x: event.clientX, y: event.clientY,
            origin: point(rect.left + rect.width / 2, rect.top + rect.height / 2),
            version: Number(svg.dataset.connectionVersion), moved: false };
        svg.dataset.connectionDragging = 'true';
        event.preventDefault();
        event.stopPropagation();
    };

    /** ポインターの移動を進行中の操作へ反映します。 */
    const move = event =>
    {
        if (!pending || pending.released || event.pointerId !== pending.pointer)
        {
            return;
        }
        if (event.buttons === 0)
        {
            cancel();
            return;
        }
        pending.currentX = event.clientX;
        pending.currentY = event.clientY;
        if (!pending.moved && Math.hypot(event.clientX - pending.x, event.clientY - pending.y) < 4)
        {
            return;
        }
        if (!pending.moved)
        {
            pending.moved = true;
            const origin = pending;
            snapping = createPortSnap(svg, origin, (node, port) => invoke('CanConnectPorts', origin.node, origin.port, origin.output, node, port, origin.version), drawPreview);
            svg.setPointerCapture(pending.pointer);
            preview = document.createElementNS('http://www.w3.org/2000/svg', 'path');
            preview.setAttribute('fill', 'none');
            preview.setAttribute('stroke', 'var(--accent, #79b8ef)');
            preview.setAttribute('stroke-width', '2');
            preview.setAttribute('stroke-dasharray', '6 4');
            preview.setAttribute('pointer-events', 'none');
            preview.setAttribute('data-connection-preview', 'true');
            svg.appendChild(preview);
        }
        drawPreview();
        if (!edgeFrame)
        {
            edgeFrame = requestAnimationFrame(scrollConnection);
        }
        event.preventDefault();
        event.stopPropagation();
    };

    /** 近接ドロップは同じドラッグと文書が有効な間だけ型確認後に確定します。
     * @returns 取消し可能な接続確認です。
     */
    const completeSnapAsync = async (old, x, y) =>
    {
        const drop = await snapping.resolveAsync(x, y);
        if (pending !== old)
        {
            return;
        }
        finish();
        if (drop && old.version === Number(svg.dataset.connectionVersion))
        {
            const target = endpoint(drop);
            await invoke('ConnectPortsAsync', old.node, old.port, old.output, target.node, target.port, old.version);
        }
        else
        {
            await invoke('CancelPortConnectionAsync');
        }
    };

    /** ポインターの解放時に操作を確定または終了します。 */
    const up = event =>
    {
        if (!pending && event.pointerId === cancelledPointer)
        {
            cancelledPointer = null;
            suppressTerminalClick(event);
            event.preventDefault();
            event.stopPropagation();
            return;
        }
        if (!pending || event.pointerId !== pending.pointer)
        {
            return;
        }
        const drop = portAt(document.elementFromPoint(event.clientX, event.clientY));
        if (!drop && pending.moved)
        {
            pending.released = true;
            if (edgeFrame)
            {
                cancelAnimationFrame(edgeFrame);
                edgeFrame = edgeTime = 0;
            }
            suppressTerminalClick(event);
            event.preventDefault();
            event.stopPropagation();
            completeSnapAsync(pending, event.clientX, event.clientY).catch(() => cancel());
            return;
        }
        const old = finish();
        if (!old.moved)
        {
            return;
        }

        // The ordinary click-click path remains available.
        suppressTerminalClick(event);
        event.preventDefault();
        event.stopPropagation();
        if (drop)
        {
            const target = endpoint(drop);
            if (target.output !== old.output)
            {
                invoke('ConnectPortsAsync', old.node, old.port, old.output, target.node, target.port, old.version);
                return;
            }
        }
        invoke('CancelPortConnectionAsync');
    };

    /** 進行中の操作を取り消して一時状態を片付けます。 */
    const cancel = () =>
    {
        if (!pending)
        {
            return;
        }
        cancelledPointer = finish().pointer;
        invoke('CancelPortConnectionAsync');
    };

    /** フォーカス喪失時に保持中の入力状態を解除します。 */
    const blur = () =>
    {
        if (pending)
        {
            cancel();
        }
        else
        {
            invoke('CancelPortConnectionAsync');
        } // A click-click selection lives in .NET, not in pending.
    };

    /** 非表示への切替時に継続中の画面操作を整理します。 */
    const visibilityChanged = () =>
    {
        if (document.hidden)
        {
            cancel();
        }
    };

    /** 解放後の確認待ちをブラウザーの通常の捕捉終了で取り消しません。 */
    const captureLost = () =>
    {
        if (!pending?.released)
        {
            cancel();
        }
    };

    /** 外側のスクロールや寸法変更を候補の画面位置へ反映します。 */
    const refreshGeometry = event =>
    {
        if (event?.target !== svg.parentElement)
        {
            snapping?.invalidate();
        }
        drawPreview();
    };

    /** キー入力を対応する操作へ変換します。 */
    const key = event =>
    {
        const expectedEscape = 'Escape';

        if (event.key !== expectedEscape || !pending)
        {
            return;
        }
        cancel();
        event.preventDefault();
        event.stopPropagation();
    };

    /** クリック対象を確認して選択または操作を行います。 */
    const click = event =>
    {
        if (!suppressClick)
        {
            return;
        }
        const terminal = suppressClick;
        suppressClick = null;
        if ((event.pointerId !== undefined && event.pointerId !== terminal.pointer)
            || (1) < (Math.abs(event.clientX - terminal.x)) || (1) < (Math.abs(event.clientY - terminal.y)))
        {
            return;
        }
        event.preventDefault();
        event.stopImmediatePropagation();
    };

    svg.addEventListener('pointerdown', down, true);
    svg.addEventListener('pointerover', hover);
    svg.addEventListener('pointerleave', clearHover);
    svg.addEventListener('pointerout', eventOut);
    window.addEventListener('pointerdown', freshPointer, true);
    window.addEventListener('pointermove', move, true);
    window.addEventListener('pointerup', up, true);
    window.addEventListener('pointercancel', cancel);
    svg.addEventListener('lostpointercapture', captureLost);
    window.addEventListener('resize', refreshGeometry);
    window.addEventListener('scroll', refreshGeometry, true);
    window.addEventListener('keydown', key, true);
    window.addEventListener('blur', blur);
    window.addEventListener('click', click, true);
    svg.parentElement.addEventListener('scroll', drawPreview);
    document.addEventListener('visibilitychange', visibilityChanged);
    return {
        /** 現在の設定と状態から表示を更新します。 */
        update()
        {
            if (pending && pending.version !== Number(svg.dataset.connectionVersion))
            {
                cancel();
            }
            refreshGeometry();
            updateAnimation();
        },

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            disposed = true;
            finish();
            clearHover();
            visibility?.disconnect();
            for (const wire of visibleWires.keys())
            {
                wire.classList.remove('wire-visible');
            }
            visibleWires.clear();
            svg.removeEventListener('pointerover', hover);
            svg.removeEventListener('pointerleave', clearHover);
            svg.removeEventListener('pointerout', eventOut);
            svg.removeEventListener('pointerdown', down, true);
            window.removeEventListener('pointerdown', freshPointer, true);
            window.removeEventListener('pointermove', move, true);
            window.removeEventListener('pointerup', up, true);
            window.removeEventListener('pointercancel', cancel);
            svg.removeEventListener('lostpointercapture', captureLost);
            window.removeEventListener('resize', refreshGeometry);
            window.removeEventListener('scroll', refreshGeometry, true);
            window.removeEventListener('keydown', key, true);
            window.removeEventListener('blur', blur);
            window.removeEventListener('click', click, true);
            svg.parentElement.removeEventListener('scroll', drawPreview);
            document.removeEventListener('visibilitychange', visibilityChanged);
        }
    };
}
