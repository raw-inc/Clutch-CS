/** 認証済みデータとして固定する操作対象を組み立てます。
 * @returns Coreと共有するUTF-8の認証対象です。
 */
export function loginCredentialContext(target, operationId, userSid)
{
    return new TextEncoder().encode(`Contents.Login.v1\n${target.hostId}\n${target.instanceId.toLowerCase()}\n${operationId.toLowerCase()}\n${userSid}`);
}

/** バイト列を転送可能なBase64へ変換します。
 * @returns 暗号化後の転送値です。
 */
function base64(value)
{
    return btoa(String.fromCharCode(...new Uint8Array(value)));
}

/** パスワードを送信前に暗号化し、対象Coreと一回の変更に結び付けます。
 * @returns 履歴に平文を保存しない資格情報です。
 */
export async function sealLoginCredentialAsync(password, encryptionKey, target, operationId, userSid, cryptoApi = globalThis.crypto)
{
    if (!cryptoApi?.subtle)
    {
        throw new Error('Device.LoginCryptoUnavailable');
    }
    if ((256) < (password.length) || password.includes('\0'))
    {
        throw new Error('Device.LoginCredentialInvalid');
    }
    const plain = new TextEncoder().encode(password), nonce = cryptoApi.getRandomValues(new Uint8Array(12));
    let rawKey;
    try
    {
        const publicKey = await cryptoApi.subtle.importKey('spki', Uint8Array.from(atob(encryptionKey.spki), value => value.charCodeAt(0)), { name: 'RSA-OAEP', hash: 'SHA-256' }, false, ['encrypt']);
        const aes = await cryptoApi.subtle.generateKey({ name: 'AES-GCM', length: 256 }, true, ['encrypt']);
        rawKey = new Uint8Array(await cryptoApi.subtle.exportKey('raw', aes));
        const body = await cryptoApi.subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: loginCredentialContext(target, operationId, userSid), tagLength: 128 }, aes, plain);
        const wrapped = await cryptoApi.subtle.encrypt({ name: 'RSA-OAEP' }, publicKey, rawKey);
        return { keyId: encryptionKey.id, wrappedKey: base64(wrapped), nonce: base64(nonce), ciphertext: base64(body) };
    }
    finally
    {
        plain.fill(0);
        rawKey?.fill(0);
    }
}
