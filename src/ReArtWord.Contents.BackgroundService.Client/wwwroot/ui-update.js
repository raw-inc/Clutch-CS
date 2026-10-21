// Root and manual recovery share installation and activation; only their failure navigation differs.
const automatic = document.body.dataset.updateMode === 'automatic';
const splash = document.querySelector('#splash'), progress = document.querySelector('#progress'), status = document.querySelector('#status'), button = document.querySelector('#update');
let observed = null, finished = false;

/** 指定した状態へ移るまで期限付きで待機します。
 * @returns 呼出し元が使用する値です。
 */
function waitFor(worker, target)
{
    return new Promise((resolve, reject) =>
    {
        const timer = setTimeout(() => finish(new Error('Update timed out. Please retry.')), 120000);

        /** 状態変更を確認して待機中の処理へ通知します。 */
        const changed = () =>
        {
            const expectedInstalled = 'installed';
            const expectedRedundant = 'redundant';

            if (worker.state === target || (target === expectedInstalled && ['activating', 'activated'].includes(worker.state)))
            {
                finish();
            }
            else if (worker.state === expectedRedundant)
            {
                finish(new Error('UI assets could not be verified. Please retry.'));
            }
        };

        /** 処理結果を確定し、待機と監視を終了します。 */
        function finish(error)
        {
            clearTimeout(timer);
            worker.removeEventListener('statechange', changed);
            if (error)
            {
                reject(error);
            }
            else
            {
                resolve();
            }
        }

        worker.addEventListener('statechange', changed);
        changed();
    });
}

/** 更新用Workerへ要求を送り、期限付きで応答を待ちます。
 * @returns 呼出し元が使用する値です。
 */
function request(worker, type)
{
    return new Promise((resolve, reject) =>
    {
        const channel = new MessageChannel(), timer = setTimeout(() => finish(new Error('Update worker did not respond.')), 10000);

        /** 処理結果を確定し、待機と監視を終了します。 */
        function finish(error, value)
        {
            clearTimeout(timer);
            channel.port1.close();
            if (error)
            {
                reject(error);
            }
            else
            {
                resolve(value);
            }
        }

        channel.port1.onmessage = event => finish(null, event.data);
        try
        {
            worker.postMessage({ type }, [channel.port2]);
        }
        catch(error)
        {
            finish(error);
        }
    });
}

/** 取得済みの件数を進捗表示へ反映します。 */
function updateProgress(data)
{
    if (finished || !Number.isInteger(data?.completed) || !Number.isInteger(data.total) || data.total <= 0)
    {
        return;
    }
    progress.max = data.total;
    progress.value = Math.max(progress.value, Math.min(data.completed, data.total));
}

/** 受信データを検証し、現在の状態へ取り込みます。 */
function receive(event)
{
    const expectedContentsPwaProgress = 'contents.pwa.progress';

    if (event.source === observed && event.data?.type === expectedContentsPwaProgress)
    {
        updateProgress(event.data);
    }
}

/** 配信元の表示名と画像を取得して画面へ反映します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 */
async function brandingAsync()
{
    try
    {
        const response = await fetch('/client/branding', { cache: 'no-store', signal: AbortSignal.timeout(3000) });
        if (!response.ok)
        {
            return;
        }
        const value = await response.json();
        window.contentsBranding.setFavicon(value.favicon ?? value.headerLogo);
        if (typeof value.contentsName === 'string')
        {
            document.querySelector('#brand-name').textContent = value.contentsName;
        }
        if (typeof value.subtitle === 'string')
        {
            document.querySelector('#brand-subtitle').textContent = value.subtitle;
        }
        for (const [field, id] of [['headerLogo', 'brand-mark'], ['contentsLogo', 'brand-name']])
        {
            const expectedContentslogo = 'contentsLogo';

            if (typeof value[field] !== 'string' || !value[field].startsWith('/client/branding/'))
            {
                continue;
            }
            const image = new Image();
            image.alt = field === expectedContentslogo ? (value.contentsName ?? 'Clutch-CS') : '';
            image.onload = () => document.getElementById(id).replaceChildren(image);
            image.src = value[field];
        }
    }
    catch
    {/* 表示情報を取得できない間も、同梱ロゴで画面を利用できます。 */}
}

/** 更新用Workerの進捗とインストール完了を待ちます。
 * @returns 処理の完了と結果を伝えるPromiseです。
 */
async function watchAsync(worker)
{
    observed = worker;
    splash.hidden = false;
    status.textContent = '画面を更新しています / Updating UI';

    // The installer may have already downloaded assets before register() returned.
    request(worker, 'contents.pwa.progress').then(updateProgress).catch(() =>
    {});
    await waitFor(worker, 'installed');
    progress.max = 1;
    progress.value = 1;
}

/** 更新資材を検証して画面の版を切り替えます。
 * @returns 処理の完了と結果を伝えるPromiseです。
 */
async function runAsync()
{
    if (button)
    {
        button.disabled = true;
    }
    finished = false;
    progress.removeAttribute('value');
    observed = null;
    navigator.serviceWorker?.addEventListener('message', receive);
    try
    {
        const expectedActivating = 'activating';

        if (!navigator.serviceWorker)
        {
            throw new Error('A secure browser context is required.');
        }
        const response = await fetch('/client/health', { cache: 'no-store', signal: AbortSignal.timeout(5000) });
        if (!response.ok)
        {
            throw new Error('ClientHost is unavailable.');
        }
        const health = await response.json();
        if (typeof health.buildVersion !== 'string')
        {
            throw new Error('ClientHost version is unavailable.');
        }
        const registration = await navigator.serviceWorker.register('/service-worker.js?ui=' + encodeURIComponent(health.buildVersion), { scope: '/', updateViaCache: 'none' });
        if (!registration.installing)
        {
            await registration.update();
        }
        if (registration.installing)
        {
            await watchAsync(registration.installing);
        }
        const waiting = registration.waiting;
        if (waiting)
        {
            splash.hidden = false;
            progress.max = 1;
            progress.value = 1;
            if (!(await request(waiting, 'contents.pwa.activate')).accepted)
            {
                if (!automatic)
                {
                    throw new Error('このサイトの他のタブ・PWAを閉じてから、もう一度更新してください。\nClose the other tabs and PWA windows for this site, then retry.');
                }
            }
            else
            {
                await waitFor(waiting, 'activated');
            }
        }
        else if (registration.active?.state === expectedActivating)
        {
            await waitFor(registration.active, 'activated');
        }
        location.replace('/dashboard');
    }
    catch(error)
    {
        if (automatic)
        {
            location.replace('/dashboard');
        }
        else
        {
            splash.hidden = false;
            status.textContent = error.message;
            if (button)
            {
                button.disabled = false;
            }
        }
    }
    finally
    {
        finished = true;
        navigator.serviceWorker?.removeEventListener('message', receive);
    }
}

/** 画面更新の開始または後処理で確認できなかった失敗を表示します。 */
function reportFailure(error)
{
    splash.hidden = false;
    status.textContent = error.message;
    if (button)
    {
        button.disabled = false;
    }
}

brandingAsync().catch(reportFailure);
if (automatic)
{
    runAsync().catch(reportFailure);
}
else
{
    button.addEventListener('click', () =>
    {
        runAsync().catch(reportFailure);
    });
}
