import { appendFileRows } from './file-pane-rows.js';
import { hostIds, resolveHosts, samePeer, currentPeer } from './file-transfer-selection.js';
import { fileActionControls } from './file-action-controls.js';
import { mutateHostFileAsync } from './file-mutations.js';

/** ホスト間のファイル転送と共有キューの表示を構成します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function filesView(root, ctx)
{
    root.className = 'files-tool';
    const explanation = document.createElement('p');
    explanation.textContent = ctx.t('Choose a cluster or leave it automatic, then select a PC on each side. Automatic selection uses a cluster shared by both PCs. Click to select; double-click drives and folders to open them. Copy into the other open folder, or drag onto a folder. Existing names are never overwritten.', 'クラスタを選択、または自動のまま、左右のPCを選びます。自動選定では両方のPCが所属する共通クラスタを使います。シングルクリックで選択し、ドライブやフォルダーはダブルクリックで開きます。相手側のフォルダーへコピー、またはドラッグできます。同名の項目は上書きしません。');
    const clusterLabel = document.createElement('label');
    clusterLabel.textContent = ctx.t('Transfer cluster', '転送クラスタ');
    const clusters = document.createElement('select');
    clusterLabel.append(clusters);
    const clusterStatus = document.createElement('p');
    clusterStatus.className = 'small muted';
    clusterStatus.setAttribute('role', 'status');
    const split = document.createElement('div');
    split.className = 'file-workspace';
    const queue = document.createElement('section');
    queue.className = 'transfer-jobs';
    const toolbar = document.createElement('div');
    toolbar.className = 'tool-toolbar';
    const help = document.createElement('details');
    help.className = 'tool-help';
    const summary = document.createElement('summary');
    summary.textContent = ctx.t('Transfer help', '転送の使い方');
    help.append(summary, explanation);
    toolbar.append(clusterLabel, clusterStatus);
    root.append(toolbar, help, split, queue);
    let peers = await ctx.peers(), drag = null, disposed = false, timer, offset = 0;
    const panes = [];
    const reload = ctx.button(ctx.t('Refresh hosts', 'ホスト一覧を更新'), async () =>
    {
        const discovered = await ctx.peers();
        if (disposed || panes.some(item => item.actions?.busy))
        {
            return;
        }
        peers = discovered;
        clusterOptions();
        await selectHostsAsync();
    });
    reload.prepend(ctx.icon('refresh'));

    /** 到達可能なクラスタを選択肢へ反映します。 */
    function clusterOptions()
    {
        const selected = clusters.value;
        clusters.replaceChildren(new Option(ctx.t('Automatic · common cluster', '自動 · 共通クラスタ'), ''));
        const ids = [...new Set(peers.map(peer => peer.clusterId))].sort();
        if (selected && !ids.includes(selected))
        {
            clusters.add(new Option(selected + ctx.t(' · unavailable', ' · 未確認'), selected));
        }
        ids.forEach(id => clusters.add(new Option(id, id)));
        clusters.value = selected;
    }

    /** 選択したクラスタ内のホストを左右の一覧へ対応付けます。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function selectHostsAsync()
    {
        if (panes.some(item => item.actions?.busy))
        {
            return;
        }
        drag = null;
        const resolved = resolveHosts(peers, panes.map(pane => pane.hostId), clusters.value);
        clusterStatus.textContent = resolved.problem ? ctx.t('No available common cluster. Select hosts in the same cluster or refresh discovery.', '利用可能な共通クラスタがありません。同じクラスタのホストを選ぶか、一覧を更新してください。')
            : resolved.clusterId ? ctx.t('Using cluster: ', '使用クラスタ: ') + resolved.clusterId : '';
        await Promise.all(panes.map((pane, index) => pane.bind(resolved.peers[index])));
    }

    clusterOptions();
    clusters.onchange = () => ctx.guard(selectHostsAsync);

    /** 送信または処理する項目を待機列へ追加します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function enqueueAsync(source, destination, directory, paths)
    {
        if (disposed || panes.some(item => item.actions?.busy))
        {
            return;
        }
        if (!source.peer || !destination.peer || !directory || !paths.length)
        {
            ctx.say(ctx.t('Open a destination folder first.', '先にコピー先フォルダーを開いてください。'));
            return;
        }
        const sourcePeer = { clusterId: source.peer.clusterId, peerId: source.peer.peerId, instanceId: source.peer.instanceId };
        const destinationPeer = { clusterId: destination.peer.clusterId, peerId: destination.peer.peerId, instanceId: destination.peer.instanceId };
        const observed = await ctx.peers();
        if (sourcePeer.clusterId !== destinationPeer.clusterId || !currentPeer(observed, sourcePeer) || !currentPeer(observed, destinationPeer))
        {
            ctx.say(ctx.t('The selected PCs are no longer available in the same cluster. Refresh hosts before copying.', '選択したPCを同じクラスタで確認できません。ホスト一覧を更新してからコピーしてください。'));
            return;
        }
        for (const path of paths)
        {
            await ctx.api('/client/transfers', null, { id: crypto.randomUUID(), source: sourcePeer, destination: destinationPeer, sourcePath: path, destinationDirectory: directory });
        }
        ctx.say(ctx.t('Added to the shared copy queue.', '共有コピーキューへ追加しました。'));
        await progressAsync();
    }

    /** ファイル一覧の選択・閲覧・転送操作を構成します。
     * @returns 呼出し元が使用する値です。
     */
    function pane(index)
    {
        const state = { hostId: '', peer: null, directory: null, parent: null, selected: new Set(), entries: new Map(), generation: 0, busy: false, completed: new Set() };
        const section = document.createElement('section');
        section.className = 'file-pane';
        const label = document.createElement('label');
        label.textContent = index === 0 ? ctx.t('Left host', '左側のホスト') : ctx.t('Right host', '右側のホスト');
        const hosts = document.createElement('select');
        label.append(hosts);

        /** 利用可能なホストを選択候補へ反映します。 */
        function hostOptions()
        {
            hosts.replaceChildren(new Option(ctx.t('Select host (PC)', 'ホスト（PC）を選択'), ''));
            const ids = hostIds(peers, clusters.value);
            if (state.hostId && !ids.includes(state.hostId))
            {
                hosts.add(new Option(state.hostId + ctx.t(' · unavailable', ' · 未確認'), state.hostId));
            }
            ids.forEach(id => hosts.add(new Option(id, id)));
            hosts.value = state.hostId;
        }

        hostOptions();
        const toolbar = document.createElement('div');
        toolbar.className = 'toolbar file-navigation';
        const location = document.createElement('form');
        location.className = 'file-location';
        const address = document.createElement('input');
        address.setAttribute('aria-label', ctx.t('Folder path', 'フォルダーパス'));
        address.placeholder = ctx.t('Drives / enter a folder path', 'ドライブ / フォルダーパスを入力');
        const go = document.createElement('button');
        go.type = 'submit';
        go.textContent = ctx.t('Go', '移動');
        location.append(address, go);
        location.onsubmit = event =>
        {
            event.preventDefault();
            void ctx.guard(() => openAsync(address.value.trim() || null));
        };
        const tree = document.createElement('div');
        tree.className = 'file-tree';
        tree.setAttribute('aria-label', ctx.t('Files and folders', 'ファイルとフォルダー'));
        const footer = document.createElement('div');
        footer.className = 'file-pane-footer';
        const count = document.createElement('span');
        count.setAttribute('role', 'status');
        const copy = ctx.button(index === 0 ? ctx.t('Copy selected →', '右へコピー →') : ctx.t('← Copy selected', '← 左へコピー'), async () =>
        {
            const other = panes[1 - index];
            await enqueueAsync(state, other, other.directory, [...state.selected]);
        });
        copy.className = 'primary';
        const drives = ctx.button(ctx.t('Drives', 'ドライブ'), () => openAsync(null));
        drives.prepend(ctx.icon('drive'));
        const up = ctx.button(ctx.t('↑ Up', '↑ 上へ'), () => openAsync(state.parent));
        const refresh = ctx.button(ctx.t('Refresh', '更新'), () => openAsync(state.directory));
        toolbar.append(drives, up, refresh);
        footer.append(count, copy);
        section.append(label, toolbar, location, tree, footer);
        split.append(section);
        state.actions = fileActionControls(section, ctx, {
            read: () => ({ generation: state.generation, directory: state.directory, peer: state.peer, label: state.peer?.hostId ?? '',
                entries: [...state.selected].map(path => state.entries.get(path)).filter(Boolean),
                available: !disposed && !!state.peer && state.directory !== null && !state.busy && !panes.some(item => item.actions?.busy) }),

            changed: () => panes.forEach(item => item.update()),

            create: (snapshot, name) => mutateHostFileAsync(ctx, snapshot.peer, 'directory.create', snapshot.directory.replace(/[\\/]+$/u, '') + '/' + name),

            remove: (snapshot, entry) => mutateHostFileAsync(ctx, snapshot.peer, entry.isDirectory ? 'directory.delete' : 'file.delete', entry.path),

            refresh: snapshot => Promise.all(panes.filter(item => samePeer(item.peer, snapshot.peer) && item.directory === snapshot.directory).map(item => item.refresh()))
        });
        toolbar.append(state.actions.create, state.actions.remove);

        /** 現在の設定と状態から表示を更新します。 */
        function update()
        {
            const locked = state.busy || panes.some(item => item.actions?.busy);
            count.textContent = ctx.t(state.selected.size + ' selected', state.selected.size + ' 件を選択');
            copy.disabled = locked || !state.peer || !state.selected.size || !panes[1 - index]?.directory || panes[1 - index]?.busy || state.peer.clusterId !== panes[1 - index]?.peer?.clusterId;
            drives.disabled = refresh.disabled = address.disabled = go.disabled = !state.peer || locked;
            up.disabled = !state.peer || !state.directory || locked;
            hosts.disabled = clusters.disabled = panes.some(item => item.actions?.busy);
            reload.disabled = clusters.disabled;
            state.actions.update();
        }

        state.update = update;

        /** 各一覧と共通の操作状態を更新します。
         * @returns 呼出し元が使用する値です。
         */
        const updateAll = () => panes.forEach(item => item.update());

        hosts.onchange = () => ctx.guard(async () =>
        {
            state.hostId = hosts.value;
            await selectHostsAsync();
        });

        /** ドロップ位置が有効な転送先かを確認します。 */
        function dropTarget(element, path)
        {
            element.ondragover = event =>
            {
                if (drag && path && !state.busy && !panes.some(item => item.actions?.busy))
                {
                    event.preventDefault();
                    event.dataTransfer.dropEffect = 'copy';
                    element.classList.add('drop-target');
                }
            };
            element.ondragleave = () => element.classList.remove('drop-target');
            element.ondrop = event =>
            {
                event.preventDefault();
                event.stopPropagation();
                element.classList.remove('drop-target');
                if (drag && path && !state.busy && !panes.some(item => item.actions?.busy))
                {
                    const selection = drag;
                    drag = null;
                    void ctx.guard(() => enqueueAsync(selection.source, state, path, selection.paths));
                }
            };
        }

        /** 指定された場所にある子項目を取得します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async function childrenAsync(path, generation, offset = 0)
        {
            if (state.generation !== generation || disposed || !state.peer)
            {
                return;
            }
            const result = await ctx.api('/api/files/browse', state.peer, { path, offset, fromDrives: true });
            if (state.generation !== generation || disposed)
            {
                return;
            }
            if (offset === 0)
            {
                tree.replaceChildren();
                state.entries.clear();
            }
            if (result.problem)
            {
                const problem = document.createElement('p');
                problem.className = 'remote-empty';
                problem.textContent = result.problem;
                tree.append(problem);
                if (!result.entries.length)
                {
                    state.directory = null;
                    state.parent = null;
                    updateAll();
                    return;
                }
            }
            state.directory = result.path;
            state.parent = result.parent;
            result.entries.forEach(entry => state.entries.set(entry.path, entry));
            address.value = result.path || '';
            dropTarget(tree, state.directory);
            dropTarget(location, state.directory);
            appendFileRows(tree, result.entries, ctx, {
                selectable: !!path, selected: state.selected, changed: updateAll, open: openAsync,

                busy: () => state.busy || panes.some(item => item.actions?.busy),

                drop: dropTarget,

                dragEnd: () =>
                {
                    const assignedValue = drag = null;
                    return assignedValue;
                },

                /** ドラッグ元の項目と操作範囲を記録します。 */
                drag(event, paths)
                {
                    drag = { source: { peer: state.peer }, paths };
                    event.dataTransfer.effectAllowed = 'copy';
                    event.dataTransfer.setData('text/plain', paths[0]);
                }
            });
            if (!result.entries.length && !result.problem)
            {
                const empty = document.createElement('p');
                empty.className = 'remote-empty';
                empty.textContent = ctx.t('This folder is empty.', 'このフォルダーは空です。');
                tree.append(empty);
            }
            if (result.nextOffset != null)
            {
                const more = ctx.button(ctx.t('Load more', '続きを表示'), async () =>
                {
                    await childrenAsync(path, generation, result.nextOffset);
                    more.remove();
                });
                tree.append(more);
            }
        }

        /** 指定した対象を開き、表示状態を更新します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async function openAsync(path)
        {
            if (disposed || panes.some(item => item.actions?.busy))
            {
                return;
            }
            const generation = ++state.generation;
            state.directory = null;
            state.parent = null;
            state.selected.clear();
            state.entries.clear();
            state.busy = true;
            tree.replaceChildren();
            address.value = path || '';
            const loading = document.createElement('p');
            loading.className = 'remote-empty';
            loading.textContent = state.peer ? ctx.t('Loading…', '読み込み中…') : ctx.t('Select a host to browse its drives.', 'ホストを選択してドライブを表示します。');
            tree.append(loading);
            tree.setAttribute('aria-busy', 'true');
            updateAll();
            try
            {
                if (state.peer)
                {
                    await childrenAsync(path, generation);
                }
            }
            catch (error)
            {
                if (state.generation === generation)
                {
                    loading.textContent = error.message;
                }
                throw error;
            }
            finally
            {
                if (state.generation === generation)
                {
                    state.busy = false;
                    tree.setAttribute('aria-busy', 'false');
                    updateAll();
                }
            }
        }

        state.refresh = () => openAsync(state.directory);
        state.bind = async peer =>
        {
            hostOptions();
            if (!samePeer(state.peer, peer))
            {
                state.peer = peer;
                await openAsync(null);
            }
            else
            {
                updateAll();
            }
        };
        openAsync(null).catch(error => ctx.say(error.message));
        update();
        return state;
    }

    panes.push(pane(0), pane(1));
    toolbar.append(reload);

    /** 処理結果を照会し、進捗の表示へ反映します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function progressAsync()
    {
        const shared = await ctx.api(`/client/transfers/state?offset=${offset}`);
        if (disposed)
        {
            return;
        }
        offset = shared.offset;
        const jobs = shared.jobs;
        const succeeded = jobs.filter(job => job.state === 'Succeeded');
        for (const pane of panes)
        {
            // Keep the completion pending while browsing or selecting, so a later poll can refresh safely.
            if (pane.busy || pane.selected.size || panes.some(item => item.actions?.busy))
            {
                continue;
            }
            const changed = succeeded.some(job => !pane.completed.has(job.request.id) && pane.peer?.instanceId === job.request.destination.instanceId && pane.directory === job.request.destinationDirectory);
            if (changed)
            {
                await pane.refresh();
            }
            pane.completed = new Set(succeeded.map(job => job.request.id));
        }
        queue.replaceChildren();
        const title = document.createElement('h2');
        title.textContent = ctx.t('Shared transfer queue', '共有転送キュー');
        queue.append(title);
        if ((128) < (shared.totalJobs))
        {
            queue.append(ctx.button(ctx.t(`Next page (${offset + 1}–${offset + jobs.length} / ${shared.totalJobs})`, `次のページ (${offset + 1}–${offset + jobs.length} / ${shared.totalJobs})`), async () =>
            {
                offset = offset + 128 < shared.totalJobs ? offset + 128 : 0;
                await progressAsync();
            }));
        }
        if (shared.unconfirmedPeers)
        {
            const warning = document.createElement('p');
            warning.textContent = ctx.t(`${shared.unconfirmedPeers} peer queues are unconfirmed. Previous progress may be shown.`, `${shared.unconfirmedPeers}個のピアのキューを確認できません。前回の進捗を表示している場合があります。`);
            queue.append(warning);
        }
        if (!jobs.length)
        {
            const empty = document.createElement('p');
            empty.textContent = ctx.t('No queued copies.', 'コピー待ちはありません。');
            queue.append(empty);
        }
        for (const job of jobs)
        {
            const row = document.createElement('div');
            row.className = 'transfer-job';
            const text = document.createElement('div');
            text.textContent = `${job.state} · ${job.request.source.peerId}: ${job.request.sourcePath} → ${job.request.destination.peerId}: ${job.request.destinationDirectory}`;
            const bar = document.createElement('progress');
            bar.max = Math.max(job.totalBytes, 1);
            bar.value = job.bytesCopied;
            const amount = document.createElement('span');
            amount.textContent = `${job.bytesCopied.toLocaleString()} / ${job.totalBytes.toLocaleString()} bytes`;
            row.append(text, bar, amount);
            if (job.problem)
            {
                const issue = document.createElement('p');
                issue.textContent = job.problem;
                row.append(issue);
            }
            queue.append(row);
        }
    }

    /** 処理結果の照会を画面の寿命中だけ繰り返します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function pollAsync()
    {
        await ctx.guard(progressAsync);
        if (!disposed)
        {
            timer = setTimeout(poll, 1000);
        }
    }

    /** 定期照会の失敗を画面へ通知します。
     * @returns 失敗の通知までを含む照会のPromiseです。
     */
    function poll()
    {
        return pollAsync().catch(error => ctx.say(error.message));
    }

    pollAsync().catch(error => ctx.say(error.message));
    return {
        /** 現在の接続・選択・処理中の状態を操作欄へ反映します。 */
        updateControls()
        {
            panes.forEach(state => state.update());
        },

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            disposed = true;
            clearTimeout(timer);
            drag = null;
            panes.forEach(state =>
            {
                state.generation++;
                state.actions.dispose();
            });
        } };
}
