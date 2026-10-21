import { values, integer } from './dom.js';

/** IPアドレスを検証して比較用のバイト列へ変換します。
 * @returns 呼出し元が使用する値です。
 */
export function addressBytes(text, family)
{
    if (family === 4)
    {
        const parts = text.split('.');
        if (parts.length !== 4 || parts.some(part => !/^\d{1,3}$/.test(part) || (255) < (Number(part))))
        {
            throw new Error('IPv4');
        }
        return parts.map(Number);
    }
    if (family !== 6 || text.includes('%'))
    {
        throw new Error('IPv6');
    }
    let expanded = text;
    if (text.includes('.'))
    {
        const split = text.lastIndexOf(':'), tail = addressBytes(text.slice(split + 1), 4);
        expanded = text.slice(0, split + 1) + ((tail[0] << 8) | tail[1]).toString(16) + ':' + ((tail[2] << 8) | tail[3]).toString(16);
    }
    const halves = expanded.split('::');
    if ((2) < (halves.length))
    {
        throw new Error('IPv6');
    }
    const left = halves[0] ? halves[0].split(':') : [], right = halves.length === 2 && halves[1] ? halves[1].split(':') : [];
    const count = left.length + right.length;
    if ((halves.length === 1 && count !== 8) || (halves.length === 2 && (8) <= (count)))
    {
        throw new Error('IPv6');
    }
    const words = [...left, ...Array(8 - count).fill('0'), ...right];
    if (words.some(word => !/^[0-9a-f]{1,4}$/i.test(word)))
    {
        throw new Error('IPv6');
    }
    return words.flatMap(word => [parseInt(word, 16) >> 8, parseInt(word, 16) & 255]);
}

/** アドレスが同じネットワーク範囲に属するか判定します。
 * @returns 呼出し元が使用する値です。
 */
export function subnet(address, prefix, family)
{
    const bytes = addressBytes(address, family);
    integer(String(prefix), 0, bytes.length * 8);
    const network = bytes.map((value, index) => value & (255 << (8 - Math.max(0, Math.min(8, prefix - index * 8)))));
    return (family === 4 ? network.join('.') : Array.from({ length: 8 }, (_, index) => ((network[index * 2] << 8) | network[index * 2 + 1]).toString(16)).join(':')) + '/' + prefix;
}

/** 複数のインターフェースを同じ編集単位にできるか確認します。
 * @returns 呼出し元が使用する判定結果です。
 */
export function canGroup(address, family)
{
    const bytes = addressBytes(address, family);
    if (bytes.every(value => value === 0))
    {
        return false;
    }
    if (family === 4)
    {
        return bytes[0] !== 127 && bytes[0] < 224 && !(bytes[0] === 169 && bytes[1] === 254);
    }
    return !(bytes.slice(0, 15).every(value => value === 0) && bytes[15] === 1) && bytes[0] !== 255 && !(bytes[0] === 254 && (bytes[1] & 192) === 128);
}

/** 入力されたアドレス一覧を構造化します。
 * @returns 呼出し元が使用する値です。
 */
export function parseAddresses(text, family)
{
    return values(text).map(value =>
    {
        const parts = value.split('/');
        if (parts.length !== 2)
        {
            throw new Error('address/prefix');
        }
        addressBytes(parts[0], family);
        return { address: parts[0], prefixLength: integer(parts[1], 0, family === 4 ? 32 : 128), automatic: false };
    });
}

/** ゲートウェイの管理方式とアドレス範囲を検証します。 */
function validateGateways(change, state)
{
    if ((change.dhcp ?? state.dhcp) !== false)
    {
        throw new Error('Device.GatewayManagedByDhcp');
    }
    if (change.family === 6 && (change.routerDiscovery ?? state.routerDiscovery) !== false)
    {
        throw new Error('Device.GatewayManagedByRouterDiscovery');
    }
    for (const gateway of change.gateways)
    {
        const bytes = addressBytes(gateway, change.family);
        if (change.family === 6 && bytes[0] === 254 && (bytes[1] & 192) === 128)
        {
            continue;
        }
        if (!(change.addresses ?? state.addresses).some(item => subnet(item.address, item.prefixLength, change.family) === subnet(gateway, item.prefixLength, change.family)))
        {
            throw new Error('Device.GatewayOutsideSubnet');
        }
    }
}

/** ネットワーク設定の値と相互条件を検証します。 */
export function validateNetwork(change, current)
{
    const state = current[change.family === 4 ? 'ipv4' : 'ipv6'];
    if (!state)
    {
        throw new Error('Device.InterfaceUnavailable');
    }
    if (change.automaticDns === false && change.dns?.length === 0 && ((change.dhcp ?? state.dhcp) !== false || (change.family === 6 && (change.routerDiscovery ?? state.routerDiscovery) !== false)))
    {
        throw new Error('Device.EmptyDnsRequiresManualAddressing');
    }
    if (change.addresses != null && (change.dhcp ?? state.dhcp) !== false)
    {
        throw new Error('Device.AddressManagedByDhcp');
    }
    if (change.gateways != null)
    {
        validateGateways(change, state);
    }
    if (Object.keys(change).length < 2)
    {
        throw new Error('Device.NoChanges');
    }
    for (const address of change.dns ?? [])
    {
        addressBytes(address, change.family);
    }
}
