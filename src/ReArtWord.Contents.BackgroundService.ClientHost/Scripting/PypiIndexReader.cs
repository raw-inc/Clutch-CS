using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Scripting;

/// <summary>公開索引を小さいチャンクで読み、JSON全体をメモリーへ保持しません。</summary>
internal sealed class PypiIndexReader
{
    private JsonReaderState state = new(new JsonReaderOptions { MaxDepth = 8 });
    private bool projects, pendingArray, pendingName;

    /// <summary>正式なルート配列を一度以上読み取れたかです。</summary>
    internal bool SeenProjects
    {
        get; private set;
    }

    /// <summary>未完了のトークンを次のチャンクへ残し、名前だけを取り出します。</summary>
    /// <returns>今回消費できたバイト数です。</returns>
    internal int Read(ReadOnlySpan<byte> bytes, bool final, List<string> names)
    {
        Utf8JsonReader reader = new(bytes, final, state);
        while (reader.Read())
        {
            if (pendingName)
            {
                pendingName = false;
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException("Invalid project name.");
                }
                string? name = reader.GetString();
                if (ScriptPackageNames.Valid(name, EScriptPackageKind.Pip))
                {
                    names.Add(ScriptPackageNames.Normalize(name!, EScriptPackageKind.Pip));
                }
            }
            if (pendingArray)
            {
                pendingArray = false;
                projects = reader.TokenType == JsonTokenType.StartArray;
                SeenProjects |= projects;
            }
            if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 1)
            {
                projects = false;
            }
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                pendingArray = reader.CurrentDepth == 1 && reader.ValueTextEquals("projects"u8);
                pendingName = projects && reader.CurrentDepth == 3 && reader.ValueTextEquals("name"u8);
            }
        }
        state = reader.CurrentState;
        return checked((int)reader.BytesConsumed);
    }
}
