namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>グループ内の起動順と、直前の起動から待つ時間を指定します。</summary>
/// <param name="ApplicationId">同じ所有先に登録されたアプリケーションIDです。</param>
/// <param name="DelayMilliseconds">このアプリケーションの起動前に待つ時間です。先頭ではグループ操作の起動段階開始から数えます。</param>
public sealed record ProcessGroupMember(string ApplicationId, int DelayMilliseconds = 0);
