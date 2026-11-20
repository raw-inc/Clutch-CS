// 接続前のプリセットと、接続中に明示反映する詳細設定の草稿を扱います。
/** 映像コーデックの詳細設定と入力検証を構成します。
 * @returns プリセット適用・草稿照合・読取り・表示更新の操作です。
 */
export function createDesktopCodecSettings(body, ctx, numberSetting, apply, modeChanged, actions = body)
{
    let enabled = false, qualityMode = false, automaticP = true, bEnabled = false, codec = 'h264', connected = false, supported = false, busy = false;
    const custom = ctx.button(ctx.t('Custom codec settings', 'コーデック詳細設定を使用'), () =>
    {
        enabled = !enabled;
        update();
        modeChanged();
    });
    custom.setAttribute('aria-label', ctx.t('Custom codec settings', 'コーデック詳細設定を使用'));
    const interval = numberSetting(ctx.t('Maximum keyframe interval (frames)', '最大キーフレーム間隔（フレーム）'), 8, 1, 240);
    const p = numberSetting(ctx.t('Maximum P frames per keyframe interval', 'キーフレーム間のPフレーム最大数'), 7, 0, 239);
    const pMode = ctx.button(ctx.t('Automatic P frames', 'Pフレームは自動（指定なし）'), () =>
    {
        automaticP = !automaticP;
        update();
        modeChanged();
    });
    pMode.setAttribute('aria-label', ctx.t('Automatic P frames', 'Pフレームは自動（指定なし）'));
    const bMode = ctx.button(ctx.t('Enable B frames', 'Bフレームを有効にする'), () =>
    {
        bEnabled = !bEnabled;
        update();
        modeChanged();
    });
    bMode.setAttribute('aria-label', ctx.t('Enable B frames', 'Bフレームを有効にする'));
    const b = numberSetting(ctx.t('Maximum consecutive B frames', '連続Bフレーム最大数'), 2, 1, 4);
    const mode = ctx.button(ctx.t('Use quality target', '品質指定に変更'), () =>
    {
        qualityMode = !qualityMode;
        update();
        modeChanged();
    });
    mode.setAttribute('aria-label', ctx.t('Quality target mode', '品質指定モード'));
    const quality = numberSetting(ctx.t('Quality value (lower is better)', '品質値（小さいほど高画質）'), 23, 1, 51);
    const pHelp = document.createElement('p'), bHelp = document.createElement('p'), qualityHelp = document.createElement('p');
    pHelp.textContent = ctx.t('A P-frame limit also limits the keyframe interval to P + 1.', 'Pフレーム数を指定すると、キーフレーム間隔も最大「Pフレーム数＋1」になります。');
    qualityHelp.textContent = ctx.t('CRF/CQ tracks quality. Traffic varies without a bitrate target or cap, and replay uses at most 16 frames. Quality values differ by encoder.',
        'CRF／CQで画質を指定すると、ビットレートに上限がなく、映像に応じて通信量が変わります。再復号は最大16フレームです。同じ数値でもエンコーダーによって画質は異なります。');
    const capability = document.createElement('p');
    capability.setAttribute('role', 'status');
    const commit = ctx.button(ctx.t('Apply codec settings', 'コーデック設定を反映'), apply);
    commit.className = 'primary codec-apply';
    body.append(custom, interval.wrapper, pMode, p.wrapper, pHelp, bMode, b.wrapper, bHelp, mode, quality.wrapper, qualityHelp, capability);
    actions.append(commit);
    for (const field of [interval.field, p.field, b.field, quality.field])
    {
        field.oninput = field.onchange = modeChanged;
    }

    /** 現在の設定と状態から表示を更新します。 */
    function update()
    {
        const expectedVp9 = 'vp9';

        const unavailable = busy || connected && !supported;
        custom.disabled = unavailable;
        showSwitch(custom, ctx.t('Custom settings', 'コーデック詳細設定'), enabled);
        for (const field of [interval.field, p.field, b.field, quality.field])
        {
            field.disabled = unavailable || !enabled;
        }
        p.field.disabled ||= automaticP;
        pMode.disabled = unavailable || !enabled;
        showSwitch(pMode, ctx.t('Automatic P frames', 'Pフレームの自動設定'), automaticP);
        bMode.disabled = unavailable || !enabled || codec === 'vp9';
        showSwitch(bMode, ctx.t('B frames', 'Bフレーム'), bEnabled && codec !== 'vp9');
        b.field.disabled ||= codec === 'vp9' || !bEnabled;
        quality.field.disabled ||= !qualityMode;
        quality.field.max = codec === expectedVp9 ? '63' : '51';
        mode.disabled = unavailable || !enabled;
        showSwitch(mode, ctx.t('Quality target', '品質指定'), qualityMode);
        commit.disabled = unavailable;
        bHelp.textContent = codec === expectedVp9 ? ctx.t('VP9 has no B-frame setting.', 'VP9ではBフレームを指定できません。') :
            ctx.t('B frames add delay and keep capturing while the desktop is static.', 'Bフレームを使うと遅延が増え、画面が静止している間も映像を取得します。');
        capability.textContent = connected && !supported ? ctx.t('Update the Core and ClientHost to use codec details.', 'コーデック詳細設定を使用するにはCoreとClientHostを更新してください。') :
            ctx.t('Apply restarts video capture only. Disable custom settings and apply to use the short-keyframe-interval mode.', '設定を反映すると映像の取得を再開します。詳細設定を無効にすると、短いキーフレーム間隔の設定に戻ります。');
    }

    /** 入力中の数値を読み、確定時だけ整数と範囲を検証します。
     * @returns 数値、または入力途中の空欄を示すNaNです。
     */
    function integer(field, validate)
    {
        const value = field.value.trim() ? Number(field.value) : NaN;
        if (validate && (!Number.isInteger(value) || value < Number(field.min) || (Number(field.max)) < (value)))
        {
            throw new Error(ctx.t('Enter codec settings within the displayed integer ranges.', 'コーデック設定には表示範囲内の整数を入力してください。'));
        }
        return value;
    }

    /** 選択中の詳細設定を、検証の有無を指定して読み取ります。
     * @returns 現在の通信設定、または詳細設定が無効ならnullです。
     */
    function settings(validate)
    {
        const vp9 = 'vp9';
        if (!enabled)
        {
            return null;
        }
        return { maximumKeyFrameInterval: integer(interval.field, validate), maximumPFrames: automaticP ? null : integer(p.field, validate),
            maximumBFrames: codec === vp9 || !bEnabled ? 0 : integer(b.field, validate), quality: qualityMode ? integer(quality.field, validate) : null };
    }

    update();
    return {
        /** プリセットの間隔を反映し、P自動・B無効・品質指定なしへ揃えます。 */
        applyPreset(keyFrameInterval)
        {
            enabled = true;
            automaticP = true;
            bEnabled = qualityMode = false;
            interval.field.value = String(keyFrameInterval);
            update();
        },

        /** 入力途中の値を例外にせず、プリセット照合用に読み取ります。
         * @returns 未適用の詳細設定、または詳細設定が無効ならnullです。
         */
        draft()
        {
            return settings(false);
        },

        /** 品質値を使用する設定かを判定します。
         * @returns 呼出し元が使用する値です。
         */
        qualitySelected()
        {
            return enabled && qualityMode;
        },

        /** 保存された値または現在の応答を読み取ります。
         * @returns maximumKeyFrameInterval・maximumPFrames・maximumBFrames・qualityを含む結果です。
         */
        read(selectedCodec)
        {
            codec = selectedCodec;
            update();
            return settings(true);
        },

        /** 現在の設定と状態から表示を更新します。 */
        update(selectedCodec, isConnected, isSupported, isBusy)
        {
            codec = selectedCodec;
            connected = isConnected;
            supported = isSupported;
            busy = isBusy;
            update();
        }
    };
}

/** コーデックの詳細設定が同じかを照合します。
 * @returns 呼出し元が使用する値です。
 */
export function sameDesktopCodecSettings(expected, actual)
{
    if (!expected)
    {
        return actual == null;
    }
    return actual != null && expected.maximumKeyFrameInterval === actual.maximumKeyFrameInterval && expected.maximumPFrames === (actual.maximumPFrames ?? null) &&
        expected.maximumBFrames === actual.maximumBFrames && expected.quality === (actual.quality ?? null);
}

/** ボタンに現在の設定値を表示し、読み上げにも同じ状態を伝えます。 */
function showSwitch(button, label, enabled)
{
    button.setAttribute('role', 'switch');
    button.setAttribute('aria-checked', String(enabled));
    button.textContent = label;
}
