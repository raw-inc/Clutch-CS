/** WindowsのDNS設定に対応する既定値を返します。
 * @returns 呼出し元が使用する値です。
 */
export function windowsDnsDefaults(configuration, family)
{
    return family === 6 && configuration?.automaticDns === true && configuration.dns.length === 3
        && configuration.dns.every((address, index) => address.toLowerCase() === `fec0:0:0:ffff::${index + 1}`);
}
