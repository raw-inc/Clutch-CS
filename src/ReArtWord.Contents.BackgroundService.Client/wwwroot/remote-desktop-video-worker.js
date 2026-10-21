// The UI owns connection/input/audio. A worker owns one decoder, canvas and all
// compressed/decoded frame resources. Chromium can opt in from connection settings.
/** 映像のWorker描画に必要な機能があるか確認します。
 * @returns 呼出し元が使用する値です。
 */
export function supportsDesktopVideoWorker(canvas, preference = 'automatic')
{
    const agent = globalThis.navigator?.userAgent ?? '';
    return (preference === 'worker' || preference === 'automatic' && /Firefox\//.test(agent))
        && typeof Worker === 'function' && typeof canvas.transferControlToOffscreen === 'function';
}

/** 映像Workerの通信と終了処理を構成します。
 * @returns 呼出し元が使用する値です。
 */
export function createDesktopVideoWorker(monitor, canvas, options)
{
    let worker, closed = false, closingTimer, queuedBytes = 0, queuedFrames = 0, ready = false, clockPending = false;
    const started = performance.now();
    let lastResponse = started;
    const stats = { delay: 0, presented: 0, dropped: 0, hasDisplayed: false, preference: options.acceleration, renderer: 'Worker', decodeDelay: 0, drawCost: 0, flushes: 0, replays: 0 };

    /** 現在の経路を終了し、利用可能な代替処理へ通知します。 */
    function fallback(reason)
    {
        if (closed || !options.active())
        {
            return;
        }
        close();
        options.backendFallback(reason);
    }

    /** 対象を閉じ、関連する状態を整理します。 */
    function close()
    {
        if (closed)
        {
            return;
        }
        closed = true;
        if (!worker)
        {
            return;
        }

        // Let VideoFrames and GPU objects close explicitly before terminating.
        worker.postMessage({ type: 'close' });
        closingTimer = setTimeout(() => worker.terminate(), 1000);
    }

    try
    {
        worker = new Worker(new URL('./remote-desktop-worker.js', import.meta.url), { type: 'module' });
        worker.onmessage = event =>
        {
            const expectedClosed = 'closed';
            const expectedAccepted = 'accepted';
            const expectedReady = 'ready';
            const expectedClockAccepted = 'clock-accepted';
            const expectedStats = 'stats';
            const expectedRefresh = 'refresh';
            const expectedFallback = 'fallback';
            const expectedBackendFailed = 'backend-failed';

            const message = event.data;
            if (message.type === expectedClosed)
            {
                clearTimeout(closingTimer);
                worker.terminate();
                return;
            }
            if (closed || !options.active())
            {
                return;
            }
            lastResponse = performance.now();
            switch (message.type)
            {
                case expectedAccepted:
                    {
                        queuedBytes = Math.max(0, queuedBytes - message.bytes);
                        queuedFrames = Math.max(0, queuedFrames - 1);
                    }
                    break;
                case expectedReady:
                    {
                        ready = true;
                    }
                    break;
                case expectedClockAccepted:
                    {
                        clockPending = false;
                    }
                    break;
                case expectedStats:
                    {
                        Object.assign(stats, message);
                    }
                    break;
                case expectedRefresh:
                    {
                        options.refresh(message.reason);
                    }
                    break;
                case expectedFallback:
                    {
                        options.fallback(message.reason);
                    }
                    break;
                case expectedBackendFailed:
                    {
                        fallback(message.reason);
                    }
                    break;
            }
        };
        worker.onerror = event =>
        {
            event.preventDefault();
            fallback('Video worker failed.');
        };
        const surface = canvas.transferControlToOffscreen();
        worker.postMessage({ type: 'init', canvas: surface, monitor, settings: {
            acceleration: options.acceleration, frameRate: options.frameRate, maximumBitrateKbps: options.maximumBitrateKbps,
            bufferBudget: options.bufferBudget?.() ?? 150
        } }, [surface]);
    }
    catch (error)
    {
        queueMicrotask(() => fallback(error.message));
    }
    return {
        /** 使用するコーデック情報を更新します。 */
        setCodec(value)
        {
            if (!closed)
            {
                worker?.postMessage({ type: 'codec', value });
            }
        },

        /** 参照関係が途切れた映像を再開可能な状態へ戻します。 */
        discontinuity()
        {
            if (!closed)
            {
                worker?.postMessage({ type: 'discontinuity' });
            }
        },

        /** 受信データを検証し、現在の状態へ取り込みます。 */
        receive(payload, key, timestamp)
        {
            if (closed || !worker)
            {
                return;
            }
            if ((256) <= (queuedFrames) || (8 * 1024 * 1024) < (queuedBytes + payload.buffer.byteLength))
            {
                fallback('Video worker queue exceeded its limit.');
                return;
            }
            queuedFrames++;
            queuedBytes += payload.buffer.byteLength;
            worker.postMessage({ type: 'frame', payload, key, timestamp, arrived: performance.timeOrigin + performance.now() }, [payload.buffer]);
        },

        /** 経過時間に応じて状態と期限を更新します。 */
        tick()
        {
            if (closed || !worker)
            {
                return;
            }
            if (!ready && (3000) < (performance.now() - started))
            {
                fallback('Video worker did not initialize.');
                return;
            }
            if ((3000) < (performance.now() - lastResponse))
            {
                fallback('Video worker stopped responding.');
                return;
            }
            if (!clockPending)
            {
                clockPending = true;
                worker.postMessage({ type: 'clock', value: options.audible?.() ?? null, at: performance.timeOrigin + performance.now(), budget: options.bufferBudget?.() ?? 150 });
            }
        },

        /** 準備された値を現在の描画領域へ反映します。
         * @returns 呼出し元が使用する値です。
         */
        draw()
        {
            return false;
        },

        close,

        get hasPending()
        {
            return false;
        },

        get hasDisplayed()
        {
            return stats.hasDisplayed;
        },

        get delay()
        {
            return stats.delay;
        },

        get presented()
        {
            return stats.presented;
        },

        get dropped()
        {
            return stats.dropped;
        },

        get preference()
        {
            return stats.preference;
        },

        get renderer()
        {
            return 'Worker · ' + stats.renderer;
        },

        get decodeDelay()
        {
            return stats.decodeDelay;
        },

        get drawCost()
        {
            return stats.drawCost;
        },

        get flushes()
        {
            return stats.flushes;
        },

        get replays()
        {
            return stats.replays;
        },

        get lastPresented()
        {
            return stats.lastPresented;
        }
    };
}
