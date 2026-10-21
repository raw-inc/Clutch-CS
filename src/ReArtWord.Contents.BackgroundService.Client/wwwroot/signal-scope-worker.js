import { ScopeHistory, readScopeFrame } from './signal-scope-history.js';
import { scopeRemote } from './signal-scope-remote.js';

const history = new ScopeHistory(), connections = new Map(), names = new Map();
let changed = false, exclusionsTimer = null, nextConnection = 0;
const remote = scopeRemote(history, connections, tell, send, () =>
{
    changed = true;
});

/** 各中継の通信先とプロキシ経路を重複なくまとめます。
 * @returns 取得機構へ渡す明示的な除外集合です。
 */
function exclusions()
{
    const tcp = new Map(), endpoints = new Map();
    for (const connection of connections.values())
    {
        for (const item of connection.endpoints)
        {
            tcp.set(item.address + ':' + item.port, item);
        }
        for (const item of connection.excludedEndpoints ?? [])
        {
            endpoints.set(item.transport + '/' + item.address + ':' + item.port, item);
        }
    }
    return { excludedTcpEndpoints: [...tcp.values()], excludedEndpoints: [...endpoints.values()] };
}

/** 同時に届いた経路更新をまとめ、取得中の接続だけへ反映します。 */
function refreshExclusions()
{
    if (exclusionsTimer !== null)
    {
        return;
    }
    exclusionsTimer = setTimeout(() =>
    {
        exclusionsTimer = null;
        const message = { type: 'exclusions', ...exclusions() }, serialized = JSON.stringify(message);
        for (const connection of connections.values())
        {
            if (connection.running && connection.socket.readyState === WebSocket.OPEN && connection.exclusions !== serialized)
            {
                connection.socket.send(serialized);
                connection.exclusions = serialized;
            }
        }
    }, 100);
}

/** 操作結果と接続状態を描画側へ通知します。 */
function tell(type, value = {})
{
    self.postMessage({ type, ...value });
}

/** 同じIPへ複数の既知hostがある場合も名前を捨てません。 */
function addName(address, name)
{
    const previous = names.get(address);
    if (!previous)
    {
        names.set(address, name);
    }
    else if (!previous.split(' / ').includes(name))
    {
        names.set(address, previous + ' / ' + name);
    }
}

/** 接続が確定している一つのCoreだけへ制御を送ります。 */
function send(connection, value)
{
    if (connection.socket.readyState !== WebSocket.OPEN)
    {
        throw new Error('Capture connection is not ready.');
    }
    connection.socket.send(JSON.stringify(value));
}

/** Workerが報告する状態を表示へ反映し、各hostの失敗を独立させます。 */
function control(connection, message)
{
    const { type } = message;
    switch (type)
    {
        case 'hello':
            if (message.protocolVersion !== 2)
            {
                throw new Error('Unsupported capture protocol version.');
            }
            send(connection, { type: 'list' });
            break;
        case 'transport':
            connection.endpoints = message.endpoints;
            connection.excludedEndpoints = message.excludedEndpoints ?? [];
            refreshExclusions();
            break;
        case 'catalog':
            connection.catalog = message.adapters;
            for (const adapter of message.adapters)
            {
                for (const address of adapter.addresses ?? [])
                {
                    addName(address, connection.host);
                }
            }
            history.setNames(names);
            for (const current of connections.values())
            {
                remote.configure(current, names);
            }
            tell('catalog', { host: connection.host, adapters: message.adapters, errors: message.errors ?? [] });
            changed = true;
            break;
        case 'state':
            clearTimeout(connection.pending);
            connection.running = message.running;
            tell('state', { host: connection.host, ...message });
            refreshExclusions();
            break;
        case 'exclusions':
        case 'configured':
            break;
        case 'search':
        case 'detail':
        case 'cleared':
            remote.control(connection, message);
            break;
        case 'stats':
            connection.stats = message;
            changed = true;
            break;
        case 'error':
            remote.failed(connection, message);
            tell('error', { host: connection.host, message: message.message });
            break;
        default:
            throw new Error('Unknown capture notification.');
    }
}

/** 選択hostごとに一接続だけを作り、原本を描画スレッドへ渡しません。 */
function connect(host, url)
{
    if (connections.has(host) || (16) <= (connections.size))
    {
        return;
    }
    const socket = new WebSocket(url), connection = { host, socket, key: String(++nextConnection), endpoints: [], running: false, stats: null, catalog: null };
    socket.binaryType = 'arraybuffer';
    connections.set(host, connection);
    socket.onmessage = event =>
    {
        try
        {
            const frame = readScopeFrame(event.data);
            if (frame.control)
            {
                control(connection, frame.control);
            }
            else if (frame.detail)
            {
                remote.detail(connection, frame.detail);
            }
            else
            {
                remote.packet(connection, frame.packet);
                history.add(frame.packet, host, performance.now(), connection.key);
                changed = true;
            }
        }
        catch (error)
        {
            tell('error', { host, message: error.message });
            socket.close(4002, 'Invalid capture stream');
        }
    };
    socket.onerror = () => tell('error', { host, message: 'Capture connection failed.' });
    socket.onclose = () =>
    {
        clearTimeout(connection.pending);
        remote.disconnected(connection);
        connection.running = false;
        tell('state', { host, running: false, closed: true });
    };
}

/** 取得状態の応答がないまま放置せず、接続を閉じてCoreの回収を促します。 */
function command(connection, value)
{
    clearTimeout(connection.pending);
    send(connection, value);
    connection.pending = setTimeout(() =>
    {
        tell('error', { host: connection.host, message: 'Capture state was not confirmed. The connection was closed.' });
        connection.socket.close();
    }, 10000);
}

/** キャプチャと独立して表示条件を更新します。 */
function receive(message)
{
    switch (message.type)
    {
        case 'connect':
            connect(message.host, message.url);
            break;
        case 'disconnect':
            connections.get(message.host)?.socket.close();
            connections.delete(message.host);
            break;
        case 'names':
            for (const [address, name] of message.entries)
            {
                addName(address, name);
            }
            history.setNames(names);
            break;
        case 'time-zone':
            history.setTimeZone(message.timeZone);
            break;
        case 'start':
            for (const [host, sources] of message.sources)
            {
                const connection = connections.get(host);
                try
                {
                    command(connection, { type: 'start', sources, ...exclusions() });
                }
                catch (error)
                {
                    tell('error', { host, message: error.message });
                    tell('state', { host, running: false, closed: true });
                    connection?.socket.close();
                }
            }
            break;
        case 'stop':
            for (const connection of connections.values())
            {
                if (connection.socket.readyState === WebSocket.OPEN)
                {
                    command(connection, { type: 'stop' });
                }
            }
            break;
        case 'configure':
            history.configure(message.limit, message.maximumBytes, message.duplicateWindow);
            for (const connection of connections.values())
            {
                remote.configure(connection, names);
            }
            changed = true;
            break;
        case 'search':
            history.search(message.query);
            tell('search', { error: null });
            changed = true;
            break;
        case 'advanced':
            remote.advanced(message.query, message.mode);
            break;
        case 'clear':
            remote.clear();
            history.clear();
            changed = true;
            break;
        case 'view':
            tell('view', { request: message.request, ...history.view(message.start, message.count, message.follow, message.anchor),
                backendDropped: [...connections.values()].reduce((sum, item) => sum + (item.stats?.dropped ?? 0), 0),
                deliveryDropped: [...connections.values()].reduce((sum, item) => sum + (item.stats?.deliveryDropped ?? 0), 0),
                sourceBytes: [...connections.values()].reduce((sum, item) => sum + (item.stats?.retainedBytes ?? 0), 0) });
            break;
        case 'detail':
            remote.select(message.id);
            break;
        case 'dispose':
            remote.dispose();
            for (const connection of connections.values())
            {
                clearTimeout(connection.pending);
                connection.socket.close();
            }
            connections.clear();
            clearTimeout(exclusionsTimer);
            clearInterval(notifications);
            history.clear();
            tell('disposed');
            break;
        default:
            throw new Error('Unknown capture view action.');
    }
}

self.onmessage = event =>
{
    try
    {
        receive(event.data);
    }
    catch (error)
    {
        const queryTypes = ['search', 'advanced'];
        tell(queryTypes.includes(event.data.type) ? event.data.type : 'error', { error: error.message, message: error.message });
    }
};

const notifications = setInterval(() =>
{
    if (changed)
    {
        changed = false;
        tell('changed');
    }
}, 100);
