using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeDeserializers;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

/// <summary>YAMLからJSONへ変換する際に、数値の桁数や指数表記を保持します。</summary>
internal static class WorkflowYamlNumbers
{
    /// <summary>数値の丸め誤差を防ぎながら、YAMLをJSONへ変換します。</summary>
    /// <returns>変換後のJSON文字列。</returns>
    internal static string ReadJson(string text)
    {
        object model = new DeserializerBuilder().WithDuplicateKeyChecking().WithMaximumRecursion(64)
            .WithAttemptingUnquotedStringTypeDeserialization()
            .WithNodeDeserializer(new NumberReader(), location => location.Before<ScalarNodeDeserializer>())
            .Build().Deserialize<object>(text);
        NormalizeKeys(model, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return new SerializerBuilder().WithMaximumRecursion(64).JsonCompatible()
            .WithTypeConverter(new NumberWriter()).Build().Serialize(model);
    }

    /// <summary>参照の循環を避けながら、数値のキーをJSONで使用できる文字列に変換します。</summary>
    private static void NormalizeKeys(object? value, HashSet<object> visited)
    {
        if (value is null || !visited.Add(value))
        {
            return;
        }
        if (value is IDictionary<object, object?> mapping)
        {
            foreach (object key in mapping.Keys.ToArray())
            {
                object? child = mapping[key];
                if (key is Number number)
                {
                    mapping.Remove(key);
                    if (!mapping.TryAdd(number.Text, child))
                    {
                        throw new FormatException("同じJSON項目名になるYAMLキーを複数指定できません。");
                    }
                }
                NormalizeKeys(child, visited);
            }
        }
        else if (value is IEnumerable<object?> sequence)
        {
            foreach (object? item in sequence)
            {
                NormalizeKeys(item, visited);
            }
        }
    }

    /// <summary>JSON形式で検証済みの数値文字列。</summary>
    private sealed record Number(string Text);

    /// <summary>YAMLの数値を浮動小数点数に変換せず、文字列として保持します。</summary>
    private sealed class NumberReader : INodeDeserializer
    {
        /// <summary>JSONとして解釈できる数値を読み取ります。その他の値は標準のYAML解析に任せます。</summary>
        /// <returns>数値を読み取った場合はtrue。</returns>
        public bool Deserialize(IParser parser, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer,
            out object? value, ObjectDeserializer rootDeserializer)
        {
            value = null;
            if (expectedType != typeof(object) || parser.Current is not Scalar scalar || scalar.Style != ScalarStyle.Plain
                || !scalar.Tag.IsEmpty || scalar.Value.Length == 0 || scalar.Value[0] is not ('-' or >= '0' and <= '9'))
            {
                return false;
            }
            try
            {
                using JsonDocument document = JsonDocument.Parse(scalar.Value);
                if (document.RootElement.ValueKind != JsonValueKind.Number)
                {
                    return false;
                }
                value = new Number(scalar.Value);
                parser.MoveNext();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>保持した数値文字列を、JSONの数値として出力します。</summary>
    private sealed class NumberWriter : IYamlTypeConverter
    {
        /// <summary>検証済みの数値文字列を表す型かどうかを判定します。</summary>
        /// <returns>Number型の場合はtrue。</returns>
        public bool Accepts(Type type) => type == typeof(Number);

        /// <summary>書き出し専用のため、このメソッドは使用できません。</summary>
        /// <returns>常にNotSupportedExceptionを送出するため、値は返しません。</returns>
        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) => throw new NotSupportedException();

        /// <summary>丸め誤差が生じないよう、読み取り時の数値表記で出力します。</summary>
        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
            => emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, ((Number)value!).Text, ScalarStyle.Plain, true, false));
    }
}
