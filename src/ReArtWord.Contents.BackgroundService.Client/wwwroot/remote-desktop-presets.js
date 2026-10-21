/** 回線の用途ごとに、次の接続へ適用する伝送設定をまとめます。 */
export const desktopTransmissionPresets = Object.freeze([
    { id: 'mobile-stability', name: ['Mobile · Stability', 'モバイル回線（安定性優先）'], fps: 8, bitrate: 1024, audioCodec: 'opus', audioBitrate: 64, audioSamples: 36000, keyFrameInterval: 8 },
    { id: 'mobile-quality', name: ['Mobile · Quality', 'モバイル回線（品質優先）'], fps: 30, bitrate: 3072, audioCodec: 'opus', audioBitrate: 96, audioSamples: 24000, keyFrameInterval: 30 },
    { id: 'fixed-stability', name: ['Fixed · Stability', '固定回線（安定性優先）'], fps: 30, bitrate: 2048, audioCodec: 'opus', audioBitrate: 96, audioSamples: 18000, keyFrameInterval: 30 },
    { id: 'fixed-balanced', name: ['Fixed · Balanced', '固定回線（バランス）'], fps: 30, bitrate: 4096, audioCodec: 'opus', audioBitrate: 128, audioSamples: 12000, keyFrameInterval: 60 },
    { id: 'fixed-quality', name: ['Fixed · Quality', '固定回線（品質優先）'], fps: 30, bitrate: 7168, audioCodec: 'opus', audioBitrate: 192, audioSamples: 12000, keyFrameInterval: 120 },
    { id: 'lan', name: ['LAN', 'LAN環境'], fps: 60, bitrate: 10240, audioCodec: 'f32le', audioBitrate: 192, audioSamples: 4800, keyFrameInterval: 30 }
].map(preset => Object.freeze({ codec: 'vp9', variableBitrate: true, ...preset })));

/** 初期表示と初回接続に使う既定の伝送設定です。 */
export const defaultDesktopTransmissionPreset = desktopTransmissionPresets.find(preset => preset.id === 'fixed-balanced');

/** プリセットのキーフレーム間隔を、P自動・B無効・品質指定なしの通信設定へ変換します。
 * @returns 追加コーデック設定の通信値です。
 */
export function desktopPresetCodecSettings(preset)
{
    return { maximumKeyFrameInterval: preset.keyFrameInterval, maximumPFrames: null, maximumBFrames: 0, quality: null };
}

/** 手動設定を含む現在の伝送値から、表示するプリセットを判定します。
 * @returns 一致するプリセット、またはカスタムを示すnullです。
 */
export function findDesktopTransmissionPreset(settings)
{
    const details = settings.codecDetails;
    if (!details || details.maximumPFrames != null || details.maximumBFrames !== 0 || details.quality != null)
    {
        return null;
    }
    const fields = ['codec', 'variableBitrate', 'fps', 'bitrate', 'audioCodec', 'audioSamples'];
    return desktopTransmissionPresets.find(preset => fields.every(field => settings[field] === preset[field])
        && details.maximumKeyFrameInterval === preset.keyFrameInterval
        && (preset.audioCodec === 'f32le' || settings.audioBitrate === preset.audioBitrate)) ?? null;
}
