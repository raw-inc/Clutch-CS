/// <summary>任意リソースのHTTP応答を、取得済み・未登録・エラーに分けます。</summary>
public static bool Run(int statusCode)
{
    if (statusCode == 200)
    {
        return true;
    }
    if (statusCode == 404)
    {
        return false;
    }
    throw new InvalidOperationException($"Resource lookup returned HTTP {statusCode}.");
}
