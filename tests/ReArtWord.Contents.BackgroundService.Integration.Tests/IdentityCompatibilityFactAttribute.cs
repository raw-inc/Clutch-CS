using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>旧形式の比較用Coreを指定した環境で、新旧実体の通信を確認します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class IdentityCompatibilityFactAttribute : FactAttribute
{
    /// <summary>比較対象を用意していない環境では、未確認の範囲をスキップ理由に残します。</summary>
    public IdentityCompatibilityFactAttribute()
    {
        string? executable = Environment.GetEnvironmentVariable("CONTENTS_IDENTITY_COMPAT_CORE");
        if (!File.Exists(executable))
        {
            Skip = "旧形式のCore実行ファイルをCONTENTS_IDENTITY_COMPAT_COREで指定した環境で確認します。";
        }
    }
}
