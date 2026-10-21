import { sealLoginCredentialAsync } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/login-credential.js';

let text = '';
for await (const chunk of process.stdin)
{
    text += chunk;
}
const value = JSON.parse(text);
const result = await sealLoginCredentialAsync(value.password, value.key, value.target, value.operationId, value.userSid);
process.stdout.write(JSON.stringify(result));
