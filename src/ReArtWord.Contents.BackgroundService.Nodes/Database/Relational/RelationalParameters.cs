using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Nodes.Database.Relational;

/// <summary>業務SQLの値を文字列連結せずADO.NETパラメーターへ渡します。</summary>
internal static class RelationalParameters
{
    /// <summary>名前付きパラメーターの型を保って設定します。</summary>
    internal static void Bind(DbCommand command, JsonElement values, JsonElement types)
    {
        if (values.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return;
        }
        if (values.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("database.parametersObjectRequired");
        }
        foreach (JsonProperty property in values.EnumerateObject())
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = property.Name;
            string? type = null;
            if (types.ValueKind == JsonValueKind.Object && types.TryGetProperty(property.Name, out JsonElement declared))
            {
                type = declared.GetString();
            }
            (DbType kind, object value) = Convert(property.Value, type);
            parameter.DbType = kind;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    /// <summary>明示型またはJSON区分から対応可能なスカラーだけを生成します。</summary>
    /// <returns>プロバイダーへ渡す型と値です。</returns>
    private static (DbType Kind, object Value) Convert(JsonElement value, string? type)
    {
        if (type is null)
        {
            type = value.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Number when value.TryGetInt64(out _) => "integer",
                JsonValueKind.Number => "decimal",
                JsonValueKind.String or JsonValueKind.Null => "string",
                _ => throw new FormatException("database.parameterTypeRequired"),
            };
        }
        DbType kind = type switch
        {
            "string" or "json" => DbType.String,
            "integer" => DbType.Int64,
            "number" => DbType.Double,
            "decimal" => DbType.Decimal,
            "boolean" => DbType.Boolean,
            "binary" => DbType.Binary,
            "datetime" => DbType.DateTimeOffset,
            "guid" => DbType.Guid,
            _ => throw new FormatException("database.parameterTypeUnsupported"),
        };
        if (value.ValueKind == JsonValueKind.Null)
        {
            return (kind, DBNull.Value);
        }
        object converted = type switch
        {
            "string" => value.GetString()!,
            "integer" => value.GetInt64(),
            "number" => Finite(value.GetDouble()),
            "decimal" => value.GetDecimal(),
            "boolean" => value.GetBoolean(),
            "binary" => value.GetBytesFromBase64(),
            "datetime" => DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            "guid" => value.GetGuid(),
            "json" => value.GetRawText(),
            _ => throw new FormatException("database.parameterTypeUnsupported"),
        };
        return (kind, converted);
    }

    /// <summary>無限大やNaNをDBへ暗黙に渡しません。</summary>
    /// <returns>有限数です。</returns>
    private static double Finite(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new FormatException("database.nonFiniteParameter");
        }
        return value;
    }
}
