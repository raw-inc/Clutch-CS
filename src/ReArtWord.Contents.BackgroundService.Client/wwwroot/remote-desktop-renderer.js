// A VideoFrame can otherwise take a CPU readback/conversion through Canvas2D.
// Keep one texture/program for the lifetime of an offscreen monitor surface.
/** canvasで使用する映像描画経路を選択します。
 * @returns draw・close・name・tick・readyを含む結果です。
 */
export function createDesktopRenderer(canvas, accelerated = false)
{
    if (!accelerated)
    {
        const context = canvas.getContext('2d', { alpha: false });
        return { draw: (frame, width = canvas.width, height = canvas.height) => context.drawImage(frame, 0, 0, width, height),

            /** 対象を閉じ、関連する状態を整理します。 */
            close()
            {},

            name: 'Canvas2D' };
    }
    const gl = canvas.getContext('webgl2', { alpha: false, antialias: false, depth: false, stencil: false });
    if (!gl)
    {
        return createDesktopRenderer(canvas);
    }
    let program, buffer, texture;
    const shaders = [];
    const frames = new Array(3).fill(null), fences = new Array(3).fill(null);
    const submittedAt = new Float64Array(3);
    let read = 0, count = 0, closed = false;

    /** 所有する資源を解放します。 */
    function release()
    {
        while (count)
        {
            const status = gl.clientWaitSync(fences[read], 0, 0);
            if (status === gl.TIMEOUT_EXPIRED)
            {
                if ((1500) < (performance.now() - submittedAt[read]))
                {
                    throw new Error('Video GPU stopped responding.');
                }
                break;
            }
            if (status === gl.WAIT_FAILED)
            {
                throw new Error('Video GPU synchronization failed.');
            }
            gl.deleteSync(fences[read]);
            fences[read] = null;
            frames[read].close();
            frames[read] = null;
            read = (read + 1) % frames.length;
            count--;
        }
    }

    /** 対象を閉じ、関連する状態を整理します。 */
    function close()
    {
        if (closed)
        {
            return;
        }
        closed = true;

        // Only teardown waits. Normal rendering polls fences with zero timeout.
        if (count && !gl.isContextLost())
        {
            gl.finish();
        }
        for (let i = 0; i < frames.length; i++)
        {
            if (fences[i])
            {
                gl.deleteSync(fences[i]);
            }
            frames[i]?.close();
            fences[i] = frames[i] = null;
        }
        count = 0;
        if (texture)
        {
            gl.deleteTexture(texture);
        }
        if (buffer)
        {
            gl.deleteBuffer(buffer);
        }
        if (program)
        {
            gl.deleteProgram(program);
        }
        for (const shader of shaders)
        {
            gl.deleteShader(shader);
        }
        texture = buffer = program = null;
        shaders.length = 0;
    }

    try
    {
        /** GPUへ渡すシェーダーを検証して作成します。
         * @returns 呼出し元が使用する値です。
         */
        function shader(type, source)
        {
            const value = gl.createShader(type);
            shaders.push(value);
            gl.shaderSource(value, source);
            gl.compileShader(value);
            if (!gl.getShaderParameter(value, gl.COMPILE_STATUS))
            {
                throw new Error('Video shader compilation failed.');
            }
            return value;
        }

        program = gl.createProgram();
        gl.attachShader(program, shader(gl.VERTEX_SHADER, '#version 300 es\nin vec2 p; out vec2 uv; void main(){ gl_Position=vec4(p,0.,1.); uv=vec2((p.x+1.)*.5,(1.-p.y)*.5); }'));
        gl.attachShader(program, shader(gl.FRAGMENT_SHADER, '#version 300 es\nprecision mediump float; in vec2 uv; uniform sampler2D image; out vec4 color; void main(){ color=texture(image,uv); }'));
        gl.linkProgram(program);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS))
        {
            throw new Error('Video shader linking failed.');
        }
        gl.useProgram(program);
        buffer = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
        const position = gl.getAttribLocation(program, 'p');
        gl.enableVertexAttribArray(position);
        gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);
        texture = gl.createTexture();
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        gl.uniform1i(gl.getUniformLocation(program, 'image'), 0);
        gl.viewport(0, 0, canvas.width, canvas.height);
        let checked = false;
        return {
            name: 'WebGL2', close, tick: release,

            /** 処理を開始できる準備状態を確認します。
             * @returns 呼出し元が使用する値です。
             */
            ready()
            {
                release();
                return count < frames.length;
            },

            /** 準備された値を現在の描画領域へ反映します。
             * @returns 呼出し元が使用する値です。
             */
            draw(frame)
            {
                if (gl.isContextLost())
                {
                    throw new Error('Video graphics context was lost.');
                }
                release();
                if (count === frames.length)
                {
                    throw new Error('Video GPU queue is full.');
                }
                gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, frame);
                gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);

                // Read back no pixels and avoid a GPU synchronization per frame.
                // The first upload checks VideoFrame support on this device.
                if (!checked)
                {
                    checked = true;
                    if (gl.getError() !== gl.NO_ERROR)
                    {
                        throw new Error('VideoFrame texture upload is unavailable.');
                    }
                }
                const fence = gl.fenceSync(gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
                if (!fence)
                {
                    gl.finish();
                    throw new Error('Video GPU fence could not be created.');
                }
                const slot = (read + count) % frames.length;
                frames[slot] = frame;
                fences[slot] = fence;
                submittedAt[slot] = performance.now();
                count++;
                gl.flush();

                // Transfer ownership until the GPU has consumed this decoder surface.
                return true;
            }
        };
    }
    catch (error)
    {
        close();
        throw error;
    }
}
