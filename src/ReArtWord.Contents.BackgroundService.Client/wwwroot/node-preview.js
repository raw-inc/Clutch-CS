const previews = new Map();
const maximumBytes = 64 * 1024 * 1024;
const supported = new Set(['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/bmp', 'audio/wav', 'audio/mpeg', 'audio/ogg', 'audio/flac', 'audio/mp4', 'audio/webm', 'video/mp4', 'video/webm', 'video/ogg']);

/** ノードの値のプレビューを描画と解放に接続します。
 * @returns load・clear・disposeを含む結果です。
 */
export function attachPreview(root)
{
    const content = root.querySelector('[data-preview-content]'), error = root.querySelector('[data-preview-error]');
    let element, url, disposed = false, generation = 0;

    /** 保持している一時状態または表示を消去します。 */
    function clear()
    {
        generation++;
        if (element)
        {
            element.pause?.();
            element.removeAttribute('src');
            element.load?.();
            element.remove();
            element = null;
        }
        if (url)
        {
            URL.revokeObjectURL(url);
            url = null;
        }
        previews.delete(root);
        error.textContent = '';
    }

    return {
        /** 対象の情報を取得し、現在の表示へ反映します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
         */
        async load(stream, type, unsupported, budget)
        {
            const expectedImg = 'img';

            clear();
            const version = generation;
            if (disposed)
            {
                return null;
            }
            if (!supported.has(type))
            {
                return unsupported;
            }
            const tag = type.startsWith('image/') ? 'img' : type.startsWith('audio/') ? 'audio' : 'video';
            const next = document.createElement(tag);
            if (tag !== expectedImg && !next.canPlayType(type))
            {
                return unsupported;
            }
            const bytes = await stream.arrayBuffer();
            if (disposed || generation !== version)
            {
                return null;
            }
            if ((16 * 1024 * 1024) < (bytes.byteLength) || (8) <= (previews.size) || (maximumBytes) < ([...previews.values()].reduce((sum, size) => sum + size, 0) + bytes.byteLength))
            {
                return budget;
            }
            element = next;
            previews.set(root, bytes.byteLength);
            url = URL.createObjectURL(new Blob([bytes], { type }));
            next.addEventListener('error', () =>
            {
                if (!disposed && element === next)
                {
                    error.textContent = unsupported;
                }
            });
            if (tag === expectedImg)
            {
                next.alt = '';
            }
            else
            {
                next.controls = true;
                next.preload = 'metadata';
                next.playsInline = true;
            }
            next.src = url;
            content.append(next);
            return null;
        },

        clear,

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            disposed = true;
            clear();
        }
    };
}
