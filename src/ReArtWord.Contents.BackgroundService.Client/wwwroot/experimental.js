const preferenceKey = 'contents.ui.experimental';

/** このWebUIに保存した、ブラウザー共通の有効化設定を読みます。
 * @returns 常時有効が選択されているかです。
 */
export function readExperimentalPreference(ignoreReadFailure = false)
{
    try
    {
        return localStorage.getItem(preferenceKey) === 'true';
    }
    catch (error)
    {
        if (ignoreReadFailure)
        {
            // 通常の画面は、保存領域が使えなくてもURL指定で利用できます。
            return false;
        }
        throw error;
    }
}

/** 保存できなかった場合は呼出し元へ通知し、保存済みとは表示しません。 */
export function saveExperimentalPreference(enabled)
{
    localStorage.setItem(preferenceKey, enabled === true ? 'true' : 'false');
}

/** 常時有効の設定とURL指定を合わせて判定します。数値指定は既存のSignalScopeだけで使用します。
 * @returns この画面で試験的な機能を表示するかです。
 */
export function experimentalEnabled(allowNumericQuery = false)
{
    const query = new URLSearchParams(location.search).get('experimental');
    const enabledQuery = 'true', numericQuery = '1';
    if (query === enabledQuery || (allowNumericQuery && query === numericQuery))
    {
        return true;
    }
    return readExperimentalPreference(true);
}
