/** ローカルファイルの読取り・書込みを試験用に再現します。 */
export class LocalFile
{
    kind = 'file'; writes = []; closes = 0; aborts = 0;

    /** 試験で操作する状態とブラウザー境界を初期化します。 */
    constructor(name, contents = '')
    {
        this.name = name;
        this.contents = contents;
    }

    /** 試験で指定した権限状態を返します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async queryPermission()
    {
        return 'granted';
    }

    /** 列挙時に保持したファイルとの実体一致を返します。
     * @returns 同じ試験用ハンドルかです。
     */
    async isSameEntry(other)
    {
        return this === other;
    }

    /** 保持している試験用ファイルを返します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async getFile()
    {
        return new File([this.contents], this.name);
    }

    /** 書込み内容と確定を記録する境界を作成します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async createWritable()
    {
        const parts = [];
        return { write: async bytes =>
        {
            if (this.writeError)
            {
                throw this.writeError;
            }
            this.writes.push(bytes.length);
            parts.push(bytes);
        },

        close: async() =>
        {
            this.closes++;
            if (this.closeError)
            {
                throw this.closeError;
            }
            this.contents = new Uint8Array(await new Blob(parts).arrayBuffer());
        },

        abort: async() =>
        {
            this.aborts++;
        } };
    }
}

/** ローカルディレクトリの列挙・作成を試験用に再現します。 */
export class LocalFolder
{
    kind = 'directory';permission = 'granted';enumerations = 0;

    /** 試験で操作する状態とブラウザー境界を初期化します。 */
    constructor(name, children = [])
    {
        this.name = name;
        this.children = new Map(children.map(child => [child.name, child]));
    }

    /** 試験で指定した権限状態を返します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async queryPermission()
    {
        return this.permission;
    }

    /** 入力された項目を値の一覧へ変換します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async *values()
    {
        this.enumerations++;
        for (const child of this.children.values())
        {
            yield child;
        }
    }

    /** 指定名の試験用ファイルを取得または作成します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async getFileHandle(name, options = {})
    {
        return this.get(name, 'file', options);
    }

    /** 指定名の試験用ディレクトリを取得または作成します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async getDirectoryHandle(name, options = {})
    {
        return this.get(name, 'directory', options);
    }

    /** 列挙時に保持したフォルダーとの実体一致を返します。
     * @returns 同じ試験用ハンドルかです。
     */
    async isSameEntry(other)
    {
        return this === other;
    }

    /** 選択した子項目だけを削除します。
     * @returns ブラウザーの削除要求の完了です。
     */
    async removeEntry(name, options = {})
    {
        const entry = this.children.get(name);
        if (!entry)
        {
            throw new DOMException('Missing entry', 'NotFoundError');
        }
        if (entry.children?.size && !options.recursive)
        {
            throw new DOMException('Directory is not empty', 'InvalidModificationError');
        }
        this.children.delete(name);
    }

    /** 保持している試験状態から指定された値を読み取ります。
     * @returns 呼出し元が使用する値です。
     */
    get(name, kind, options)
    {
        const item = this.children.get(name);
        if (item)
        {
            if (item.kind !== kind)
            {
                throw new DOMException('Entry type mismatch', 'TypeMismatchError');
            }
            return item;
        }
        if (!options.create)
        {
            throw new DOMException('Missing entry', 'NotFoundError');
        }
        const created = kind === 'directory' ? new LocalFolder(name) : new LocalFile(name);
        this.children.set(name, created);
        this.created?.(created);
        return created;
    }
}
