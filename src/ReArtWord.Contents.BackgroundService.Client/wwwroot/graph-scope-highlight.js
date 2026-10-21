/** Shift押下中にだけ、選択した実行区間の外側を暗くします。
 * @returns 再描画後の更新と、イベントを解放する操作です。
 */
export function attachGraphScopeHighlight(svg)
{
    let held = false;

    /** 編集欄での文字入力を範囲表示の操作として扱いません。
     * @returns 図を操作するフォーカス位置ならtrueです。
     */
    const eligible = () => !document.activeElement?.closest('input,textarea,select,[contenteditable]:not([contenteditable="false"]),dialog,[role="dialog"]');

    /** 現在の選択とサーバー側と共通の区間情報を、その場で表示へ反映します。 */
    function update()
    {
        if (!held && !svg.classList.contains('scope-highlight'))
        {
            return;
        }
        const selected = Array.from(svg.querySelectorAll('.graph-node.selected'));
        const scopes = new Set(selected.map(node => node.dataset.selectedScope ?? '0'));
        const active = held && eligible() && selected.length !== 0 && !scopes.has('0');
        svg.classList.toggle('scope-highlight', active);
        for (const node of svg.querySelectorAll('.graph-node'))
        {
            const inside = (node.dataset.scopeMembership ?? '').split(' ').some(scope => scopes.has(scope));
            node.classList.toggle('outside-scope', active && !inside);
        }
    }

    /** Shift単独の押下を検出し、キーの既存用途を妨げません。 */
    const down = event =>
    {
        const shift = 'Shift';
        if (event.key === shift && !event.repeat)
        {
            held = true;
            update();
        }
    };

    /** 押下中の表示を解除します。 */
    const clear = () =>
    {
        held = false;
        update();
    };

    /** Shiftが離れた場合だけ表示を解除します。 */
    const up = event =>
    {
        const shift = 'Shift';
        if (event.key === shift || !event.shiftKey)
        {
            clear();
        }
    };

    /** タブを離れた後にキー状態を持ち越しません。 */
    const visibility = () =>
    {
        if (document.hidden)
        {
            clear();
        }
    };

    document.addEventListener('keydown', down, true);
    document.addEventListener('keyup', up, true);
    document.addEventListener('focusin', update, true);
    document.addEventListener('visibilitychange', visibility);
    window.addEventListener('blur', clear);
    return { update,

        /** 対象の図が破棄されたときに、状態と監視を取り除きます。 */
        dispose()
        {
            clear();
            document.removeEventListener('keydown', down, true);
            document.removeEventListener('keyup', up, true);
            document.removeEventListener('focusin', update, true);
            document.removeEventListener('visibilitychange', visibility);
            window.removeEventListener('blur', clear);
        }
    };
}
