import { createDesktopVideo } from './remote-desktop-video.js';
import { createDesktopRenderer } from './remote-desktop-renderer.js';

let video, renderer, render = 0, timer = 0, closed = false, clock = null, clockAt = 0, lastReport = 0, bufferBudget = 150;

/** 経過時間の基準となる現在時刻を取得します。
 * @returns 呼出し元が使用する値です。
 */
const now = () => performance.timeOrigin + performance.now();

/** 音声の時計が有効な間だけ現在の再生位置を返します。
 * @returns 呼出し元が使用する値です。
 */
function audible()
{
    const elapsed = now() - clockAt;
    return clock == null || (250) < (elapsed) ? null : clock + Math.max(0, elapsed) * 1000;
}

/** 失敗を通知し、後続の処理を停止します。 */
function fail(error)
{
    if (!closed)
    {
        postMessage({ type: 'backend-failed', reason: String(error?.message ?? error).slice(0, 240) });
    }
    stop();
}

/** 準備された値を現在の描画領域へ反映します。 */
function draw()
{
    render = 0;
    if (closed || !video)
    {
        return;
    }
    try
    {
        if (video.draw(audible()))
        {
            schedule();
        }
    }
    catch (error)
    {
        fail(error);
    }
}

/** 重複した描画要求を次の一回にまとめます。 */
function schedule()
{
    if (!render && !closed)
    {
        render = setTimeout(draw, 8);
    }
}

/** 現在の統計と状態を観測側へ報告します。 */
function report()
{
    if (!video || closed)
    {
        return;
    }
    postMessage({ type: 'stats', delay: video.delay, presented: video.presented, dropped: video.dropped,
        hasDisplayed: video.hasDisplayed, preference: video.preference, renderer: renderer.name,
        decodeDelay: video.decodeDelay, drawCost: video.drawCost, flushes: video.flushes, replays: video.replays, lastPresented: video.lastPresented });
}

/** 所有する処理と監視を停止します。 */
function stop()
{
    if (closed)
    {
        return;
    }
    closed = true;
    clearTimeout(render);
    clearInterval(timer);
    if (video)
    {
        video.close();
    }
    else
    {
        renderer?.close();
    }
}

self.onmessage = event =>
{
    const expectedClose = 'close';

    const message = event.data;
    if (message.type === expectedClose)
    {
        stop();
        postMessage({ type: 'closed' });
        self.close();
        return;
    }
    if (closed)
    {
        return;
    }
    try
    {
        const expectedInit = 'init';
        const expectedCodec = 'codec';
        const expectedClock = 'clock';
        const expectedDiscontinuity = 'discontinuity';
        const expectedFrame = 'frame';

        switch (message.type)
        {
            case expectedInit:
                {
                    renderer = createDesktopRenderer(message.canvas, true);
                    bufferBudget = message.settings.bufferBudget;
                    video = createDesktopVideo(message.monitor, message.canvas, {
                        ...message.settings, renderer,

                        active: () => !closed,

                        schedule, audible,

                        bufferBudget: () => bufferBudget,

                        refresh: reason => postMessage({ type: 'refresh', reason }),

                        fallback: reason => postMessage({ type: 'fallback', reason }),

                        error: fail
                    });
                    timer = setInterval(() =>
                    {
                        try
                        {
                            video.tick(performance.now());
                            if (video.hasPending)
                            {
                                schedule();
                            }
                            if ((250) < (performance.now() - lastReport))
                            {
                                lastReport = performance.now();
                                report();
                            }
                        }
                        catch (error)
                        {
                            fail(error);
                        }
                    }, 25);
                    postMessage({ type: 'ready' });
                }
                break;
            case expectedCodec:
                {
                    video.setCodec(message.value);
                }
                break;
            case expectedClock:
                {
                    clock = message.value;
                    clockAt = message.at;
                    bufferBudget = message.budget;
                    postMessage({ type: 'clock-accepted' });
                }
                break;
            case expectedDiscontinuity:
                {
                    video.discontinuity();
                }
                break;
            case expectedFrame:
                {
                    video.receive(message.payload, message.key, message.timestamp, message.arrived - performance.timeOrigin);
                    postMessage({ type: 'accepted', bytes: message.payload.buffer.byteLength });
                }
                break;
        }
    }
    catch (error)
    {
        fail(error);
    }
};
