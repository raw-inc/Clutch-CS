// Shared edge motion for accepted node and connection drags. Coordinates are viewport pixels.
/** グラフの端に近い操作位置に合わせてスクロールします。
 * @returns 呼出し元が使用する値です。
 */
export function scrollGraphEdge(svg, x, y, elapsed)
{
    const viewport = svg.parentElement, rect = viewport.getBoundingClientRect();
    const left = Math.max(0, rect.left + viewport.clientLeft), top = Math.max(0, rect.top + viewport.clientTop);
    const right = Math.min(window.innerWidth, rect.left + viewport.clientLeft + viewport.clientWidth);
    const bottom = Math.min(window.innerHeight, rect.top + viewport.clientTop + viewport.clientHeight);

    /** 端からの距離に応じたスクロール速度を求めます。
     * @returns 呼出し元が使用する値です。
     */
    const speed = (position, start, end) =>
    {
        const band = Math.min(48, (end - start) / 2);
        if (band <= 0)
        {
            return 0;
        }
        if (position < start + band)
        {
            return -720 * Math.min(1, (start + band - position) / band);
        }
        if ((end - band) < (position))
        {
            return 720 * Math.min(1, (position - end + band) / band);
        }
        return 0;
    };

    // Do not create negative graph coordinates. The right/bottom canvas can grow.
    const dx = Math.max(-viewport.scrollLeft, speed(x, left, right) * elapsed);
    const dy = Math.max(-viewport.scrollTop, speed(y, top, bottom) * elapsed);
    if (!dx && !dy)
    {
        return false;
    }
    const nextLeft = viewport.scrollLeft + dx, nextTop = viewport.scrollTop + dy;

    /** 必要な操作範囲まで表示領域を拡張します。 */
    const extend = (side, amount) =>
    {
        if ((0) < (amount))
        {
            svg.style[side] = `${(parseFloat(svg.style[side]) || 0) + amount}px`;
        }
    };

    extend('marginRight', nextLeft - Math.max(0, viewport.scrollWidth - viewport.clientWidth));
    extend('marginBottom', nextTop - Math.max(0, viewport.scrollHeight - viewport.clientHeight));
    viewport.scrollLeft = nextLeft;
    viewport.scrollTop = nextTop;
    return true;
}
