import { compileScopeSearch } from './signal-scope-search.js';

const decoder = new TextDecoder();

/** バイナリフレームを上限内で読み取り、原本と表示メタデータを分けます。
 * @returns 制御またはパケットです。
 */
export function readScopeFrame(buffer)
{
    if (!(buffer instanceof ArrayBuffer) || buffer.byteLength < 2 || (2097152) < (buffer.byteLength))
    {
        throw new Error('Invalid capture frame length.');
    }
    const bytes = new Uint8Array(buffer);
    if (bytes[0] === 1)
    {
        return { control: JSON.parse(decoder.decode(bytes.subarray(1))) };
    }
    if (![2, 3].includes(bytes[0]) || buffer.byteLength < 5)
    {
        throw new Error('Invalid capture frame kind.');
    }
    const length = new DataView(buffer).getUint32(1);
    if (length < 2 || (131072) < (length) || buffer.byteLength < length + 5 || (9000) < (buffer.byteLength - length - 5))
    {
        throw new Error('Invalid capture packet boundaries.');
    }
    const packet = JSON.parse(decoder.decode(bytes.subarray(5, length + 5)));
    validateMetadata(packet);
    if (!Number.isSafeInteger(packet.packetId) || packet.packetId < 1)
    {
        throw new Error('Invalid capture packet identity.');
    }
    if (bytes[0] === 3)
    {
        return { detail: { ...packet, raw: bytes.slice(length + 5) } };
    }
    if (buffer.byteLength !== length + 5 || packet.details !== undefined || packet.raw !== undefined)
    {
        throw new Error('Live capture rows must not contain packet contents.');
    }
    return { packet };
}

/** 原本の有無に依らず、表示と参照に必要な識別情報を検証します。 */
function validateMetadata(packet)
{
    if (typeof packet.sourceId !== 'string' || typeof packet.fingerprint !== 'string' || typeof packet.time !== 'string' || !Number.isFinite(Date.parse(packet.time)))
    {
        throw new Error('Invalid capture metadata.');
    }
}

/** 件数と概算容量を制限し、別の観測点が捉えた同じ一回だけを統合します。 */
export class ScopeHistory
{
    /** 起動時の有限容量を設定します。 */
    constructor(limit = 50000, maximumBytes = 320 * 1024 * 1024, duplicateWindow = 250)
    {
        this.configure(limit, maximumBytes, duplicateWindow);
        this.names = new Map();
        this.advancedQuery = 0;
        this.timeFormat = new Intl.DateTimeFormat('en-GB', { timeZone: 'UTC', hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3, hour12: false });
        this.match = compileScopeSearch('');
        this.clear();
    }

    /** experimental設定にも上限を設けます。 */
    configure(limit, maximumBytes, duplicateWindow)
    {
        if (!Number.isInteger(limit) || limit < 100 || (250000) < (limit) || !Number.isInteger(maximumBytes) || maximumBytes < 1048576 || (1073741824) < (maximumBytes) || !Number.isFinite(duplicateWindow) || duplicateWindow < 0 || (2000) < (duplicateWindow))
        {
            throw new Error('Capture limits are out of range.');
        }
        this.limit = limit;
        this.maximumBytes = maximumBytes;
        this.duplicateWindow = duplicateWindow;
        this.trim();
    }

    /** 取得を止めず保持内容と重複照合だけをリセットします。 */
    clear()
    {
        this.rows = [];
        this.head = 0;
        this.byId = new Map();
        this.recent = new Map();
        this.expiry = [];
        this.expiryHead = 0;
        this.matches = [];
        this.matchHead = 0;
        this.bytes = 0;
        this.nextId = 0;
        this.received = 0;
        this.duplicates = 0;
        this.rolled = 0;
    }

    /** 既知hostの全IPを、表示方式によらず検索対象へ追加します。 */
    setNames(entries)
    {
        this.names = new Map(entries);
        for (const row of this.byId.values())
        {
            const before = row.search.length;
            this.index(row);
            const growth = (row.search.length - before) * 2;
            row.size += growth;
            this.bytes += growth;
        }
        this.trim();
        this.refilter();
    }

    /** 不完全な入力中は前回の有効な検索結果を維持します。 */
    search(query)
    {
        const match = compileScopeSearch(query);
        this.match = match;
        this.refilter();
    }

    /** 表示時刻と同じタイムゾーンの文字列も通常検索へ含めます。 */
    setTimeZone(timeZone)
    {
        this.timeFormat = new Intl.DateTimeFormat('en-GB', { timeZone, hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3, hour12: false });
        this.setNames(this.names);
    }

    /** 取得側の結果は通常検索とAND合成し、古い検索応答を混ぜません。 */
    setAdvanced(requestId)
    {
        this.advancedQuery = requestId;
        this.refilter();
    }

    /** 一つのhostの検索完了時にだけ、まとめて表示条件を更新します。 */
    applyAdvanced(connection, requestId, ids)
    {
        if (requestId !== this.advancedQuery)
        {
            return;
        }
        for (const row of this.byId.values())
        {
            for (const reference of row.references)
            {
                if (reference.connection === connection)
                {
                    reference.matched = ids.has(reference.packetId) || (reference.queryId === requestId && reference.matched);
                    reference.queryId = requestId;
                }
            }
        }
        this.refilter();
    }

    /** ブラウザ内の条件と、現在の取得側条件を合わせます。
     * @returns 表示対象かです。
     */
    accepts(row)
    {
        return this.match(row.search) && (!this.advancedQuery || row.references.some(reference => reference.queryId === this.advancedQuery && reference.matched));
    }

    /** 現在の保持範囲だけを再検索します。 */
    refilter()
    {
        this.matches = [];
        this.matchHead = 0;
        for (const row of this.byId.values())
        {
            row.matched = this.accepts(row);
            if (row.matched)
            {
                this.matches.push(row.id);
            }
        }
    }

    /** 原本や解析詳細を保持せず、一覧と既知host名だけを索引化します。 */
    index(row)
    {
        row.sourceName = this.names.get(row.source) ?? row.source;
        row.destinationName = this.names.get(row.destination) ?? row.destination;
        row.search = [row.id, row.time, this.timeFormat.format(new Date(row.time)), row.source, row.destination, row.sourcePort, row.destinationPort, row.sourceName, row.destinationName, row.protocol, row.transport,
            row.summary, row.observers.join(' ')].join('\n').toLowerCase();
    }

    /** 同一観測点の再送は別件として保持し、他の観測点と一対一で対応させます。
     * @returns 新規行または統合された行です。
     */
    add(packet, host, now = performance.now(), connection = host)
    {
        this.received++;
        this.expire(now);
        const point = host + '/' + packet.sourceId + '/' + (packet.observationPoint ?? ''), time = Date.parse(packet.time);
        const group = this.recent.get(packet.fingerprint) ?? { candidates: [], head: 0, positions: new Map() };
        const { candidates } = group;
        let low = group.head, high = candidates.length;
        const previous = group.positions.get(point) ?? 0;
        while (low < high)
        {
            const middle = (low + high) >>> 1;
            if (candidates[middle].id <= previous)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        for (let position = low; position < candidates.length; position++)
        {
            const candidate = candidates[position];
            const row = this.byId.get(candidate.id);
            if ((0) < (this.duplicateWindow) && row && candidate.points.size < 512 && !candidate.points.has(point) && Math.abs(time - candidate.time) <= this.duplicateWindow)
            {
                candidate.points.add(point);
                group.positions.set(point, row.id);
                row.references.push({ connection, packetId: packet.packetId, queryId: packet.queryId, matched: packet.matched });
                row.size += 128;
                this.bytes += 128;
                const observer = host + ' · ' + packet.sourceId;
                if (!row.observers.includes(observer))
                {
                    const previousSize = row.search.length;
                    row.observers.push(observer);
                    this.index(row);
                    const growth = (row.search.length - previousSize + observer.length) * 2 + 32;
                    row.size += growth;
                    this.bytes += growth;
                }
                if (!row.matched && this.accepts(row))
                {
                    row.matched = true;
                    this.insertMatch(row.id);
                }
                row.observationCount++;
                this.duplicates++;
                this.trim();
                return row;
            }
        }
        const row = { ...packet, id: ++this.nextId, observers: [host + ' · ' + packet.sourceId], observationCount: 1,
            references: [{ connection, packetId: packet.packetId, queryId: packet.queryId, matched: packet.matched }] };
        this.index(row);
        row.size = (row.search.length + JSON.stringify(packet).length) * 2 + 768;
        this.rows.push(row);
        this.byId.set(row.id, row);
        this.bytes += row.size;
        row.matched = this.accepts(row);
        if (row.matched)
        {
            this.matches.push(row.id);
        }
        const candidate = { id: row.id, time, now, fingerprint: packet.fingerprint, points: new Set([point]) };
        candidates.push(candidate);
        group.positions.set(point, row.id);
        this.recent.set(packet.fingerprint, group);
        this.expiry.push(candidate);
        this.trim();
        return row;
    }

    /** 遅れて一致した観測だけを番号順に追加します。 */
    insertMatch(id)
    {
        let low = this.matchHead, high = this.matches.length;
        while (low < high)
        {
            const middle = (low + high) >>> 1;
            if (this.matches[middle] < id)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        this.matches.splice(low, 0, id);
    }

    /** 重複照合には直近の少数の識別子だけを保持し、原本を延命しません。 */
    expire(now)
    {
        while (this.expiryHead < this.expiry.length && ((8192) <= (this.expiry.length - this.expiryHead) || (2000) < (now - this.expiry[this.expiryHead].now)))
        {
            const expired = this.expiry[this.expiryHead++], group = this.recent.get(expired.fingerprint);
            if (group)
            {
                group.head++;
                if (group.head === group.candidates.length)
                {
                    this.recent.delete(expired.fingerprint);
                }
                else if ((1024) < (group.head))
                {
                    group.candidates = group.candidates.slice(group.head);
                    group.head = 0;
                }
            }
        }
        if ((8192) < (this.expiryHead))
        {
            this.expiry = this.expiry.slice(this.expiryHead);
            this.expiryHead = 0;
        }
    }

    /** 古い行をO(1)で除外し、配列の圧縮はまとめて行います。 */
    trim()
    {
        while (this.byId && (this.limit < this.byId.size || this.maximumBytes < this.bytes))
        {
            const row = this.rows[this.head];
            this.rows[this.head++] = null;
            this.byId.delete(row.id);
            this.bytes -= row.size;
            this.rolled++;
        }
        if (!this.rows)
        {
            return;
        }
        while (this.matchHead < this.matches.length && !this.byId.has(this.matches[this.matchHead]))
        {
            this.matchHead++;
        }
        if ((8192) < (this.head))
        {
            this.rows = this.rows.slice(this.head);
            this.head = 0;
        }
        if ((8192) < (this.matchHead))
        {
            this.matches = this.matches.slice(this.matchHead);
            this.matchHead = 0;
        }
    }

    /** 表示する数十行だけを返し、原本と検索インデックスはWorkerから出しません。
     * @returns 可視行と保持統計です。
     */
    view(start, count = 80, follow = false, anchor = null)
    {
        const total = this.matches.length - this.matchHead;
        if (!follow && anchor !== null)
        {
            let low = this.matchHead, high = this.matches.length;
            while (low < high)
            {
                const middle = (low + high) >>> 1;
                if (this.matches[middle] < anchor)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            start = low - this.matchHead;
        }
        const first = follow ? Math.max(0, total - count) : Math.max(0, Math.min(start, total - 1));
        const rows = this.matches.slice(this.matchHead + first, this.matchHead + first + count).map(id =>
        {
            const row = this.byId.get(id);
            return { id, time: row.time, source: row.source, destination: row.destination, sourceName: row.sourceName, destinationName: row.destinationName,
                sourcePort: row.sourcePort, destinationPort: row.destinationPort, protocol: row.protocol, summary: row.summary, value: row.value, observationCount: row.observationCount };
        });
        return { rows, first, total, retained: this.byId.size, bytes: this.bytes, received: this.received, duplicates: this.duplicates, rolled: this.rolled };
    }
}
