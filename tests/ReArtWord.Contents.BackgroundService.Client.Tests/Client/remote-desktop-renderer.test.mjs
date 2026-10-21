import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createDesktopRenderer } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-renderer.js';
import { createDesktopVideo } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-video.js';

/** 試験で制御できるGPU描画境界を構成します。
 * @returns canvas・gl・pending・fences・finished・deletedを含む結果です。
 */
function gpu()
{
    const pending = [], fences = [];
    let uploaded, finished = 0, deleted = 0;
    const gl = { COMPILE_STATUS: 1, LINK_STATUS: 2, NO_ERROR: 0, ALREADY_SIGNALED: 3, TIMEOUT_EXPIRED: 4, WAIT_FAILED: 5,

        createShader: () => ({}),

        /** 試験用シェーダーの内容を記録します。 */
        shaderSource()
        {},

        /** 試験用シェーダーのコンパイル要求を受け付けます。 */
        compileShader()
        {},

        getShaderParameter: () => true,

        createProgram: () => ({}),

        /** 試験用プログラムへシェーダーを関連付けます。 */
        attachShader()
        {},

        /** 試験用の描画プログラムをリンクします。 */
        linkProgram()
        {},

        getProgramParameter: () => true,

        /** 試験用GPUの使用プログラムを指定します。 */
        useProgram()
        {},

        createBuffer: () => ({}),

        /** 試験用GPUのバッファーを選択します。 */
        bindBuffer()
        {},

        /** 試験用GPUへバッファー内容を渡します。 */
        bufferData()
        {},

        getAttribLocation: () => 0,

        /** 試験用GPUの頂点属性を有効にします。 */
        enableVertexAttribArray()
        {},

        /** 試験用GPUの頂点属性の配置を指定します。 */
        vertexAttribPointer()
        {},

        createTexture: () => ({}),

        /** 試験用GPUの使用テクスチャーを選択します。 */
        activeTexture()
        {},

        /** 試験用GPUへテクスチャーを関連付けます。 */
        bindTexture()
        {},

        /** 試験用テクスチャーの設定を指定します。 */
        texParameteri()
        {},

        /** 試験用シェーダーへ整数の設定を渡します。 */
        uniform1i()
        {},

        getUniformLocation: () => 0,

        /** 試験用GPUの描画範囲を指定します。 */
        viewport()
        {},

        isContextLost: () => false,

        /** 試験用GPUへ画像データを渡します。 */
        texImage2D(...args)
        {
            uploaded = args.at(-1);
        },

        /** 試験用GPUの描画要求を記録します。 */
        drawArrays()
        {
            pending.push(uploaded);
        },

        getError: () => 0,

        /** 試験で完了を制御できるGPU同期点を作成します。
         * @returns 呼出し元が使用する値です。
         */
        fenceSync()
        {
            const fence = { done: false };
            fences.push(fence);
            return fence;
        },

        /** 保持している入力または描画要求を送出します。 */
        flush()
        {},

        /** 試験用GPUの同期結果を返します。
         * @returns 呼出し元が使用する値です。
         */
        clientWaitSync(fence, flags, timeout)
        {
            assert.equal(timeout, 0);
            return fence.done ? 3 : 4;
        },

        /** 試験用GPUの同期点を解放します。 */
        deleteSync()
        {
            deleted++;
        },

        /** 処理結果を確定し、待機と監視を終了します。 */
        finish()
        {
            finished++;
            for (const fence of fences)
            {
                fence.done = true;
            }
        },

        /** 試験用GPUのテクスチャーを解放します。 */
        deleteTexture()
        {},

        /** 試験用GPUのバッファーを解放します。 */
        deleteBuffer()
        {},

        /** 試験用GPUのプログラムを解放します。 */
        deleteProgram()
        {},

        /** 試験用GPUのシェーダーを解放します。 */
        deleteShader()
        {}
    };
    const canvas = { width: 64, height: 64,

        getContext: kind =>
        {
            assert.equal(kind, 'webgl2');
            return gl;
        } };
    return { canvas, gl, pending, fences,

        get finished()
        {
            return finished;
        },

        get deleted()
        {
            return deleted;
        } };
}

/** 描画検証に使用するフレームを作成します。
 * @returns timestamp・closed・closeを含む結果です。
 */
const picture = timestamp => ({ timestamp, closed: 0,

    /** 対象を閉じ、関連する状態を整理します。 */
    close()
    {
        this.closed++;
    } });

test('decoder surfaces stay alive until the GPU fence completes, without blocking rendering', () =>
{
    const g = gpu(), renderer = createDesktopRenderer(g.canvas, true), frame = picture(1);
    assert.equal(renderer.draw(frame), true);
    assert.equal(frame.closed, 0, 'asynchronous texture upload still owns the source');
    renderer.tick();
    assert.equal(frame.closed, 0);
    assert.equal(g.finished, 0);
    assert.equal(g.pending[0].closed, 0, 'the GPU must not sample a recycled decoder surface');
    g.fences[0].done = true;
    renderer.tick();
    assert.equal(frame.closed, 1);
    assert.equal(g.deleted, 1);
    renderer.close();
    renderer.close();
    assert.equal(frame.closed, 1);
    assert.equal(g.finished, 0);
});

test('in-flight GPU surfaces are bounded and teardown releases them exactly once', () =>
{
    const g = gpu(), renderer = createDesktopRenderer(g.canvas, true), frames = [picture(1), picture(2), picture(3)];
    for (const frame of frames)
    {
        assert.equal(renderer.ready(), true);
        renderer.draw(frame);
    }
    assert.equal(renderer.ready(), false);
    assert.equal(g.finished, 0);
    renderer.close();
    assert.equal(g.finished, 1);
    assert.equal(g.deleted, 3);
    assert.deepEqual(frames.map(frame => frame.closed), [1, 1, 1]);
});

test('a stalled GPU raises an error without recycling a surface still in flight', t =>
{
    t.mock.method(performance, 'now', () => 0);
    const g = gpu(), renderer = createDesktopRenderer(g.canvas, true), frame = picture(1);
    renderer.draw(frame);
    t.mock.method(performance, 'now', () => 1600);
    assert.throws(() => renderer.tick(), /GPU stopped responding/);
    assert.equal(frame.closed, 0);
    renderer.close();
    assert.equal(frame.closed, 1);
    assert.equal(g.finished, 1);
});

test('presentation keeps its final frame queued while the GPU is saturated', t =>
{
    const saved = Object.getOwnPropertyDescriptor(globalThis, 'VideoDecoder');
    let callbacks;
    globalThis.VideoDecoder = class
    {
        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(value)
        {
            callbacks = value;
            this.state = 'unconfigured';
        }

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {
            this.state = 'closed';
        }
    };
    t.after(() =>
    {
        if (saved)
        {
            Object.defineProperty(globalThis, 'VideoDecoder', saved);
        }
        else
        {
            delete globalThis.VideoDecoder;
        }
    });
    const g = gpu(), renderer = createDesktopRenderer(g.canvas, true), video = createDesktopVideo({ width: 64, height: 64 }, g.canvas, { renderer,

        active: () => true,

        /** 重複した描画要求を次の一回にまとめます。 */
        schedule()
        {},

        acceleration: 'prefer-hardware' });
    const frames = [picture(1), picture(2), picture(3), picture(4)];
    for (const frame of frames)
    {
        callbacks.output(frame);
        video.draw(null);
    }
    assert.equal(video.presented, 3);
    assert.equal(video.lastPresented, 3);
    assert.equal(frames[3].closed, 0);
    g.fences[0].done = true;
    video.draw(null);
    assert.equal(video.presented, 4);
    assert.equal(video.lastPresented, 4);
    video.close();
    assert.deepEqual(frames.map(frame => frame.closed), [1, 1, 1, 1]);
});
