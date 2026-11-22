// Own the series and its offset together so a render never paints a new path with an old origin.
const charts = new Map();
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
let frame;
let lastDraw = -Infinity;

/** 対象の配置位置を計算して反映します。 */
function position(state, now)
{
    // Use the same ClientHost clock as the samples; subsequent frames use monotonic elapsed time.
    const seconds = (0) < (state.origin) && !reducedMotion.matches ? (state.epoch + now - state.origin) / 1000 : 0;
    state.series.setAttribute("transform", `translate(${-seconds} 0)`);
}

/** 重複した描画要求を次の一回にまとめます。 */
function schedule()
{
    if (frame === undefined && charts.size && !reducedMotion.matches)
    {
        frame = requestAnimationFrame(draw);
    }
}

/** 準備された値を現在の描画領域へ反映します。 */
function draw()
{
    const now = performance.now();
    if (now - lastDraw < 33)
    {
        frame = requestAnimationFrame(draw);
        return;
    }
    lastDraw = now;
    frame = undefined;
    for (const [chart, state] of charts)
    {
        if (!chart.isConnected)
        {
            charts.delete(chart);
            continue;
        }
        if (chart.closest("[hidden]"))
        {
            continue;
        }
        position(state, now);
    }
    schedule();
}

/** 現在の設定と状態から表示を更新します。 */
export function update(chart, path, origin, serverNow, highlight = "")
{
    if (!chart?.isConnected)
    {
        return;
    }
    let state = charts.get(chart);
    if (!state)
    {
        const series = chart.querySelector("[data-series]");
        state = { series, line: series.querySelector("path"), highlight: series.querySelector(".trend-highlight"), origin: 0 };
        charts.set(chart, state);
    }
    state.epoch = Number(serverNow) - performance.now();
    state.origin = Number(origin) || 0;
    if (state.path !== path)
    {
        state.line.setAttribute("d", path);
        state.path = path;
    }
    if (state.highlightPath !== highlight)
    {
        state.highlight.setAttribute("d", highlight);
        state.highlightPath = highlight;
    }
    position(state, performance.now());
    schedule();
}

/** 対象の監視を解除します。 */
export function unobserve(chart)
{
    charts.delete(chart);
    for (const element of charts.keys())
    {
        if (!element.isConnected)
        {
            charts.delete(element);
        }
    }
    if (!charts.size && frame !== undefined)
    {
        cancelAnimationFrame(frame);
        frame = undefined;
    }
}

reducedMotion.addEventListener("change", () =>
{
    if (frame !== undefined)
    {
        cancelAnimationFrame(frame);
        frame = undefined;
    }
    const now = performance.now();
    for (const state of charts.values())
    {
        position(state, now);
    }
    schedule();
});
