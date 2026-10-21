/** hostと取得元の複数選択を、接続状態と一緒に管理します。
 * @returns 選択、状態反映、取得元一覧の操作です。
 */
export function scopeSources(container, ctx, send, changed)
{
    const hosts = new Map();
    const panel = document.createElement('section'), summary = document.createElement('h2'), body = document.createElement('div');
    panel.className = 'scope-sources';
    summary.textContent = ctx.t('Hosts & network interfaces', 'Host・ネットワークインターフェース');
    body.className = 'scope-hosts';
    panel.append(summary, body);
    container.append(panel);

    /** 選択された取得元だけを接続先ごとに返します。
     * @returns host名と明示したbackend/adapterの組です。
     */
    function selected()
    {
        return [...hosts.values()].filter(host => host.check.checked && host.ready).map(host => [host.peer.hostId,
            host.inputs.filter(input => input.check.checked).map(input => ({ backend: input.adapter.backend, adapterId: input.adapter.id }))]).filter(([, sources]) => sources.length);
    }

    /** 開始済みの取得元を表示の変更で差し替えません。 */
    function update()
    {
        const running = [...hosts.values()].some(host => host.running || host.pending);
        for (const host of hosts.values())
        {
            host.check.disabled = running;
            for (const input of host.inputs)
            {
                input.check.disabled = running || !host.ready;
            }
        }
        changed();
    }

    /** 同一hostに複数Peerがある場合は、利用可能な一実体に絞ります。
     * @returns ホスト一覧の取得完了です。
     */
    async function loadAsync()
    {
        const peers = await ctx.peers();
        for (const host of hosts.values())
        {
            send({ type: 'disconnect', host: host.peer.hostId });
        }
        hosts.clear();
        body.replaceChildren();
        const ready = 'ready';
        peers.sort((a, b) => Number(b.status === ready) - Number(a.status === ready));
        send({ type: 'names', entries: peers.map(peer => [peer.address, peer.hostId]) });
        for (const peer of peers)
        {
            if (hosts.has(peer.hostId))
            {
                continue;
            }
            const group = document.createElement('details'), heading = document.createElement('summary'), label = document.createElement('label'), check = document.createElement('input');
            const address = document.createElement('small');
            const interfaces = document.createElement('div'), status = document.createElement('small');
            check.type = 'checkbox';
            check.setAttribute('aria-label', peer.hostId);
            label.append(check, document.createTextNode(peer.hostId));
            interfaces.className = 'scope-interfaces';
            interfaces.textContent = ctx.t('Select this host to load capture interfaces.', 'Hostを選択すると取得元を確認できます。');
            group.className = 'scope-host';
            group.dataset.host = peer.hostId;
            heading.className = 'scope-host-heading';
            address.className = 'scope-host-address';
            address.textContent = peer.address;
            status.className = 'scope-host-state';
            label.append(address);
            heading.append(label, status);
            group.append(heading, interfaces);
            body.append(group);
            const host = { peer, group, check, interfaces, status, inputs: [], ready: false, running: false, pending: false };
            hosts.set(peer.hostId, host);
            label.onclick = event => event.stopPropagation();
            check.onchange = () =>
            {
                if (check.checked)
                {
                    if ((16) < ([...hosts.values()].filter(item => item.check.checked).length))
                    {
                        check.checked = false;
                        ctx.say(ctx.t('Select up to 16 hosts.', '選択できるhostは最大16台です。'));
                        return;
                    }
                    group.open = true;
                    interfaces.textContent = ctx.t('Loading capture interfaces…', '取得元を確認しています…');
                    const secureProtocol = 'https:', url = new URL('/client/signal-scope', location.href);
                    url.protocol = location.protocol === secureProtocol ? 'wss:' : 'ws:';
                    url.search = new URLSearchParams({ cluster: peer.clusterId, peer: peer.peerId, instance: peer.instanceId });
                    send({ type: 'connect', host: peer.hostId, url: url.href });
                }
                else
                {
                    send({ type: 'disconnect', host: peer.hostId });
                    host.inputs = [];
                    host.ready = false;
                    interfaces.textContent = ctx.t('Select this host to load capture interfaces.', 'Hostを選択すると取得元を確認できます。');
                    status.textContent = '';
                }
                update();
            };
        }
        update();
    }

    /** 取得機構とNICを別々に示し、同じNICの複数取得元も選択できます。 */
    function catalog(message)
    {
        const host = hosts.get(message.host);
        if (!host)
        {
            return;
        }
        host.interfaces.replaceChildren();
        host.ready = true;
        host.inputs = [];
        for (const adapter of message.adapters)
        {
            const label = document.createElement('label'), check = document.createElement('input');
            check.type = 'checkbox';
            check.setAttribute('aria-label', adapter.name + ' (' + adapter.backend + ')');
            check.onchange = () =>
            {
                if ((16) < (host.inputs.filter(item => item.check.checked).length))
                {
                    check.checked = false;
                    ctx.say(ctx.t('Select up to 16 capture sources per host.', 'hostごとに選択できる取得元は最大16件です。'));
                }
                update();
            };
            label.title = adapter.details + '\n' + (adapter.addresses ?? []).join(', ');
            label.append(check, document.createTextNode(adapter.name + ' · ' + adapter.backend));
            host.interfaces.append(label);
            host.inputs.push({ check, adapter });
        }
        for (const error of message.errors)
        {
            const note = document.createElement('p');
            note.className = 'scope-source-error';
            note.textContent = error.backend + ': ' + error.message;
            host.interfaces.append(note);
        }
        host.status.textContent = ctx.t('Ready to select', '取得元を選択してください');
        update();
    }

    /** 応答された取得状態だけを実行中と表示します。 */
    function state(message)
    {
        const host = hosts.get(message.host);
        if (host)
        {
            host.pending = false;
            host.running = message.running;
            if (message.closed)
            {
                host.ready = false;
                if (!host.inputs.length)
                {
                    host.interfaces.textContent = ctx.t('Capture interfaces unavailable. Reselect this host to reconnect.', '取得元を確認できません。再接続するにはこのHostを再選択してください。');
                }
            }
            host.status.textContent = message.closed ? ctx.t('Disconnected · reselect to connect', '切断・再選択で接続') : message.running ? ctx.t('Capturing', '取得中') : ctx.t('Stopped', '停止');
            const failed = message.sources?.filter(source => source.error).map(source => source.backend + ': ' + source.error);
            if (failed?.length)
            {
                ctx.say(message.host + ' · ' + failed.join('\n'));
            }
            update();
        }
    }

    return { loadAsync, selected, catalog, state,

        /** 状態確認まで取得元の変更を待ちます。 */
        pending()
        {
            for (const [host] of selected())
            {
                hosts.get(host).pending = true;
            }
            update();
        },

        get running()
        {
            return [...hosts.values()].some(host => host.running || host.pending);
        } };
}
