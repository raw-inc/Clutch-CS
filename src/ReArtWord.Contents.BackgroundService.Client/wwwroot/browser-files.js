import { hostIds, resolveHosts, currentPeer } from './file-transfer-selection.js';
import { uploadBrowserFile, uploadBrowserDirectory } from './browser-file-upload.js';
import { appendFileRows } from './file-pane-rows.js';
import { requireLocalPermission, localDirectory, listLocalFiles, downloadToLocal, browserDownloadUrl } from './browser-local-files.js';
import { fileActionControls } from './file-action-controls.js';
import { mutateHostFileAsync, createLocalFolderAsync, deleteLocalEntryAsync } from './file-mutations.js';

/** ブラウザーとホスト間のファイル転送画面を構成します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function browserFilesView(root, ctx)
{
    root.className = 'files-tool';
    const toolbar = document.createElement('div');
    toolbar.className = 'tool-toolbar';
    const cluster = document.createElement('select'), host = document.createElement('select');
    cluster.setAttribute('aria-label', ctx.t('Cluster', 'クラスタ'));
    host.setAttribute('aria-label', ctx.t('Host', 'ホスト'));
    const status = document.createElement('span');
    status.className = 'tool-status';
    status.setAttribute('role', 'status');
    const help = document.createElement('details');
    help.className = 'tool-help';
    const summary = document.createElement('summary');
    summary.textContent = ctx.t('Transfer help', '転送の使い方');
    const explanation = document.createElement('p');
    explanation.textContent = ctx.t('Choose a local folder and grant browser access for two-pane file and folder copying. Click to select; double-click or Enter to open folders. Drag onto a destination folder or copy selected items. Keep this page open. Interrupted folder copies may leave completed or empty entries. Without folder access, use file selection and Download links.', 'ローカルフォルダーを選び、ブラウザーでアクセスを許可すると、左右のペインでファイル・フォルダーをコピーできます。クリックで選択、ダブルクリックまたはEnterで開きます。フォルダーへのドラッグ、または選択項目のコピーを使えます。転送中は画面を開いたままにしてください。同名項目は上書きせず、中断時は完了済み・空の項目が残る場合があります。フォルダーを許可しない場合はファイル選択とダウンロードを使えます。');
    help.append(summary, explanation);
    const split = document.createElement('div');
    split.className = 'file-workspace';
    const queue = document.createElement('section');
    queue.className = 'transfer-jobs';
    root.append(toolbar, help, split, queue);
    let peers = [], target, localRoot, busy = false, disposed = false, transfer, drag, choosing = false, jobTimer;
    const jobs = [], panes = [];
    const reload = ctx.button(ctx.t('Refresh hosts', 'ホスト一覧を更新'), loadAsync);
    reload.prepend(ctx.icon('refresh'));
    toolbar.append(cluster, host, reload, status);

    /** 現在の接続・選択・処理中の状態を操作欄へ反映します。 */
    function updateControls()
    {
        cluster.disabled = host.disabled = reload.disabled = busy || choosing || panes.some(pane => pane.actions?.busy);
        for (const pane of panes)
        {
            pane.update();
        }
    }

    /** 利用可能なホストを選択候補へ反映します。 */
    function hostOptions()
    {
        const prior = host.value;
        host.replaceChildren(new Option(ctx.t('Select a host', 'ホストを選択'), ''));
        for (const id of hostIds(peers, cluster.value))
        {
            host.add(new Option(id, id));
        }
        host.value = [...host.options].some(item => item.value === prior) ? prior : '';
    }

    /** 対象と選択状態を照合して切り替えます。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function selectAsync()
    {
        if (busy || disposed || panes.some(pane => pane.actions?.busy))
        {
            return;
        }
        target = resolveHosts(peers, [host.value], cluster.value).peers[0];
        drag = null;
        status.textContent = target ? `${target.clusterId} / ${target.hostId}` : ctx.t('Select an available host', '接続可能なホストを選択');
        await panes[1].open(null);
    }

    /** 対象の情報を取得し、現在の表示へ反映します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function loadAsync()
    {
        if (busy || panes.some(pane => pane.actions?.busy))
        {
            return;
        }
        const discovered = await ctx.peers();
        if (disposed || busy || panes.some(pane => pane.actions?.busy))
        {
            return;
        }
        peers = discovered;
        const prior = cluster.value;
        cluster.replaceChildren(new Option(ctx.t('Automatic cluster', '自動クラスタ'), ''));
        for (const id of [...new Set(peers.map(item => item.clusterId))].sort())
        {
            cluster.add(new Option(id, id));
        }
        cluster.value = [...cluster.options].some(item => item.value === prior) ? prior : '';
        hostOptions();
        await selectAsync();
    }

    cluster.onchange = () => ctx.guard(async() =>
    {
        hostOptions();
        await selectAsync();
    });
    host.onchange = () => ctx.guard(selectAsync);

    /** ファイル一覧の選択・閲覧・転送操作を構成します。
     * @returns 呼出し元が使用する値です。
     */
    function pane(local)
    {
        const state = { local, directory: null, parent: null, selected: new Set(), entries: new Map(), browsing: false, generation: 0 };
        const section = document.createElement('section');
        section.className = 'file-pane';
        const title = document.createElement('h2');
        title.className = 'file-pane-title';
        title.textContent = local ? ctx.t('This device', 'この端末') : ctx.t('Host', 'ホスト');
        title.prepend(ctx.icon('monitor'));
        const navigation = document.createElement('div');
        navigation.className = 'toolbar file-navigation';
        const location = document.createElement('form');
        location.className = 'file-location';
        const address = document.createElement('input');
        address.setAttribute('aria-label', local ? ctx.t('Local folder path', 'ローカルフォルダーパス') : ctx.t('Host folder path', 'ホストのフォルダーパス'));
        const go = document.createElement('button');
        go.type = 'submit';
        go.textContent = ctx.t('Go', '移動');
        location.append(address, go);
        const tree = document.createElement('div');
        tree.className = 'file-tree';
        tree.setAttribute('aria-label', local ? ctx.t('Local files', '端末のファイル') : ctx.t('Host files', 'ホストのファイル'));
        const footer = document.createElement('div');
        footer.className = 'file-pane-footer';
        const count = document.createElement('span');
        count.setAttribute('role', 'status');
        const copy = ctx.button(local ? ctx.t('Copy selected →', '右へコピー →') : ctx.t('← Copy selected', '← 左へコピー'), () => copyItemsAsync(state, [...state.selected].map(path => state.entries.get(path)), panes[local ? 1 : 0].directory));
        copy.className = 'primary';
        const home = ctx.button(local ? ctx.t('Folder root', '選択フォルダー') : ctx.t('Drives', 'ドライブ'), () => openAsync(local ? '' : null));
        home.prepend(ctx.icon(local ? 'folder' : 'drive'));
        const up = ctx.button(ctx.t('↑ Up', '↑ 上へ'), () => openAsync(state.parent));
        const refresh = ctx.button(ctx.t('Refresh', '更新'), () => openAsync(state.directory));
        refresh.prepend(ctx.icon('refresh'));
        navigation.append(home, up, refresh);
        footer.append(count, copy);
        section.append(title, navigation, location, tree, footer);
        split.append(section);
        state.actions = fileActionControls(section, ctx, {
            read: () => ({ generation: state.generation, directory: state.directory, peer: target, root: localRoot,
                label: local ? ctx.t('This device: ', 'この端末: ') + (localRoot?.name ?? '') : target?.hostId ?? '',
                entries: [...state.selected].map(path => state.entries.get(path)).filter(Boolean),
                available: !disposed && state.directory !== null && (local ? !!localRoot : !!target) && !busy && !choosing && !state.browsing && !panes.some(pane => pane.actions?.busy) }),

            changed: updateControls,

            create: (snapshot, name) => local ? createLocalFolderAsync(snapshot.root, snapshot.directory, name, ctx.signal)
                : mutateHostFileAsync(ctx, snapshot.peer, 'directory.create', snapshot.directory.replace(/[\\/]+$/u, '') + '/' + name),

            remove: (snapshot, entry) => local ? deleteLocalEntryAsync(snapshot.root, snapshot.directory, entry, ctx.signal)
                : mutateHostFileAsync(ctx, snapshot.peer, entry.isDirectory ? 'directory.delete' : 'file.delete', entry.path),

            refresh: snapshot => openAsync(snapshot.directory)
        });
        navigation.append(state.actions.create, state.actions.remove);
        let picker, chooser;
        if (local)
        {
            const access = document.createElement('div');
            access.className = 'browser-access';
            picker = ctx.button(ctx.t('Choose local folder', 'ローカルフォルダーを選択'), async() =>
            {
                if (busy || choosing || panes.some(pane => pane.actions?.busy))
                {
                    return;
                }
                choosing = true;
                updateControls();
                try
                {
                    const handle = await globalThis.showDirectoryPicker({ mode: 'readwrite' });
                    await requireLocalPermission(handle, 'readwrite');
                    if (disposed)
                    {
                        return;
                    }
                    localRoot = handle;
                    chooser.value = '';
                    title.textContent = ctx.t('This device · ', 'この端末 · ') + handle.name;
                    title.prepend(ctx.icon('monitor'));
                    await openAsync('');
                }
                catch(error)
                {
                    const expectedAborterror = 'AbortError';

                    if (error.name !== expectedAborterror)
                    {
                        throw error;
                    }
                }
                finally
                {
                    choosing = false;
                    updateControls();
                }
            });
            chooser = document.createElement('input');
            chooser.type = 'file';
            chooser.multiple = true;
            chooser.setAttribute('aria-label', ctx.t('Choose local files', '端末のファイルを選択'));
            chooser.onchange = () =>
            {
                if (busy || choosing || panes.some(pane => pane.actions?.busy))
                {
                    return;
                }
                localRoot = null;
                drag = null;
                state.generation++;
                state.browsing = false;
                state.directory = '';
                state.parent = null;
                state.selected.clear();
                state.entries.clear();
                const files = [...chooser.files];
                if ((4096) < (files.length) || new Set(files.map(file => file.name)).size !== files.length)
                {
                    chooser.value = '';
                    ctx.say(ctx.t('Select at most 4096 files with distinct names.', '重複しない名前のファイルを4096件以内で選択してください。'));
                    render([]);
                    updateControls();
                    return;
                }
                const entries = files.map(file => ({ name: file.name, path: file.name, isDirectory: false, file }));
                entries.forEach(entry => state.selected.add(entry.path));
                title.textContent = ctx.t('Selected local files', '選択した端末ファイル');
                address.value = '';
                render(entries);
                updateControls();
            };
            const permission = document.createElement('p');
            permission.className = 'small muted';
            permission.textContent = typeof globalThis.showDirectoryPicker === 'function'
                ? ctx.t('Only the folder you allow is accessible.', '許可したフォルダーの範囲だけにアクセスします。')
                : ctx.t('Folder access is unavailable in this browser. File selection and downloads remain available.', 'このブラウザーではフォルダーへのアクセスを利用できません。ファイル選択とダウンロードを利用できます。');
            access.append(picker, chooser, permission);
            section.insertBefore(access, navigation);
        }

        /** ドロップされた対象を確認して受け付けます。 */
        function drop(element, path)
        {
            element.ondragover = event =>
            {
                if (drag && drag.local !== local && path !== null && !busy && !state.browsing && !panes.some(pane => pane.actions?.busy))
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
                if (drag && drag.local !== local && path !== null && !busy && !state.browsing && !panes.some(pane => pane.actions?.busy))
                {
                    const items = drag;
                    drag = null;
                    void ctx.guard(() => copyItemsAsync(items, items.entries, path));
                }
            };
        }

        /** 現在の値を表示内容へ反映します。 */
        function render(entries, append = false)
        {
            if (!append)
            {
                tree.replaceChildren();
                state.entries.clear();
            }
            entries.forEach(entry => state.entries.set(entry.path, entry));
            appendFileRows(tree, entries, ctx, { selectable: local || state.directory !== null, selected: state.selected, changed: updateControls, open: openAsync,

                busy: () => busy || state.browsing || panes.some(pane => pane.actions?.busy),

                drop,

                dragEnd: () =>
                {
                    const assignedValue = drag = null;
                    return assignedValue;
                },

                /** ドラッグ元の項目と操作範囲を記録します。 */
                drag(event, paths)
                {
                    drag = { local, entries: paths.map(path => state.entries.get(path)) };
                    event.dataTransfer.effectAllowed = 'copy';
                    event.dataTransfer.setData('text/plain', paths[0]);
                },

                /** 対象要素に表示上の属性と操作を付けます。 */
                decorate(row, entry)
                {
                    if (!local && !entry.isDirectory)
                    {
                        const link = document.createElement('a');
                        link.className = 'button-link';
                        link.textContent = ctx.t('Download', 'ダウンロード');
                        link.href = browserDownloadUrl(target, entry.path);
                        link.download = entry.name;
                        row.append(link);
                    }
                }
            });
            if (!state.entries.size)
            {
                const empty = document.createElement('p');
                empty.className = 'remote-empty';
                empty.textContent = local && !localRoot ? ctx.t('Choose a folder or files on this device.', 'この端末のフォルダーまたはファイルを選択してください。') : ctx.t('This folder is empty.', 'このフォルダーは空です。');
                tree.append(empty);
            }
            drop(tree, state.directory);
            drop(location, state.directory);
        }

        /** 現在の一覧位置に対応するページを取得します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async function pageAsync(path, run, offset = 0)
        {
            const result = await ctx.api('/api/files/browse', target, { path, offset, fromDrives: true });
            if (disposed || run !== state.generation)
            {
                return;
            }
            if (result.problem)
            {
                throw new Error(result.problem);
            }
            state.directory = result.path;
            state.parent = result.parent;
            address.value = result.path || '';
            render(result.entries, offset !== 0);
            if (result.nextOffset != null)
            {
                const more = ctx.button(ctx.t('Load more', '続きを表示'), async() =>
                {
                    await pageAsync(path, run, result.nextOffset);
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
            if (busy || disposed || panes.some(pane => pane.actions?.busy))
            {
                return;
            }
            const run = ++state.generation;
            drag = null;
            state.browsing = true;
            state.selected.clear();
            state.directory = null;
            state.parent = null;
            tree.replaceChildren();
            updateControls();
            try
            {
                if (local)
                {
                    if (localRoot)
                    {
                        const result = await listLocalFiles(localRoot, path || '', ctx.signal);
                        if (run !== state.generation || disposed)
                        {
                            return;
                        }
                        state.directory = path || '';
                        state.parent = path ? path.split('/').slice(0, -1).join('/') : null;
                        address.value = path || '';
                        render(result.entries);
                    }
                    else
                    {
                        state.directory = '';
                        render([...state.entries.values()]);
                    }
                }
                else if (target)
                {
                    await pageAsync(path, run);
                }
                else
                {
                    state.entries.clear();
                    const empty = document.createElement('p');
                    empty.className = 'remote-empty';
                    empty.textContent = ctx.t('Select a host to browse its drives.', 'ホストを選択してドライブを表示します。');
                    tree.append(empty);
                }
            }
            catch(error)
            {
                if (run === state.generation)
                {
                    state.directory = null;
                    const warning = document.createElement('p');
                    warning.className = 'remote-empty';
                    warning.textContent = error.message;
                    tree.replaceChildren(warning);
                }
                throw error;
            }
            finally
            {
                if (run === state.generation)
                {
                    state.browsing = false;
                    updateControls();
                }
            }
        }

        state.open = openAsync;

        /** 送受信先の選択と閲覧状態からコピー可能かを判定します。
         * @returns コピーを開始できる場合はtrueです。
         */
        function canCopy(locked)
        {
            const destination = panes[local ? 1 : 0];
            return !locked && !!target && !!state.selected.size && destination?.directory != null && !destination?.browsing && (local || !!localRoot);
        }

        state.update = () =>
        {
            const available = local ? !!localRoot : !!target, locked = busy || state.browsing || choosing || panes.some(pane => pane.actions?.busy);
            home.disabled = refresh.disabled = address.disabled = go.disabled = !available || locked;
            up.disabled = !available || state.parent === null || locked;
            if (picker)
            {
                picker.disabled = locked || typeof globalThis.showDirectoryPicker !== 'function';
            }
            if (chooser)
            {
                chooser.disabled = busy || choosing || panes.some(pane => pane.actions?.busy);
            }
            copy.disabled = !canCopy(locked);
            state.actions.update();
            count.textContent = ctx.t(`${state.selected.size} selected`, `${state.selected.size} 件を選択`);
        };
        location.onsubmit = event =>
        {
            event.preventDefault();
            void ctx.guard(() => openAsync(local ? address.value.trim().replace(/^\//, '') : address.value.trim() || null));
        };
        return state;
    }

    panes.push(pane(true), pane(false));

    /** 転送ジョブの状態と進捗を一覧へ表示します。 */
    function renderJobs()
    {
        queue.replaceChildren();
        const heading = document.createElement('h2');
        heading.textContent = ctx.t('Browser transfers · keep this page open', '端末との転送 · この画面を開いたままにしてください');
        queue.append(heading);
        for (const job of jobs)
        {
            const row = document.createElement('div');
            row.className = 'transfer-job';
            const label = document.createElement('div');
            label.textContent = `${job.name} · ${job.direction} · ${job.state}`;
            const bar = document.createElement('progress');
            if (job.total || job.done)
            {
                bar.max = Math.max(job.total, 1);
                bar.value = job.done ? bar.max : job.bytes;
            }
            const amount = document.createElement('span');
            amount.textContent = job.bytes.toLocaleString() + ' bytes';
            row.append(label, bar, amount);
            if (job.problem)
            {
                const problem = document.createElement('p');
                problem.textContent = job.problem;
                row.append(problem);
            }
            queue.append(row);
        }
        if (busy)
        {
            queue.append(ctx.button(ctx.t('Cancel transfer', '転送を中止'), () => transfer?.abort()));
        }
    }

    /** 選択項目を送信先へコピーし、結果と進捗を更新します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function copyItemsAsync(source, entries, destinationPath)
    {
        if (!canStartCopy(entries, destinationPath))
        {
            return;
        }
        if ((4096) < (entries.length))
        {
            throw new Error(ctx.t('Select at most 4096 entries.', '一度に選択できる項目は4096件までです。'));
        }
        const peer = { ...target }, folder = localRoot;
        busy = true;
        transfer = new AbortController();
        const active = transfer;

        /** 所有する処理へ取消しを要求します。
         * @returns 呼出し元が使用する値です。
         */
        const abort = () => active.abort();

        ctx.signal.addEventListener('abort', abort, { once: true });
        drag = null;
        updateControls();
        const batch = entries.map(entry => ({ name: entry.name, direction: source.local ? '→ Host' : '→ ' + ctx.t('This device', 'この端末'), state: ctx.t('Queued', '待機中'), bytes: 0, total: entry.file?.size || 0 }));
        jobs.push(...batch);
        if ((128) < (jobs.length))
        {
            jobs.splice(0, jobs.length - 128);
        }
        renderJobs();
        let index = 0;
        try
        {
            if (!currentPeer(await ctx.peers(), peer))
            {
                throw new Error(ctx.t('Host instance changed. Refresh hosts before copying.', 'ホストの実体が変わりました。一覧を更新してください。'));
            }
            if (folder)
            {
                await requireLocalPermission(folder, source.local ? 'read' : 'readwrite');
            }
            const destination = source.local ? null : await localDirectory(folder, destinationPath);
            for (;index < entries.length;index++)
            {
                active.signal.throwIfAborted();
                const entry = entries[index], job = batch[index];
                job.state = ctx.t('Preparing', '準備中');
                renderJobs();

                /** 処理結果を照会し、進捗の表示へ反映します。 */
                const progress = (phase, bytes, total) =>
                {
                    const expectedHashing = 'hashing';

                    job.state = phase === expectedHashing ? ctx.t('Verifying', '内容確認中') : ctx.t('Transferring', '転送中');
                    job.bytes = bytes;
                    if (Number.isSafeInteger(total))
                    {
                        job.total = total;
                    }
                    if (!disposed && !jobTimer)
                    {
                        jobTimer = setTimeout(() =>
                        {
                            jobTimer = null;
                            if (!disposed)
                            {
                                renderJobs();
                            }
                        }, 100);
                    }
                };

                if (source.local)
                {
                    if (entry.isDirectory)
                    {
                        await uploadBrowserDirectory(entry.handle, peer, destinationPath, ctx.api, active.signal, progress);
                    }
                    else
                    {
                        await uploadBrowserFile(entry.file || await entry.handle.getFile(), peer, destinationPath, ctx.api, active.signal, progress);
                    }
                }
                else
                {
                    job.bytes = await downloadToLocal(entry, destination, peer, ctx.api, active.signal, { progress: bytes => progress('downloading', bytes) });
                }
                job.state = ctx.t('Completed', '完了');
                job.total = job.bytes;
                job.done = true;
                if (!disposed)
                {
                    renderJobs();
                }
            }
        }
        catch(error)
        {
            const job = batch[index];
            if (job)
            {
                const expectedAborterror2 = 'AbortError';

                job.state = error.name === expectedAborterror2 ? ctx.t('Cancelled', '中止') : ctx.t('Unconfirmed / failed', '未確認・失敗');
                job.problem = error.message + ctx.t(' Check the destination before retrying; some entries may already exist.', ' 再実行前に保存先を確認してください。一部の項目が作成済みの場合があります。');
            }
            for (let next = index + 1;next < batch.length;next++)
            {
                batch[next].state = ctx.t('Not started', '未実行');
            }
        }
        finally
        {
            ctx.signal.removeEventListener('abort', abort);
            busy = false;
            transfer = null;
            if (!disposed)
            {
                renderJobs();
                await ctx.guard(() => Promise.all(panes.map(pane => pane.open(pane.directory))));
                updateControls();
            }
        }
    }

    /** 転送を始める前に、画面の寿命・実行中操作・対象項目を確認します。
     * @returns 一回の転送を開始できる場合はtrueです。
     */
    function canStartCopy(entries, destinationPath)
    {
        return !(disposed || ctx.signal.aborted || busy || choosing || panes.some(pane => pane.actions?.busy) || !target || destinationPath == null || !entries.length || entries.some(entry => !entry));
    }

    await panes[0].open('');
    await loadAsync();
    updateControls();
    return { updateControls,

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            disposed = true;
            clearTimeout(jobTimer);
            panes.forEach(pane =>
            {
                pane.generation++;
                pane.actions.dispose();
                pane.entries.clear();
                pane.selected.clear();
            });
            transfer?.abort();
            localRoot = null;
            drag = null;
        } };
}
