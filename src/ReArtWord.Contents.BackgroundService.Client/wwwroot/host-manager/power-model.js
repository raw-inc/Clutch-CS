export const powerTimers = { displaySeconds: 'Turn off display after', sleepSeconds: 'Sleep after', hibernateSeconds: 'Hibernate after sleep', unattendedSleepSeconds: 'Sleep after unattended wake' };
export const powerActions = ['Do nothing', 'Sleep', 'Hibernate', 'Shut down'];

/** 秒数の未確認と無効を区別して表示します。
 * @returns 現在値を表す文字列です。
 */
export function powerTime(value, t = key => key)
{
    if (value == null)
    {
        return t('Unconfirmed');
    }
    return value === 0 ? t('Disabled') : `${value / 60} ${t('minutes')}`;
}

/** この画面が管理する自動タイマー全体を確認します。
 * @returns 未確認を無効に数えない状態です。
 */
export function powerStatus(state)
{
    const values = ['ac', 'dc'].flatMap(supply => Object.keys(powerTimers).map(key => state[supply][key]));
    values.push(state.idleShutdown.acSeconds, state.idleShutdown.dcSeconds);
    if (state.restrictions.length || values.some(value => value == null))
    {
        return 'Automatic power settings unconfirmed';
    }
    return values.every(value => value === 0) ? 'All managed automatic timers are disabled' : 'Automatic power timers are enabled';
}

/** 空欄を変更なしとして、分単位入力を有限の秒数へ変換します。
 * @returns 未変更はundefinedです。
 */
export function powerMinutes(value)
{
    if (!value.trim().length)
    {
        return undefined;
    }
    const minutes = Number(value);
    if (!Number.isInteger(minutes) || minutes < 0 || (10080) < (minutes))
    {
        throw new Error('Device.InvalidPowerSetting');
    }
    return minutes * 60;
}

/** 読み取れた自動電源タイマーを無効にする変更予定を作成します。
 * @returns 自動タイマー無効化と高速スタートアップ無効化の予定です。
 */
export function installationPowerChange(state)
{
    const change = { fastStartup: false, idleShutdownAcSeconds: 0, idleShutdownDcSeconds: 0 };
    for (const supply of ['ac', 'dc'])
    {
        change[supply] = Object.fromEntries(Object.keys(powerTimers).filter(key => state[supply][key] != null).map(key => [key, 0]));
    }
    return change;
}

/** 部分変更の現在値と予定値を同じ項目順で整形します。
 * @returns 変更前後の説明です。
 */
export function powerPreview(item, t = key => key)
{
    const ac = 'ac';
    const before = [], after = [], state = item.before.data, change = item.change;
    for (const supply of ['ac', 'dc'])
    {
        for (const [key, value] of Object.entries(change[supply] ?? {}))
        {
            const button = 'powerButtonAction';
            const label = `${t(supply === ac ? 'AC power' : 'Battery power')} · ${t(powerTimers[key] ?? 'Power button action')}`;
            const format = key === button ? value => t(powerActions[value] ?? 'Unconfirmed') : value => powerTime(value, t);
            before.push(`${label}: ${format(state[supply][key])}`);
            after.push(`${label}: ${format(value)}`);
        }
        const key = supply === ac ? 'idleShutdownAcSeconds' : 'idleShutdownDcSeconds';
        if (change[key] !== undefined)
        {
            const label = `${t(supply === ac ? 'AC power' : 'Battery power')} · ${t('Idle shutdown (Clutch-CS)')}`;
            before.push(`${label}: ${powerTime(state.idleShutdown[`${supply}Seconds`], t)}`);
            after.push(`${label}: ${powerTime(change[key], t)}`);
        }
    }
    if (change.fastStartup !== undefined)
    {
        /** 未確認と真偽の現在値を区別します。
         * @returns 設定の説明です。
         */
        const format = value => value == null ? t('Unconfirmed') : t(value ? 'Enabled' : 'Disabled');

        before.push(`${t('Fast Startup')}: ${format(state.fastStartup)}`);
        after.push(`${t('Fast Startup')}: ${format(change.fastStartup)}`);
    }
    return [before.join('\n'), after.join('\n')];
}
