namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>一つの操作の生成方式を非同期の子処理へ引き継ぎ、並行する別操作とは分離します。</summary>
public sealed class DerivedIdentityScope : IDisposable
{
    private static readonly AsyncLocal<EDerivedIdFormat?> active = new();
    private readonly EDerivedIdFormat? previous;
    private bool disposed;

    /// <summary>新規受付で選んだ方式、または元操作に保存されている方式を指定します。</summary>
    public DerivedIdentityScope(EDerivedIdFormat format)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "未対応の操作ID生成方式です。");
        }
        previous = active.Value;
        active.Value = format;
    }

    /// <summary>生成方式を明示していない旧経路では、元のIDを変えません。</summary>
    public static EDerivedIdFormat Current => active.Value ?? EDerivedIdFormat.Legacy;

    /// <summary>この操作が終わったら、呼出元の方式へ戻します。</summary>
    public void Dispose()
    {
        if (!disposed)
        {
            active.Value = previous;
            disposed = true;
        }
    }
}
