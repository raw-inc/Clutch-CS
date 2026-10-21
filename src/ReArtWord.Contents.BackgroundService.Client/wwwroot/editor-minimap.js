/** 選択範囲全体が収まる倍率と、移動先の中心を求めます。
 * @returns 既存の5～150%へ制限した倍率と中心です。
 */
export function fitMapRectangle(rectangle, width, height)
{
    if (![rectangle.x, rectangle.y, rectangle.width, rectangle.height, width, height].every(Number.isFinite)
        || rectangle.width <= 0 || rectangle.height <= 0 || width <= 0 || height <= 0)
    {
        return null;
    }
    return { x: rectangle.x + rectangle.width / 2, y: rectangle.y + rectangle.height / 2,
        zoom: Math.max(.05, Math.min(1.5, width / rectangle.width, height / rectangle.height)) };
}

/** ミニマップの通常ドラッグとShift範囲選択を、画面描画の間隔で反映します。
 * @returns 所有するポインター操作の取消と終了処理です。
 */
export function attachMinimapPointer(map, selection, options)
{
    let drag = null, frame = 0;

    /** 初期座標系を保ち、マップ外へ出ても配置範囲の端までに制限します。
     * @returns ワークフロー座標です。
     */
    function point(event)
    {
        const position = new DOMPoint(event.clientX, event.clientY).matrixTransform(drag.inverse), bounds = drag.bounds;
        return { x: Math.max(bounds.x, Math.min(bounds.x + bounds.width, position.x)),
            y: Math.max(bounds.y, Math.min(bounds.y + bounds.height, position.y)) };
    }

    /** 移動方向に依存しない選択矩形を作ります。
     * @returns マップと同じ座標系の矩形です。
     */
    function rectangle()
    {
        return { x: Math.min(drag.first.x, drag.last.x), y: Math.min(drag.first.y, drag.last.y),
            width: Math.abs(drag.last.x - drag.first.x), height: Math.abs(drag.last.y - drag.first.y) };
    }

    /** 次のフレームを取り消し、キャプチャと選択枠を解放します。 */
    function cancel()
    {
        cancelAnimationFrame(frame);
        frame = 0;
        const previous = drag;
        drag = null;
        selection.setAttribute('visibility', 'hidden');
        if (previous && map.hasPointerCapture(previous.id))
        {
            map.releasePointerCapture(previous.id);
        }
    }

    /** レイアウト更新に合わせて、押下中の移動または選択枠を描画します。 */
    function paint()
    {
        frame = 0;
        if (!drag)
        {
            return;
        }
        if (!options.enabled() || drag.version !== options.version())
        {
            cancel();
            return;
        }
        if (drag.selecting)
        {
            for (const [key, value] of Object.entries(rectangle()))
            {
                selection.setAttribute(key, String(value));
            }
            selection.setAttribute('visibility', 'visible');
        }
        else
        {
            options.move(drag.last);
        }
    }

    /** 主ポインターの左操作だけを開始し、Shift状態は開始時に固定します。 */
    function down(event)
    {
        const matrix = map.getScreenCTM();
        if (drag || event.button !== 0 || event.isPrimary === false || !options.enabled() || !matrix)
        {
            return;
        }
        const bounds = map.viewBox.baseVal;
        drag = { id: event.pointerId, inverse: matrix.inverse(), bounds: { x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height },
            selecting: event.shiftKey, startX: event.clientX, startY: event.clientY, version: options.version() };
        drag.first = drag.last = point(event);
        map.setPointerCapture(event.pointerId);
        event.preventDefault();
        event.stopPropagation();
        paint();
    }

    /** 最新位置を保持し、複数のmove通知は次の一描画へまとめます。 */
    function move(event)
    {
        if (!drag || event.pointerId !== drag.id)
        {
            return;
        }
        drag.last = point(event);
        event.preventDefault();
        event.stopPropagation();
        frame ||= requestAnimationFrame(paint);
    }

    /** 解放位置まで移動し、十分な面積を選択した場合だけ倍率を変更します。 */
    function up(event)
    {
        if (!drag || event.pointerId !== drag.id)
        {
            return;
        }
        cancelAnimationFrame(frame);
        drag.last = point(event);
        paint();
        if (!drag)
        {
            return;
        }
        const selected = drag.selecting && 3 <= Math.abs(event.clientX - drag.startX) && 3 <= Math.abs(event.clientY - drag.startY);
        const bounds = rectangle();
        cancel();
        event.preventDefault();
        event.stopPropagation();
        if (selected)
        {
            options.select(bounds);
        }
    }

    /** 別ポインターの終了通知で、現在の操作を取り消しません。 */
    function pointerCancel(event)
    {
        if (drag?.id === event.pointerId)
        {
            cancel();
        }
    }

    /** 非表示の画面へポインター操作を持ち越しません。 */
    function visibility()
    {
        if (document.hidden)
        {
            cancel();
        }
    }

    map.addEventListener('pointerdown', down);
    map.addEventListener('pointermove', move);
    map.addEventListener('pointerup', up);
    map.addEventListener('pointercancel', pointerCancel);
    map.addEventListener('lostpointercapture', pointerCancel);
    window.addEventListener('blur', cancel);
    window.addEventListener('resize', cancel);
    document.addEventListener('visibilitychange', visibility);
    return { cancel,

        /** 画面終了時に購読と描画予約をすべて解除します。 */
        dispose()
        {
            cancel();
            map.removeEventListener('pointerdown', down);
            map.removeEventListener('pointermove', move);
            map.removeEventListener('pointerup', up);
            map.removeEventListener('pointercancel', pointerCancel);
            map.removeEventListener('lostpointercapture', pointerCancel);
            window.removeEventListener('blur', cancel);
            window.removeEventListener('resize', cancel);
            document.removeEventListener('visibilitychange', visibility);
        }
    };
}
