// ブラウザー固有I/Oだけを公開する。画面・API・共同編集ロジックはC#が所有する。
window.contentsBrowser = {
    language: value =>
    {
        document.documentElement.lang = value;
    },

    read: (key, session = false) => !session && window.contentsEditorStore.owns(key) ? window.contentsEditorStore.read(key) : (session ? sessionStorage : localStorage).getItem(key),

    write: (key, value, session = false) => !session && window.contentsEditorStore.owns(key) ? window.contentsEditorStore.write(key, value) : (session ? sessionStorage : localStorage).setItem(key, value),

    readExperimentalPreferenceAsync: async (ignoreReadFailure = false) => (await import('./experimental.js')).readExperimentalPreference(ignoreReadFailure),

    saveExperimentalPreferenceAsync: async enabled => (await import('./experimental.js')).saveExperimentalPreference(enabled),

    download: (name, text, type = 'application/json') =>
    {
        const url = URL.createObjectURL(new Blob([text], { type })), link = document.createElement('a');
        link.href = url;
        link.download = name;
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    },

    downloadUrl: value =>
    {
        const url = new URL(value, document.baseURI);
        if (url.origin !== location.origin)
        {
            throw new Error("Only same-origin downloads are allowed.");
        }
        const link = document.createElement('a');
        link.href = url.href;
        link.download = '';
        link.click();
    },

    point: (svg, x, y) =>
    {
        const point = new DOMPoint(x, y).matrixTransform(svg.getScreenCTM().inverse());
        return { x: point.x, y: point.y };
    },

    beginDrag: (svg, x, y, id) =>
    {
        svg.setPointerCapture(id);
        return window.contentsBrowser.point(svg, x, y);
    },

    editing: () => document.activeElement?.matches('input,textarea,select') ?? false,

    dashboardActionFeedback: (board, id) =>
    {
        if (!board?.isConnected || matchMedia('(prefers-reduced-motion: reduce)').matches)
        {
            return;
        }
        const card = Array.from(board.querySelectorAll('[data-card-id]')).find(item => item.dataset.cardId === id);
        const feedback = card?.querySelector('.dashboard-action-feedback');
        if (!feedback)
        {
            return;
        }
        for (const animation of feedback.getAnimations())
        {
            animation.cancel();
        }
        feedback.animate([{ opacity: 0, transform: 'scale(1)' }, { opacity: 0.065, transform: 'scale(1.01)', offset: 0.2 },
            { opacity: 0, transform: 'scale(1.025)' }], { duration: 420, easing: 'ease-out' });
    },

    dashboardDropTarget: (board, x, y) =>
    {
        const rows = [];
        for (const card of board.querySelectorAll(':scope > .dashboard-cards > [data-card-id]'))
        {
            const rect = card.getBoundingClientRect(), last = rows.at(-1);
            if (!last || 1 < Math.abs(last.top - rect.top))
            {
                rows.push({ top: rect.top, bottom: rect.bottom, cards: [] });
            }
            const row = rows.at(-1);
            row.bottom = Math.max(row.bottom, rect.bottom);
            row.cards.push({ id: card.dataset.cardId, rect });
        }
        if (!rows.length || rows.at(-1).bottom < y)
        {
            return null;
        }
        if (rows.every(row => row.cards.length === 1))
        {
            return rows.find(row => y < (row.top + row.bottom) / 2)?.cards[0].id ?? null;
        }
        const index = rows.findIndex((row, i) => i === rows.length - 1 || y < (row.bottom + rows[i + 1].top) / 2);
        return rows[index].cards.find(card => x < card.rect.left + card.rect.width / 2)?.id ?? rows[index + 1]?.cards[0].id ?? null;
    },

    revealNavigation: () =>
    {
        const navigation = document.querySelector('nav.navigation'), active = navigation?.querySelector('a.active');
        if (!active)
        {
            return;
        }
        const container = navigation.getBoundingClientRect(), item = active.getBoundingClientRect();
        if (item.left < container.left)
        {
            navigation.scrollLeft += item.left - container.left;
        }
        else if ((container.right) < (item.right))
        {
            navigation.scrollLeft += item.right - container.right;
        }
    }
};

// ネイティブのドラッグ情報はブラウザーで設定し、Razorは同じ画面で開始した選択だけを受け取ります。
document.addEventListener('dragstart', event =>
{
    const card = event.target.closest?.('[data-contents-dashboard-card][draggable="true"]');
    if (card && event.dataTransfer)
    {
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('application/x-contents-dashboard-card', card.dataset.contentsDashboardCard);
        return;
    }
    const item = event.target.closest?.('[data-contents-node-type]');
    if (item && event.dataTransfer)
    {
        event.dataTransfer.effectAllowed = 'copy';
        event.dataTransfer.setData('application/x-contents-node', item.dataset.contentsNodeType);
    }
});

/** カード間や子コントロールへの移動も、ブラウザーが次の描画を待たずに受け付けます。 */
const allowDashboardDrop = event =>
{
    if (event.target.closest?.('.dashboard-board') && event.dataTransfer?.types.includes('application/x-contents-dashboard-card'))
    {
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
    }
};

document.addEventListener('dragenter', allowDashboardDrop);
document.addEventListener('dragover', allowDashboardDrop);

// サービス workerの実状態だけをRazorへ返す。編集中の版を自動切替しない。
const contentsPwa = { registration: null, error: null };
window.contentsBrowser.registerPwa = async() =>
{
    if (!('serviceWorker' in navigator))
    {
        contentsPwa.error = 'unsupported';
        return;
    }
    try
    {
        let version = '';
        try
        {
            const response = await fetch('/client/health', { cache: 'no-store', signal: AbortSignal.timeout(5000) });
            if (response.ok)
            {
                version = (await response.json()).buildVersion ?? '';
            }
        }
        catch
        { /* Hostを確認できない間は、取得済みの画面資材と登録情報を維持します。 */ }
        if (!version)
        {
            contentsPwa.registration = await navigator.serviceWorker.getRegistration();
            if (contentsPwa.registration)
            {
                return;
            }
        }
        contentsPwa.registration = await navigator.serviceWorker.register('service-worker.js' + (version ? '?ui=' + encodeURIComponent(version) : ''), { scope: '/', updateViaCache: 'none' });
    }
    catch
    {
        contentsPwa.error = 'registration.failed';
    }
};
window.contentsBrowser.pwaStatus = async() =>
{
    const registration = contentsPwa.registration;
    const state = { supported: 'serviceWorker' in navigator, registered: !!registration, controlled: !!navigator.serviceWorker?.controller, waiting: !!registration?.waiting, ready: false, version: null, error: contentsPwa.error };
    const controller = navigator.serviceWorker?.controller;
    if (controller)
    {
        const answer = await new Promise(resolve =>
        {
            const channel = new MessageChannel();

            /** 処理結果を確定し、待機と監視を終了します。 */
            const finish = value =>
            {
                clearTimeout(timeout);
                channel.port1.close();
                resolve(value);
            };

            const timeout = setTimeout(() => finish(null), 1000);
            channel.port1.onmessage = event => finish(event.data);
            controller.postMessage({ type: 'contents.pwa.status' }, [channel.port2]);
        });
        if (answer?.published === true)
        {
            state.ready = answer.ready === true;
            state.version = answer.version;
        }
    }
    return state;
};

// 最前面の表示領域を使い、SVG内のノードやスクロール領域で切り取られないようにする。
const contentsDialogFocus = new Map();
window.contentsBrowser.openDialog = (element, id) =>
{
    if (!element?.isConnected)
    {
        return;
    }
    const previous = document.activeElement;
    if (element instanceof HTMLDialogElement && !element.open)
    {
        element.showModal();
    }

    /** 進行中の操作を取り消して一時状態を片付けます。
     * @returns 呼出し元が使用する判定結果です。
     */
    const cancel = event => event.preventDefault();

    /** 現在の編集状態に対応する操作対象を取得します。
     * @returns 呼出し元が使用する値です。
     */
    const controls = () => Array.from(element.querySelectorAll('button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),a[href],[tabindex="0"]')).filter(control => (0) < (control.getClientRects().length));

    /** 画面内のキー操作を対象の入力欄へ留めます。 */
    const trap = event =>
    {
        const expectedTab = 'Tab';

        if (event.key !== expectedTab)
        {
            return;
        }
        const items = controls(), first = items[0], last = items.at(-1);
        if (!first)
        {
            event.preventDefault();
            element.focus();
            return;
        }
        if (event.shiftKey && (document.activeElement === first || document.activeElement === element))
        {
            event.preventDefault();
            last.focus();
        }
        else if (!event.shiftKey && document.activeElement === last)
        {
            event.preventDefault();
            first.focus();
        }
    };

    element.addEventListener('keydown', trap);
    element.addEventListener('cancel', cancel);
    contentsDialogFocus.set(id, { element, previous, trap, cancel });
    (controls()[0] ?? element).focus();
};
window.contentsBrowser.closeDialog = id =>
{
    const state = contentsDialogFocus.get(id);
    if (!state)
    {
        return;
    }
    state.element.removeEventListener('keydown', state.trap);
    contentsDialogFocus.delete(id);
    state.element.removeEventListener('cancel', state.cancel);
    if (state.element instanceof HTMLDialogElement && state.element.open)
    {
        state.element.close();
    }
    if (state.previous?.isConnected)
    {
        state.previous.focus();
    }
};
