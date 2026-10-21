import { createTerminalStream } from './terminal-stream.js';
import { createTerminalLayout } from './terminal-layout.js';
import { createTerminalKeys } from './terminal-keys.js';
import { createTerminalFullscreen } from './terminal-fullscreen.js';

/** ターミナルの接続・入出力・セッション表示を構成します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function terminalView(root, ctx)
{
    const experimental = new URLSearchParams(location.search).get('experimental') === 'true';
    root.className = 'terminal-workspace';
    const sidebar = document.createElement('aside');
    sidebar.className = 'remote-sidebar';
    const peersTree = document.createElement('div');
    const sessions = document.createElement('div');
    const panel = document.createElement('section');
    panel.className = 'terminal-panel';
    const toolbar = document.createElement('div');
    toolbar.className = 'toolbar tool-toolbar terminal-toolbar';
    const host = document.createElement('div');
    host.className = 'terminal-screen';
    const note = document.createElement('p');
    note.className = 'tool-status terminal-target';
    note.textContent = ctx.t('Select a peer to start a shell, or take over a shared session.', 'ピアを選んでシェルを開始、または共有セッションへ接続します。');
    sidebar.append(sessions, peersTree);
    panel.append(note, host);
    root.append(toolbar, sidebar, panel);
    let peer, session, term, fit, stream, generation = 0, owner;
    let disposed = false;
    let peers = [], listTimer, listing, sessionSignature = '', peerSignature = '';
    const shellSelect = document.createElement('select');
    shellSelect.className = 'terminal-shell';
    shellSelect.setAttribute('aria-label', ctx.t('New session shell', '新規セッションのShell'));
    const shellLabel = document.createElement('label');
    shellLabel.textContent = 'Shell';
    shellLabel.append(shellSelect);
    const shellCatalogs = new Map();
    let shellTarget = '', shells = [], shellLoading = false, shellGeneration = 0;
    let capabilities = null, capabilityTarget = '', capabilityRequest = null, savingTokenPolicy = false;
    const keys = createTerminalKeys(ctx, { ready: () => !!stream?.ready && !!session && !session.exited,

        send: data => stream?.input(data),

        focus: () => term?.focus(),

        applicationCursor: () => !!term?.modes?.applicationCursorKeysMode });
    panel.append(keys.element);
    host.addEventListener('paste', () => keys.reset(), { capture: true, signal: ctx.signal });
    const sessionLists = new Map();
    const layout = createTerminalLayout(root, host, () => fit?.fit(), ctx.signal);

    /** ピアの所属と実体を区別するキーを作成します。
     * @returns 呼出し元が使用する値です。
     */
    const peerKey = value => value ? JSON.stringify([value.clusterId, value.peerId, value.instanceId]) : '';

    /** 接続先が同じピアの同じ実体かを照合します。
     * @returns 呼出し元が使用する判定結果です。
     */
    const samePeer = (a, b) => peerKey(a) === peerKey(b);

    /** 接続と描画を終了し、保留中の解放処理を保持します。 */
    const disconnect = () =>
    {
        void fullscreen.exit().catch(() =>
        {});
        generation++;
        stream?.close();
        stream = null;
        term?.dispose();
        term = null;
        fit = null;
        session = null;
        host.replaceChildren();
        updateControls();
        renderSessions();
    };

    /** 現在の状態を一覧用の短い説明へまとめます。
     * @returns 呼出し元が使用する値です。
     */
    function summary(view)
    {
        return `${view.system ? 'SYSTEM' : view.elevated ? ctx.t('Administrator', '管理者') : ctx.t('User', 'ユーザー')} · ${view.sessionId.slice(0, 8)} · ${view.shell.split(/[\\/]/).pop()} · ${view.exited ? ctx.t('Exited', '終了済み') : ctx.t('Running', '実行中')}`;
    }

    /** 取得したセッションと接続状態を表示します。 */
    function renderSessions()
    {
        if (disposed || ctx.signal.aborted)
        {
            return;
        }
        const signature = JSON.stringify([peerKey(peer), session?.sessionId, [...sessionLists].map(([key, item]) => [key, item.failed, item.target.hostId, item.values.map(value => [value.sessionId, value.shell, value.elevated, value.system, value.exited])])]);
        if (signature === sessionSignature)
        {
            return;
        }
        sessionSignature = signature;
        sessions.replaceChildren();
        const title = document.createElement('h2');
        title.textContent = ctx.t('Shared sessions', '共有セッション');
        sessions.append(title);
        const hint = document.createElement('p');
        hint.className = 'muted';
        hint.textContent = ctx.t('All discovered peers · automatic refresh', '全検出ピア · 自動更新');
        sessions.append(hint);
        let total = 0, failed = 0;
        for (const item of sessionLists.values())
        {
            const selected = item.target;
            failed += item.failed ? 1 : 0;
            if (!item.values.length)
            {
                continue;
            }
            const group = document.createElement('h3');
            group.textContent = `${selected.clusterId} / ${selected.hostId} / ${selected.peerId}`;
            sessions.append(group);
            if (item.failed)
            {
                const stale = document.createElement('p');
                stale.className = 'muted';
                stale.textContent = ctx.t('Read unconfirmed · last known sessions', '取得未確認 · 最終確認済みのセッション');
                sessions.append(stale);
            }
            for (const value of item.values)
            {
                total++;
                const row = document.createElement('div');
                row.className = 'terminal-session' + (samePeer(peer, selected) && session?.sessionId === value.sessionId ? ' selected' : '');
                row.dataset.peer = peerKey(selected);
                const name = document.createElement('span');
                name.textContent = summary(value);
                row.append(name);
                const claim = ctx.button(ctx.t('Take over', '接続を引き継ぐ'), () => connectAsync(value, false, selected));
                const terminate = ctx.button(ctx.t('Terminate', '明示終了'), async () =>
                {
                    if (samePeer(peer, selected) && session?.sessionId === value.sessionId)
                    {
                        disconnect();
                    }
                    await ctx.api('/api/terminal/close', selected, { sessionId: value.sessionId });
                    await list();
                });
                claim.disabled = terminate.disabled = item.failed;
                row.append(claim, terminate);
                sessions.append(row);
            }
        }
        if (!total)
        {
            const empty = document.createElement('p');
            empty.className = 'remote-empty';
            empty.textContent = ctx.t('No sessions confirmed. Select a peer to start a shell.', '確認できたセッションはありません。ピアを選択してシェルを開始できます。');
            sessions.append(empty);
        }
        if (failed)
        {
            const warning = document.createElement('p');
            warning.className = 'muted';
            warning.textContent = ctx.t(`Reads unconfirmed: ${failed} peers`, `取得未確認: ${failed}ピア`);
            sessions.append(warning);
        }
    }

    /** 対象の一覧を再取得して現在の表示を更新します。
     * @returns 呼出し元が使用する値です。
     */
    function list()
    {
        if (disposed)
        {
            return Promise.resolve();
        }
        if (listing)
        {
            return listing;
        }
        clearTimeout(listTimer);
        listing = (async () =>
        {
            try
            {
                const entries = await ctx.api('/client/terminal/sessions');
                if (disposed)
                {
                    return;
                }
                for (const entry of entries)
                {
                    const target = { ...entry.peer, clusterId: entry.clusterId };
                    sessionLists.set(peerKey(target), { target, values: entry.sessions, failed: !entry.confirmed });
                }
                await loadPeersAsync(entries.map(entry => ({ ...entry.peer, clusterId: entry.clusterId })));
                await loadCapabilities(true);
            }
            catch (error)
            {
                if (!disposed)
                {
                    ctx.say(error.message);
                }
            }
            finally
            {
                listing = null;
                if (!disposed && !ctx.signal.aborted)
                {
                    listTimer = setTimeout(() =>
                    {
                        void list();
                    }, 3000);
                }
            }
        })();
        return listing;
    }

    /** 接続対象と応答を確認して通信を開始します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function connectAsync(value, created = false, target = peer)
    {
        disconnect();
        ctx.say('');
        peer = target;
        await selectPeer();
        const run = generation;
        owner = created ? value.owner : crypto.randomUUID();
        const claimed = created ? value : await ctx.api('/api/terminal/claim', peer, { sessionId: value.sessionId, owner });
        if (run !== generation || disposed)
        {
            return;
        }
        session = claimed;
        term = new window.Terminal({ cursorBlink: true, scrollback: 3000, fontSize: 14, theme: { background: '#101114', foreground: '#e5e7eb', cursor: '#91b8ee', selectionBackground: '#354a66' } });
        const surface = document.createElement('div');
        surface.className = 'terminal-surface';
        host.append(surface);
        fit = new window.FitAddon.FitAddon();
        term.loadAddon(fit);
        term.open(surface);
        fit.fit();
        term.focus();
        term.attachCustomKeyEventHandler(event =>
        {
            const expectedKeydown = 'keydown';

            if (event.type !== expectedKeydown)
            {
                return true;
            }
            if ((event.ctrlKey || event.metaKey) && event.shiftKey && ['c', 'v'].includes(event.key.toLowerCase()))
            {
                const expectedC = 'c';

                event.preventDefault();
                void ctx.guard(event.key.toLowerCase() === expectedC ? copySelectionAsync : pasteClipboardAsync);
                return false;
            }
            return true;
        });
        stream = createTerminalStream(term, peer, claimed, {
            created,

            active: () => run === generation && !disposed,

            ready: () =>
            {
                updateControls();
                layout.update();
            },

            transformInput: data => keys.transform(data),

            problem: () => ctx.say(ctx.t('Input queue is full. Paste in smaller parts.', '入力待ちが上限です。小さく分けて貼り付けてください。')),

            /** 接続が終了した理由と残った状態を反映します。 */
            closed(reason, view)
            {
                const expectedExited = 'exited';
                const expectedOwnership = 'ownership';

                if (run !== generation || disposed)
                {
                    return;
                }
                session = reason === expectedExited ? view : null;
                updateControls();
                renderSessions();
                ctx.say(reason === expectedExited ? ctx.t('Shell exited.', 'シェルが終了しました。') : reason === expectedOwnership ? ctx.t('Another client took over this terminal.', '別の画面がこのターミナルの接続を引き継ぎました。') : ctx.t('Terminal connection ended. Take over the session to reconnect. Unconfirmed input was not resent.', '端末の接続が終了しました。共有セッションから再接続できます。未確認の入力は再送していません。'));
            }
        });
        term.onResize(() =>
        {
            if (run === generation)
            {
                stream?.resize();
            }
        });
        updateControls();
        layout.update();
        await list();
    }

    /** 到達可能なピアを再取得して選択欄を更新します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function loadPeersAsync(discovered)
    {
        if (disposed)
        {
            return;
        }
        peers = [...new Map(discovered.map(target => [peerKey(target), target])).values()];
        const keys = new Set(peers.map(peerKey));
        for (const key of sessionLists.keys())
        {
            if (!keys.has(key))
            {
                sessionLists.delete(key);
            }
        }
        for (const key of shellCatalogs.keys())
        {
            if (!keys.has(key))
            {
                shellCatalogs.delete(key);
            }
        }
        renderSessions();
        const signature = JSON.stringify(peers.map(target => [peerKey(target), target.hostId]));
        if (signature === peerSignature)
        {
            await selectPeer();
            return;
        }
        peerSignature = signature;
        peersTree.replaceChildren();
        const title = document.createElement('h2');
        title.textContent = ctx.t('Clusters & peers', 'クラスタ・ピア');
        peersTree.append(title);
        if (!peers.length)
        {
            const empty = document.createElement('p');
            empty.textContent = ctx.t('No peers discovered. Configure a network using Select connection.', 'ピアが見つかりません。Select connectionから接続ネットワークを設定してください。');
            peersTree.append(empty);
        }
        for (const cluster of [...new Set(peers.map(item => item.clusterId))])
        {
            const branch = document.createElement('details');
            branch.open = true;
            const label = document.createElement('summary');
            label.textContent = cluster;
            branch.append(label);
            for (const target of peers.filter(item => item.clusterId === cluster))
            {
                const button = ctx.button(`${target.hostId} / ${target.peerId}`, async () =>
                {
                    disconnect();
                    peer = target;
                    ctx.say('');
                    shellTarget = '';
                    await selectPeer();
                    updateControls();
                    renderSessions();
                });
                button.dataset.peer = peerKey(target);
                button.prepend(ctx.icon('monitor'));
                branch.append(button);
            }
            peersTree.append(branch);
        }
        await selectPeer();
    }

    /** 現在選択したピアの表示と必要な照会を更新します。
     * @returns 呼出し元が使用する値です。
     */
    function selectPeer()
    {
        if (peer)
        {
            note.textContent = `${peer.clusterId} / ${peer.hostId} / ${peer.peerId}`;
        }
        peersTree.querySelectorAll('button').forEach(button => button.classList.toggle('selected', button.dataset.peer === peerKey(peer)));
        return Promise.all([loadShellsAsync(), loadCapabilities()]);
    }

    /** 接続先が対応している操作機能を取得します。
     * @returns 呼出し元が使用する値です。
     */
    function loadCapabilities(force = false)
    {
        const selected = peer, key = peerKey(selected);
        if (key === capabilityTarget && (capabilityRequest || !force))
        {
            return capabilityRequest ?? Promise.resolve();
        }
        if (key !== capabilityTarget)
        {
            capabilities = null;
            capabilityTarget = key;
            updateControls();
        }
        if (!selected)
        {
            return Promise.resolve();
        }
        const request = (async() =>
        {
            try
            {
                const value = await ctx.api('/api/terminal/capabilities', selected);
                if (typeof value?.userAvailable !== 'boolean' || typeof value?.administratorAvailable !== 'boolean')
                {
                    throw new Error('Invalid terminal capabilities.');
                }
                if (!disposed && peerKey(peer) === key)
                {
                    capabilities = value;
                }
            }
            catch
            {
                // An older Core can still provide ordinary sessions; never guess administrator availability.
                if (!disposed && peerKey(peer) === key)
                {
                    capabilities = { userAvailable: true, administratorAvailable: false, administratorReason: 'CapabilitiesUnavailable' };
                }
            }
            finally
            {
                if (!disposed && peerKey(peer) === key)
                {
                    updateControls();
                }
            }
        })();
        capabilityRequest = request;
        return request.finally(() =>
        {
            if (capabilityRequest === request)
            {
                capabilityRequest = null;
            }
        });
    }

    /** 利用可能なシェルを選択欄へ反映します。 */
    function showShells(message, selected = '')
    {
        shellSelect.replaceChildren();
        if (!shells.length)
        {
            const option = document.createElement('option');
            option.value = '';
            option.textContent = message;
            shellSelect.append(option);
        }
        for (const [index, shell] of shells.entries())
        {
            const option = document.createElement('option');
            option.value = shell.path;
            option.textContent = `${shell.name}${index === 0 ? ctx.t(' (default)', '（既定）') : ''} · ${shell.path}`;
            shellSelect.append(option);
        }
        shellSelect.value = selected;
        shellSelect.title = selected || message;
    }

    /** シェル一覧の件数と必須項目を検証します。 */
    function validateShellCatalog(items)
    {
        if (!Array.isArray(items) || (64) < (items.length) || items.some(item => typeof item.path !== 'string' || !item.path || typeof item.name !== 'string'))
        {
            throw new Error(ctx.t('The shell catalog could not be verified.', 'Shell一覧を確認できません。'));
        }
    }

    /** 接続先のシェル候補を取得します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function loadShellsAsync()
    {
        const selected = peer, key = peerKey(selected);
        if (key === shellTarget)
        {
            return;
        }
        const run = ++shellGeneration;
        shellTarget = key;
        shells = [];
        shellLoading = !!selected;
        showShells(selected ? ctx.t('Loading shells…', 'Shellを取得中…') : ctx.t('Select a peer', 'ピアを選択'));
        updateControls();
        if (!selected)
        {
            return;
        }
        try
        {
            let catalog = shellCatalogs.get(key);
            if (!catalog)
            {
                const items = await ctx.api('/api/terminal/shells', selected);
                validateShellCatalog(items);
                catalog = { items, selected: items[0]?.path || '' };
                if (!disposed && run === shellGeneration && peers.some(item => peerKey(item) === key))
                {
                    shellCatalogs.set(key, catalog);
                }
            }
            if (disposed || run !== shellGeneration || peerKey(peer) !== key)
            {
                return;
            }
            shells = catalog.items;
            showShells(ctx.t('No shell found at Core startup', 'Core起動時にShellが見つかりませんでした'), catalog.selected);
        }
        catch(error)
        {
            if (!disposed && run === shellGeneration && peerKey(peer) === key)
            {
                showShells(ctx.t('Shell list unavailable · select peer again', 'Shell取得不可 · ピアを再選択'));
                ctx.say(error.message);
            }
        }
        finally
        {
            if (!disposed && run === shellGeneration && peerKey(peer) === key)
            {
                shellLoading = false;
                updateControls();
            }
        }
    }

    shellSelect.onchange = () =>
    {
        const catalog = shellCatalogs.get(peerKey(peer));
        if (catalog)
        {
            catalog.selected = shellSelect.value;
        }
        shellSelect.title = shellSelect.value;
    };

    /** 選択された内容をクリップボードへコピーします。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function copySelectionAsync()
    {
        const text = term?.getSelection();
        if (!text)
        {
            ctx.say(ctx.t('Select terminal text to copy.', 'コピーする端末テキストを選択してください。'));
            return;
        }
        try
        {
            await navigator.clipboard.writeText(text);
        }
        catch
        {
            ctx.say(ctx.t('Clipboard access is unavailable. Use the browser Copy command or Ctrl+C with selected text.', 'クリップボードにアクセスできません。選択したテキストをブラウザーのコピー操作またはCtrl+Cでコピーしてください。'));
        }
    }

    /** クリップボードの文字列を現在の入力へ渡します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function pasteClipboardAsync()
    {
        if (!session || session.exited)
        {
            return;
        }
        const active = session;
        try
        {
            const text = await navigator.clipboard.readText();
            if (session === active && !disposed)
            {
                keys.reset();
                term.paste(text);
                term.focus();
            }
        }
        catch
        {
            ctx.say(ctx.t('Clipboard access is unavailable. Focus the terminal and use Ctrl+V / Cmd+V.', 'クリップボードにアクセスできません。端末にフォーカスしてCtrl+V / Cmd+Vで貼り付けてください。'));
        }
    }

    /** 入力された条件から新しい対象を作成します。
     * @returns 呼出し元が使用する値です。
     */
    const create = elevated => ctx.button(elevated ? ctx.t('＋ Administrator', '＋ 管理者実行') : ctx.t('＋ User session', '＋ ユーザー実行'), async () =>
    {
        const selected = peer, run = generation, shell = shellSelect.value;
        if (disposed || ctx.signal.aborted || !selected || shellLoading || !shells.some(item => item.path === shell) || (elevated ? capabilities?.administratorAvailable !== true : capabilities?.userAvailable !== true))
        {
            return;
        }
        const value = await ctx.api('/api/terminal/create', selected, { sessionId: crypto.randomUUID(), owner: crypto.randomUUID(), elevated, shell });
        if (selected === peer && run === generation && !disposed)
        {
            await connectAsync(value, true);
        }
    });

    const user = create(false), admin = create(true);
    user.className = 'primary';
    const copy = ctx.button(ctx.t('Copy', 'コピー'), copySelectionAsync), paste = ctx.button(ctx.t('Paste', '貼り付け'), pasteClipboardAsync);
    copy.title = 'Ctrl+Shift+C';
    paste.title = 'Ctrl+Shift+V';
    const leave = ctx.button(ctx.t('Disconnect', '接続解除'), async () => disconnect());
    const fullscreen = createTerminalFullscreen(root, host, ctx, { active: () => !!term,

        focus: () => term?.focus(),

        fit: () => layout.update() });
    for (const [button, icon] of [[user, 'play'], [admin, 'settings'], [copy, 'copy'], [paste, 'clipboard'], [leave, 'stop']])
    {
        button.prepend(ctx.icon(icon));
    }
    const refreshSessions = ctx.button(ctx.t('Refresh sessions', 'セッション更新'), list);
    const tokenPolicy = experimental ? ctx.button('', async() =>
    {
        const selected = peer, current = capabilities?.settings;
        if (!selected || savingTokenPolicy || typeof current?.strictUserToken !== 'boolean')
        {
            return;
        }
        savingTokenPolicy = true;
        updateControls();
        try
        {
            await ctx.api('/api/terminal/settings/save', selected, { strictUserToken: !current.strictUserToken, version: current.version });
            if (samePeer(selected, peer))
            {
                ctx.say(ctx.t('Saved on this Core. Applies to new user sessions from every client.', 'このCoreに保存しました。全クライアントからの新規ユーザーセッションに適用します。'));
            }
        }
        finally
        {
            savingTokenPolicy = false;
            await capabilityRequest;
            await loadCapabilities(true);
            updateControls();
        }
    }) : null;

    /** 接続先の権限とシェル一覧を起動操作へ反映します。 */
    function updateLaunchControls()
    {
        const expectedCoreadministratorrequired = 'CoreAdministratorRequired';

        const unavailable = !peer || shellLoading || !shells.length || !peers.some(item => samePeer(item, peer));
        user.disabled = unavailable || capabilities?.userAvailable !== true;
        admin.disabled = unavailable || capabilities?.administratorAvailable !== true;
        admin.textContent = capabilities?.administratorUsesSystem ? ctx.t('＋ SYSTEM session', '＋ SYSTEM実行') : ctx.t('＋ Administrator', '＋ 管理者実行');
        admin.prepend(ctx.icon('settings'));
        user.title = capabilities?.userAvailable ? '' : ctx.t('No non-elevated user token is available.', '通常ユーザーのトークンを取得できません。');
        admin.title = capabilities?.administratorUsesSystem ? ctx.t('No user is logged on. Start as SYSTEM.', '未ログオンのためSYSTEMとして起動します。') :
            capabilities?.administratorAvailable ? ctx.t('Start as this user with administrator privileges', 'ユーザー本人の管理者権限で起動') :
                capabilities?.administratorReason === expectedCoreadministratorrequired ? ctx.t('Core must run as administrator or SYSTEM. Terminal does not request UAC elevation.', 'Coreを管理者またはSYSTEMで起動してください。TerminalからUAC昇格は行いません。') :
                    ctx.t('An elevated token could not be confirmed on this Core.', 'このCoreで昇格トークンを確認できません。');
        shellSelect.disabled = unavailable;
    }

    /** 現在の接続・選択・処理中の状態を操作欄へ反映します。 */
    function updateControls()
    {
        updateLaunchControls();
        refreshSessions.disabled = disposed;
        copy.disabled = leave.disabled = !term;
        paste.disabled = !stream?.ready || !session || session.exited;
        keys.refresh();
        fullscreen.update();
        if (tokenPolicy)
        {
            const settings = capabilities?.settings, strict = settings?.strictUserToken === true;
            tokenPolicy.disabled = !peer || savingTokenPolicy || typeof settings?.strictUserToken !== 'boolean';
            tokenPolicy.textContent = ctx.t('Strict unelevated: ', '厳密な非昇格: ') + (strict ? 'ON' : 'OFF');
            tokenPolicy.setAttribute('aria-pressed', String(strict));
            tokenPolicy.title = ctx.t('Experimental · saved on the selected Core. OFF uses the existing user-token method; ON enforces a restricted token and Medium integrity. Existing sessions keep their original mode.', 'Experimental · 選択中Coreに保存。OFFは従来のユーザートークン方式、ONは制限トークンとMedium整合性を使用。起動済みセッションは変更しません。');
        }
    }

    showShells(ctx.t('Select a peer', 'ピアを選択'));
    toolbar.append(shellLabel, user, admin, leave, copy, paste, ...fullscreen.buttons);
    if (tokenPolicy)
    {
        toolbar.append(tokenPolicy);
    }
    const sessionHeading = document.createElement('h2');
    sessionHeading.textContent = ctx.t('Shared sessions', '共有セッション');
    const empty = document.createElement('p');
    empty.className = 'remote-empty';
    empty.textContent = ctx.t('Loading shared sessions…', '共有セッションを取得中…');
    sessions.append(sessionHeading, empty);
    const sidebarActions = document.createElement('div');
    sidebarActions.className = 'toolbar';
    sidebarActions.append(refreshSessions, ctx.button(ctx.t('Refresh peers', 'ピア更新'), list));
    sidebar.append(sidebarActions);
    const menu = document.createElement('div');
    menu.className = 'terminal-context-menu';
    menu.hidden = true;
    menu.setAttribute('role', 'menu');
    const menuCopy = ctx.button(ctx.t('Copy selection', '選択範囲をコピー'), async () =>
    {
        menu.hidden = true;
        await copySelectionAsync();
    });
    const menuPaste = ctx.button(ctx.t('Paste', '貼り付け'), async () =>
    {
        menu.hidden = true;
        await pasteClipboardAsync();
    });
    const selectAll = ctx.button(ctx.t('Select all', 'すべて選択'), async () =>
    {
        term?.selectAll();
        menu.hidden = true;
    });
    for (const button of [menuCopy, menuPaste, selectAll])
    {
        button.setAttribute('role', 'menuitem');
    }
    menu.append(menuCopy, menuPaste, selectAll);
    panel.append(menu);
    document.addEventListener('fullscreenchange', () =>
    {
        menu.hidden = true;
        (document.fullscreenElement === host ? host : panel).append(menu);
    }, { signal: ctx.signal });

    /** 開いている操作メニューを閉じます。 */
    const hideMenu = event =>
    {
        if (!menu.contains(event.target))
        {
            menu.hidden = true;
        }
    };

    document.addEventListener('pointerdown', hideMenu, { signal: ctx.signal });
    document.addEventListener('scroll', () =>
    {
        const assignedValue = menu.hidden = true;
        return assignedValue;
    }, { signal: ctx.signal, capture: true });
    host.addEventListener('contextmenu', event =>
    {
        if (!term)
        {
            return;
        }
        event.preventDefault();
        menuCopy.disabled = !term.hasSelection();
        menuPaste.disabled = !session || session.exited;
        menu.hidden = false;
        menu.style.left = Math.max(8, Math.min(event.clientX, innerWidth - menu.offsetWidth - 8)) + 'px';
        menu.style.top = Math.max(8, Math.min(event.clientY, innerHeight - menu.offsetHeight - 8)) + 'px';
        menu.querySelector('button:not(:disabled)')?.focus();
    });
    menu.onkeydown = event =>
    {
        const expectedEscape = 'Escape';
        const expectedTab = 'Tab';

        const buttons = [...menu.querySelectorAll('button:not(:disabled)')];
        if (event.key === expectedEscape || event.key === expectedTab)
        {
            menu.hidden = true;
            term?.focus();
        }
        if (['ArrowDown', 'ArrowUp'].includes(event.key))
        {
            const expectedArrowdown = 'ArrowDown';

            event.preventDefault();
            const index = buttons.indexOf(document.activeElement);
            buttons[(index + (event.key === expectedArrowdown ? 1 : buttons.length - 1)) % buttons.length]?.focus();
        }
    };
    updateControls();
    await list();
    return { updateControls,

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            disposed = true;
            clearTimeout(listTimer);
            shellCatalogs.clear();
            fullscreen.dispose();
            layout.close();
            disconnect();
        } };
}
