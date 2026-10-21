// 接続前のプリセットと、接続中に明示反映する詳細設定の草稿を扱います。
/** 映像コーデックの詳細設定と入力検証を構成します。
 * @returns プリセット適用・草稿照合・読取り・表示更新の操作です。
 */
export function createDesktopCodecSettings(body, ctx, numberSetting, apply, modeChanged)
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
    const explanation = document.createElement('p');
    const capability = document.createElement('p');
    capability.setAttribute('role', 'status');
    const commit = ctx.button(ctx.t('Apply codec settings', 'コーデック設定を反映'), apply);
    body.append(custom, interval.wrapper, pMode, p.wrapper, bMode, b.wrapper, mode, quality.wrapper, explanation, capability, commit);
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
        custom.setAttribute('aria-pressed', String(enabled));
        for (const field of [interval.field, p.field, b.field, quality.field])
        {
            field.disabled = unavailable || !enabled;
        }
        p.field.disabled ||= automaticP;
        pMode.disabled = unavailable || !enabled;
        pMode.setAttribute('aria-pressed', String(automaticP));
        bMode.disabled = unavailable || !enabled || codec === 'vp9';
        bMode.setAttribute('aria-pressed', String(bEnabled && codec !== 'vp9'));
        b.field.disabled ||= codec === 'vp9' || !bEnabled;
        quality.field.disabled ||= !qualityMode;
        quality.field.max = codec === expectedVp9 ? '63' : '51';
        mode.disabled = unavailable || !enabled;
        mode.textContent = qualityMode ? ctx.t('Use bitrate target', 'ビットレート指定に変更') : ctx.t('Use quality target', '品質指定に変更');
        mode.setAttribute('aria-pressed', String(qualityMode));
        commit.disabled = unavailable;
        explanation.textContent = (codec === expectedVp9 ? ctx.t('VP9: B-frame control is unavailable; P is the inter-frame limit. ', 'VP9ではBフレーム指定は使用しません。Pは非キーフレーム数の上限です。') :
            ctx.t('B frames add delay and keep capturing even when the desktop is static. ', 'Bフレームは遅延を増やし、静止中も映像取得を続けるため負荷・通信量が増えます。')) +
            ctx.t('P is automatic unless an explicit limit is selected. An explicit P limit also bounds the keyframe interval by P + 1. Quality mode uses CRF/CQ without a bitrate target or cap; bandwidth varies with the image. Replay is limited to 16 frames in quality mode. Values are not equivalent across encoders.',
                'Pは自動が既定です。P上限を指定した場合はキーフレーム間隔をP上限＋1でも制限します。品質指定はCRF/CQで、ビットレートの目標・上限を使用せず、映像に応じて通信量が変わります。品質指定では再復号を最大16フレームに制限します。同じ数値でもエンコーダー間で画質は一致しません。');
        capability.textContent = connected && !supported ? ctx.t('Update the Core and ClientHost to use codec details.', 'コーデック詳細設定を使用するにはCoreとClientHostを更新してください。') :
            ctx.t('Apply restarts video capture only. Disable custom settings and apply to use the short-keyframe-interval mode.', '反映すると映像取得を切り替えます。詳細設定をOFFにして反映すると、短いキーフレーム間隔の設定へ戻ります。');
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
