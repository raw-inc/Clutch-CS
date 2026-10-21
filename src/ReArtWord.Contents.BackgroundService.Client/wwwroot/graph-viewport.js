/** 端の余白を必要なだけ増やし、画面ピクセルの移動を既存スクロールへ反映します。 */
export function scrollGraphBy(svg, viewport, dx, dy)
{
    if (![dx, dy].every(Number.isFinite))
    {
        return;
    }

    /** 既存のズーム余白へ追加します。 */
    const extend = (side, amount) =>
    {
        svg.style[side] = `${(parseFloat(svg.style[side]) || 0) + amount}px`;
    };

    let left = viewport.scrollLeft + dx, top = viewport.scrollTop + dy;
    if (left < 0)
    {
        extend('marginLeft', -left);
        left = 0;
    }
    if (top < 0)
    {
        extend('marginTop', -top);
        top = 0;
    }
    const maximumLeft = Math.max(0, viewport.scrollWidth - viewport.clientWidth);
    const maximumTop = Math.max(0, viewport.scrollHeight - viewport.clientHeight);
    if (maximumLeft < left)
    {
        extend('marginRight', left - maximumLeft);
    }
    if (maximumTop < top)
    {
        extend('marginBottom', top - maximumTop);
    }
    viewport.scrollLeft = left;
    viewport.scrollTop = top;
}

/** ノード座標を画面へ変換し、倍率を維持したまま表示中心へ移します。 */
export function centerGraphPoint(svg, viewport, x, y)
{
    const matrix = svg.getScreenCTM();
    if (!matrix || ![x, y].every(Number.isFinite))
    {
        return;
    }
    const point = new DOMPoint(x, y).matrixTransform(matrix), bounds = viewport.getBoundingClientRect();
    scrollGraphBy(svg, viewport, point.x - bounds.left - viewport.clientLeft - viewport.clientWidth / 2,
        point.y - bounds.top - viewport.clientTop - viewport.clientHeight / 2);
}
