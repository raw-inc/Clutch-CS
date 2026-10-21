// A missing VLAN ID does not establish untagged mode. Only an explicit OS observation does.
/** VLANの方式と値を短い説明に変換します。
 * @returns 呼出し元が使用する値です。
 */
export function vlanLabel(mode, id, translate = key => key)
{
    const expectedUntagged = 'Untagged';
    const expectedAccess = 'Access';

    if (mode === expectedUntagged)
    {
        return translate('Untagged');
    }
    if (mode === expectedAccess && Number.isInteger(id) && (0) < (id))
    {
        return `Access · VLAN ${id}`;
    }
    return mode ?? translate('Unconfirmed');
}

/** VLANの状態に対応する表示色を選びます。
 * @returns 呼出し元が使用する値です。
 */
export function vlanTone(mode)
{
    return ({ Access: 'vlan-access', Trunk: 'vlan-trunk', Untagged: 'vlan-untagged' })[mode] ?? 'warn';
}
