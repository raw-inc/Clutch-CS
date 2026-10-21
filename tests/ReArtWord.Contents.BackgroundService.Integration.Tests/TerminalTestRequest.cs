using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>通信試験ではCoreが提供できる権限を選び、権限切替自体は専用試験で検証します。</summary>
internal static class TerminalTestRequest
{
    /// <summary>通常ユーザーを優先し、CIなど通常トークンがない環境では管理者モードを明示します。</summary>
    /// <returns>新しいセッションの作成要求です。</returns>
    internal static async Task<TerminalRequest> CreateAsync(CorePeer core, string? shell = null, CancellationToken cancellationToken = default)
    {
        TerminalCapabilities available = await core.GetAsync<TerminalCapabilities>("terminal.capabilities", cancellationToken).ConfigureAwait(false);
        Assert.True(available.UserAvailable || available.AdministratorAvailable,
            $"Terminal unavailable: user={available.UserReason}; administrator={available.AdministratorReason}");
        return new(Guid.NewGuid(), Guid.NewGuid(), Elevated: !available.UserAvailable, Shell: shell);
    }
}
