import { keys } from './labels.js';
import { node, input, checkbox, icon, action, badge, empty } from './dom.js';
import { networkView } from './network.js';
import { hostsView } from './hosts.js';
import { firewallView } from './firewall.js';
import { environmentView } from './environment.js';
import { displayView } from './display.js';
import { powerView } from './power.js';
import { powerSchedulesView, powerScheduleKeys } from './power-schedules.js';
import { windowsUpdateView } from './windows-update.js';
import { loginView } from './login.js';
import { wakeOnLanView } from './wake-on-lan.js';
import { showPreview, showHistory } from './operations.js';

const screens = new WeakMap();
const areas = [['network', 'NetworkIF', 'network', networkView], ['hosts', 'Hosts', 'file', hostsView],
    ['firewall', 'Firewall', 'shield', firewallView], ['environment', 'PATH', 'code', environmentView], ['display', 'Display', 'monitor', displayView], ['power', 'Power', 'power', powerView],
    ['powerSchedules', 'powerSchedule.title', 'power', powerSchedulesView], ['windowsUpdate', 'Windows Update', 'refresh', windowsUpdateView], ['login', 'Login', 'user', loginView], ['wakeOnLan', 'wake.title', 'power', wakeOnLanView]];

/** 画面の文言と表示するキーを対応付けます。
 * @returns 呼出し元が使用する値です。
 */
export function textKeys()
{
    return [...new Set([...keys, ...powerScheduleKeys])];
}

/** 対象画面の処理と表示を解放します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function unmount(element)
{
    const screen = screens.get(element);
    if (screen)
    {
        screen.abort.abort();
        clearTimeout(screen.timer);
        screen.views.forEach(item => item.view.dispose?.());
        screen.dialogs.forEach(dialog => dialog.remove());
    }
    screens.delete(element);
    element.replaceChildren();
}

/** 画面の寿命に対応する接続・操作・表示を構成します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function mount(element, labels)
{
    await unmount(element);
    if (!document.querySelector('link[data-host-manager]'))
    {
        const css = node('link');
        css.rel = 'stylesheet';
        css.href = 'host-manager/style.css';
        css.dataset.hostManager = '';
        document.head.append(css);
    }
    const screen = { abort: new AbortController(), hosts: [], selected: new Set(), area: 'network', generation: 0, dialogs: new Set(), views: new Map(), history: [], pending: new Set() };
    screens.set(element, screen);
    const title = node('div', '', 'hm-page-heading'), titleCopy = node('div'), titleTools = node('div', '', 'hm-actions');
    titleCopy.append(node('h1', 'HostManager (β)'), node('p', labels['Manage operating system settings across discovered hosts.'], 'hm-muted'));
    title.append(titleCopy, titleTools);
    const workspace = node('div', '', 'hm-workspace'), sidebar = node('aside', '', 'hm-sidebar'), main = node('div', '', 'hm-main');
    const tabs = node('div', '', 'hm-tabs');
    tabs.setAttribute('role', 'tablist');
    tabs.setAttribute('aria-label', 'HostManager');
    const message = node('div', '', 'hm-message');
    message.setAttribute('role', 'status');
    const content = node('div', '', 'hm-content'), activity = node('button', '', 'hm-activity');
    activity.type = 'button';
    activity.hidden = true;
    main.append(tabs, message, content, activity);
    workspace.append(sidebar, main);
    element.append(title, workspace);
    const search = input('', 'search');
    search.placeholder = labels['Search hosts'];
    search.setAttribute('aria-label', labels['Search hosts']);
    const hostList = node('div', '', 'hm-host-list'), selection = node('span', '', 'hm-selection-count'), total = badge('0');
    const sidebarHeading = node('div', '', 'hm-sidebar-heading');
    sidebarHeading.append(node('h2', labels['Hosts']), total);
    const selectionTools = node('div', '', 'hm-selection-tools');
    sidebar.append(sidebarHeading, search, selectionTools, hostList, selection);
    sidebar.setAttribute('aria-label', labels['Target hosts']);

    /** 未適用の編集内容が残っているか確認します。
     * @returns 呼出し元が使用する判定結果です。
     */
    const hasDrafts = () => [...screen.views.values()].some(item => item.view.hasChanges?.());

    const ctx = {
        signal: screen.abort.signal,

        t: key => labels[key] ?? key,

        dialogs: screen.dialogs,

        hosts: () => screen.hosts.filter(host => screen.selected.has(host.target.hostId) && supportedHost(host)),

        /** 操作の結果または問題を画面に表示します。 */
        say(text)
        {
            message.textContent = text;
            const feedback = [...screen.dialogs].at(-1)?.querySelector('[data-hm-feedback]');
            if (feedback)
            {
                feedback.textContent = text;
                if (text)
                {
                    feedback.scrollIntoView?.({ block: 'nearest' });
                }
            }
        },

        /** 取消しと応答の確認を伴う管理要求を送信します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
         */
        async api(path, body, signal = screen.abort.signal)
        {
            const response = await fetch('/client/host-manager/' + path, { method: body === undefined ? 'GET' : 'POST',
                headers: body === undefined ? {} : { 'Content-Type': 'application/json', 'X-Contents-Request': body.operationId ?? crypto.randomUUID() },
                body: body === undefined ? undefined : JSON.stringify(body), signal, cache: 'no-store' });
            if (response.status === 400)
            {
                throw new Error(ctx.t('Validation failed. Check the input values.'));
            }
            if (!response.ok)
            {
                const expectedRead = 'read';

                throw new Error(ctx.t(body === undefined || path === expectedRead ? 'Read failed. Check the selected host, its supported features and privileges.' : 'Request unconfirmed. Refresh the saved operation history; do not repeat the change.'));
            }
            return response.json();
        },

        read: (host, query, signal) => ctx.api('read', { target: host.target, query }, signal),

        /** 操作の失敗を受け止め、現在の画面へ結果を通知します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
         */
        async guard(run)
        {
            try
            {
                return await run();
            }
            catch (error)
            {
                const expectedAborterror = 'AbortError';

                if (screen.abort.signal.aborted || error.name === expectedAborterror)
                {
                    return;
                }
                const reasons = { 'Device.GatewayManagedByDhcp': 'Gateway changes require DHCP (and IPv6 router discovery) to be disabled explicitly or already disabled.',
                    'Device.GatewayManagedByRouterDiscovery': 'Gateway changes require DHCP (and IPv6 router discovery) to be disabled explicitly or already disabled.',
                    'Device.AddressManagedByDhcp': 'Static addresses require DHCP to be disabled explicitly or already disabled.',
                    'Device.GatewayOutsideSubnet': 'The gateway must belong to the interface subnet.', 'Device.NoChanges': 'No changes',
                    'IPv4': 'Validation failed. Check the input values.', 'IPv6': 'Validation failed. Check the input values.' };
                ctx.say(ctx.t(reasons[error.message] ?? error.message));
            }
        },

        /** 操作の実行と処理中表示を持つボタンを構成します。
         * @returns 呼出し元が使用する値です。
         */
        button(label, run, className = '')
        {
            const button = node('button', ctx.t(label), className);
            button.type = 'button';
            button.onclick = () =>
            {
                const expectedTrue = 'true';

                if (button.disabled || button.getAttribute('aria-busy') === expectedTrue)
                {
                    return;
                }
                return ctx.guard(async () =>
                {
                    button.setAttribute('aria-busy', 'true');
                    try
                    {
                        await run();
                    }
                    finally
                    {
                        button.removeAttribute('aria-busy');
                    }
                });
            };
            return button;
        },

        /** 確認用のダイアログを構成します。
         * @returns root・footer・element・closeを含む結果です。
         */
        dialog(label)
        {
            const dialog = node('dialog', '', 'hm-dialog'), header = node('div', '', 'hm-dialog-title'), body = node('div', '', 'hm-dialog-body'), footer = node('div', '', 'hm-dialog-actions');
            const previous = document.activeElement;

            /** 対象を閉じ、関連する状態を整理します。 */
            const close = () =>
            {
                dialog.close();
                dialog.remove();
                screen.dialogs.delete(dialog);
                if (previous?.isConnected)
                {
                    previous.focus({ preventScroll: true });
                }
            };

            const dismiss = action(ctx, 'Close', 'close', close);
            dismiss.classList.add('hm-icon-button');
            dismiss.setAttribute('aria-label', ctx.t('Close'));
            dismiss.title = ctx.t('Close');
            const heading = node('h2', ctx.t(label));
            heading.id = 'hm-dialog-' + crypto.randomUUID();
            dialog.setAttribute('aria-labelledby', heading.id);
            header.append(heading, dismiss);
            const feedback = node('p', '', 'hm-message');
            feedback.dataset.hmFeedback = '';
            feedback.setAttribute('role', 'status');
            body.append(feedback);
            dialog.append(header, body, footer);
            element.append(dialog);
            screen.dialogs.add(dialog);
            dialog.addEventListener('cancel', event =>
            {
                event.preventDefault();
                close();
            });
            dialog.showModal();
            queueMicrotask(() =>
            {
                if (dialog.open)
                {
                    body.querySelector('input:not(:disabled),textarea:not(:disabled),select:not(:disabled)')?.focus();
                }
            });
            return { root: body, footer, element: dialog, close };
        },

        /** 未確定の変更を破棄して表示を戻します。
         * @returns 呼出し元が使用する値です。
         */
        discard(run, dirty = hasDrafts())
        {
            if (!dirty)
            {
                return ctx.guard(run);
            }
            const dialog = ctx.dialog('Discard unsaved changes?');
            dialog.root.append(node('p', ctx.t('Your edited settings have not been applied.')));
            dialog.footer.append(ctx.button('Keep editing', dialog.close), ctx.button('Discard changes', async () =>
            {
                dialog.close();
                await run();
            }, 'danger'));
        },

        /** 適用前の変更内容を確認表示へ渡します。
         * @returns 呼出し元が使用する値です。
         */
        preview(changes, submitted)
        {
            const generation = screen.generation, editor = [...screen.dialogs].at(-1);

            /** 現在選択されている値を取得します。
             * @returns 呼出し元が使用する値です。
             */
            const current = () => generation === screen.generation && [...screen.dialogs].at(-1) === editor && (!editor || editor.isConnected && editor.open);

            return Promise.resolve(typeof changes === 'function' ? changes() : changes).then(prepared => current() ? showPreview(ctx, prepared, refreshHistoryAsync, current, submitted) : undefined)
                .finally(() =>
                {
                    if (editor && !editor.isConnected && generation === screen.generation)
                    {
                        ctx.say('');
                    }
                });
        },

        /** 送信済みの操作を表示状態へ反映します。 */
        submitted(id)
        {
            screen.pending.add(id);
            for (const dialog of [...screen.dialogs])
            {
                dialog.close();
                dialog.remove();
            }
            screen.dialogs.clear();
            openHistory();
        },
    };

    /** 接続先の変更に対応して各表示を初期化します。 */
    function resetViews()
    {
        screen.generation++;
        screen.views.forEach(item => item.view.dispose?.());
        screen.views.clear();
        content.replaceChildren();
    }

    /** 選択された機能の内容を表示します。 */
    function renderContent()
    {
        screen.generation++;
        ctx.say('');
        for (const [area, item] of screen.views)
        {
            item.root.hidden = area !== screen.area;
        }
        if (!screen.views.has(screen.area))
        {
            const root = node('section', '', 'hm-area');
            root.id = 'hm-panel-' + screen.area;
            root.setAttribute('role', 'tabpanel');
            root.setAttribute('aria-labelledby', 'hm-tab-' + screen.area);
            content.append(root);
            const build = areas.find(item => item[0] === screen.area)[3];
            screen.views.set(screen.area, { root, view: build(root, ctx) });
        }
        const active = screen.views.get(screen.area);
        if (active.stale && !active.view.hasChanges?.())
        {
            active.stale = false;
            ctx.guard(() => active.view.reload?.()).catch(error => ctx.say(error.message));
        }
        renderTargets();
    }

    /** 未保存変更を確認してホスト選択を切り替えます。
     * @returns 呼出し元が使用する値です。
     */
    function changeSelection(next)
    {
        if (!screen.refreshing)
        {
            return ctx.discard(() =>
            {
                screen.selected = next;
                resetViews();
                renderContent();
            });
        }
    }

    /** 現在の絞り込みで表示するホストを取得します。
     * @returns 呼出し元が使用する値です。
     */
    function visibleHosts()
    {
        const query = search.value.trim().toLocaleLowerCase();
        return screen.hosts.filter(host => `${host.target.hostId} ${host.capabilities?.machineName ?? ''}`.toLocaleLowerCase().includes(query));
    }

    /** 電源予定だけは、停止前に確認したHostも操作対象にできます。
     * @returns 現在のタブで選択可能かです。
     */
    function supportedHost(host)
    {
        const schedules = 'powerSchedules';
        return screen.area === schedules || host.capabilities?.areas.includes(screen.area);
    }

    /** 操作対象のホストを一覧へ反映します。 */
    function renderTargets()
    {
        const focused = hostList.contains(document.activeElement) ? document.activeElement.getAttribute('aria-label') : null;
        const left = hostList.scrollLeft, top = hostList.scrollTop;
        let focusedPick;
        hostList.replaceChildren();
        total.textContent = String(screen.hosts.length);
        const visible = visibleHosts();
        for (const host of visible)
        {
            const supported = supportedHost(host), selected = screen.selected.has(host.target.hostId);
            const pick = checkbox('', selected && supported);
            pick.root.classList.add('hm-host');
            pick.root.dataset.selected = String(selected && supported);
            pick.control.disabled = !supported || screen.refreshing;
            pick.control.setAttribute('aria-label', host.target.hostId);
            const copy = node('span', '', 'hm-host-copy');
            const description = host.capabilities ? `${host.capabilities.operatingSystem} · ${ctx.t(host.capabilities.elevated ? 'Administrator' : 'Limited')}` : ctx.t('Offline');
            copy.append(node('strong', host.target.hostId), node('small', supported ? description : ctx.t('Unavailable in this tab')));
            pick.root.append(icon('monitor'), copy);
            pick.root.title = `${host.target.clusterId} / ${host.target.peerId}`;
            pick.control.onchange = () =>
            {
                const next = new Set(screen.selected);
                if (pick.control.checked)
                {
                    next.add(host.target.hostId);
                }
                else
                {
                    next.delete(host.target.hostId);
                }
                pick.control.checked = selected;
                ctx.guard(() => changeSelection(next)).catch(error => ctx.say(error.message));
            };
            hostList.append(pick.root);
            if (focused === host.target.hostId)
            {
                focusedPick = pick;
            }
        }
        if (!visible.length)
        {
            hostList.append(empty(ctx.t(screen.hosts.length ? 'No matching hosts' : 'No supported hosts discovered.'), '', 'search'));
        }
        hostList.scrollLeft = left;
        hostList.scrollTop = top;
        if (focusedPick)
        {
            focusedPick.control.focus({ preventScroll: true });
            if ((hostList.clientWidth) < (hostList.scrollWidth))
            {
                const container = hostList.getBoundingClientRect(), item = focusedPick.root.getBoundingClientRect();
                if (item.left < container.left)
                {
                    hostList.scrollLeft += item.left - container.left;
                }
                else if ((container.right) < (item.right))
                {
                    hostList.scrollLeft += item.right - container.right;
                }
            }
        }
        selection.textContent = `${ctx.t('Selected hosts')}: ${ctx.hosts().length}`;
    }

    for (const [area, label, glyph] of areas)
    {
        const button = action(ctx, label, glyph, () =>
        {
            screen.area = area;
            for (const item of tabs.children)
            {
                const selected = item === button;
                item.setAttribute('aria-selected', String(selected));
                item.tabIndex = selected ? 0 : -1;
            }
            renderContent();
        });
        button.id = 'hm-tab-' + area;
        button.setAttribute('role', 'tab');
        button.setAttribute('aria-selected', String(area === screen.area));
        button.setAttribute('aria-controls', 'hm-panel-' + area);
        button.tabIndex = area === screen.area ? 0 : -1;
        tabs.append(button);
    }
    tabs.addEventListener('keydown', event =>
    {
        const expectedHome = 'Home';
        const expectedEnd = 'End';
        const expectedArrowright = 'ArrowRight';

        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key))
        {
            return;
        }
        event.preventDefault();
        const buttons = [...tabs.children], index = buttons.indexOf(document.activeElement);
        const next = event.key === expectedHome ? 0 : event.key === expectedEnd ? buttons.length - 1 : (index + (event.key === expectedArrowright ? 1 : -1) + buttons.length) % buttons.length;
        buttons[next].click();
        buttons[next].focus();
    });
    search.oninput = renderTargets;
    selectionTools.append(ctx.button('Select visible', () => changeSelection(new Set([...screen.selected, ...visibleHosts().filter(supportedHost).map(host => host.target.hostId)]))), ctx.button('Clear selection', () => changeSelection(new Set())));
    const historyButton = action(ctx, 'Operation history', 'history', openHistory), refreshButton = action(ctx, 'Refresh hosts', 'refresh', () => ctx.discard(refreshHostsAsync));
    titleTools.append(refreshButton, historyButton);
    activity.onclick = openHistory;

    /** 到達可能なホストと機能を取得します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function refreshHostsAsync()
    {
        screen.refreshing = true;
        content.inert = true;
        hostList.setAttribute('aria-busy', 'true');
        renderTargets();
        for (const button of [...tabs.children, ...selectionTools.children])
        {
            button.disabled = true;
        }
        try
        {
            const hosts = await ctx.api('hosts');
            if (ctx.signal.aborted)
            {
                return;
            }
            screen.hosts = hosts;
            screen.selected = new Set([...screen.selected].filter(id => hosts.some(host => host.target.hostId === id)));
            resetViews();
            renderContent();
        }
        finally
        {
            screen.refreshing = false;
            content.inert = false;
            hostList.removeAttribute('aria-busy');
            for (const button of [...tabs.children, ...selectionTools.children])
            {
                button.disabled = false;
            }
            renderTargets();
        }
    }

    /** 保存されている操作履歴の表示を開きます。 */
    function openHistory()
    {
        if (screen.historyDialog?.element.isConnected)
        {
            return;
        }
        screen.historyDialog = ctx.dialog('Operation history');
        screen.historyDialog.element.classList.add('hm-history-dialog');
        showHistory(screen.historyDialog.root, ctx, screen.history, refreshHistoryAsync);
    }

    /** 操作履歴を再取得して結果を更新します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function refreshHistoryAsync()
    {
        const states = await ctx.api('batches');
        if (ctx.signal.aborted)
        {
            return;
        }
        screen.history = states;
        if (screen.historyDialog?.element.isConnected)
        {
            showHistory(screen.historyDialog.root, ctx, states, refreshHistoryAsync);
        }
        const running = states.filter(state => state.running).length, uncertain = states.filter(state => state.results.some(result => result?.outcome === 'unknown')).length;
        const completed = states.filter(state => !state.running && screen.pending.has(state.request.operationId));
        if (completed.length)
        {
            completed.forEach(state => screen.pending.delete(state.request.operationId));
            for (const item of screen.views.values())
            {
                item.stale = true;
            }
            const active = screen.views.get(screen.area);
            if (active && !active.view.hasChanges?.())
            {
                active.stale = false;
                ctx.guard(() => active.view.reload?.()).catch(error => ctx.say(error.message));
            }
        }
        activity.hidden = !running && !uncertain;
        activity.textContent = `${ctx.t(running ? 'Running' : 'Unknown')}: ${running || uncertain} · ${ctx.t('View results')}`;
        clearTimeout(screen.timer);
        if (running)
        {
            screen.timer = setTimeout(() => ctx.guard(refreshHistoryAsync), 2500);
        }
    }

    renderContent();
    await ctx.guard(async () =>
    {
        await refreshHostsAsync();
        await refreshHistoryAsync();
    });
}
