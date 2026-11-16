using Xunit;

namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>CIで媒体ツールを用意できなかった場合だけ、依存する試験を除外します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class MediaFactAttribute : FactAttribute
{
    /// <summary>実行途中の失敗をスキップへ変えず、準備結果だけを確認します。</summary>
    public MediaFactAttribute() => Skip = UnavailableReason;

    /// <summary>未設定や実行ファイルの不備は従来どおり試験で検出します。</summary>
    internal static string? UnavailableReason
    {
        get
        {
            string variable = "CONTENTS_TEST_MEDIA_UNAVAILABLE", unavailable = "1";
            if (string.Equals(Environment.GetEnvironmentVariable(variable), unavailable, StringComparison.Ordinal))
            {
                return "FFmpeg/ffprobeを取得できなかったため、実行ファイルが必要な試験をスキップします。";
            }
            return null;
        }
    }
}
