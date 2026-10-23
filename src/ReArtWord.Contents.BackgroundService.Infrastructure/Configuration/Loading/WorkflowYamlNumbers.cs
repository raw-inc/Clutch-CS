using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeDeserializers;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

/// <summary>JSONで扱える数値を浮動小数点へ丸めず、YAMLから契約JSONへ引き継ぎます。</summary>
internal static class WorkflowYamlNumbers
{
    /// <summary>JSON以外のYAMLスカラーは既存の型判定へ委譲します。</summary>
    /// <returns>桁数と指数を保持したJSONです。</returns>
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

    /// <summary>JSONの項目名へ数値スカラーを出力せず、共有されたYAMLの参照は一度だけ辿ります。</summary>
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

    /// <summary>解析済みのJSON数値だけを表します。</summary>
    private sealed record Number(string Text);

    /// <summary>引用されていないJSON数値を、その字句のまま所有します。</summary>
    private sealed class NumberReader : INodeDeserializer
    {
        /// <summary>文字列やYAML固有表記は既存の読込みへ渡します。</summary>
        /// <returns>この読込みで数値を取得したかです。</returns>
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

    /// <summary>検査済み数値だけをJSON互換のプレーンスカラーへ戻します。</summary>
    private sealed class NumberWriter : IYamlTypeConverter
    {
        /// <summary>任意の文字列を生のJSONとして出力しません。</summary>
        /// <returns>所有する数値型かです。</returns>
        public bool Accepts(Type type) => type == typeof(Number);

        /// <summary>この変換は読込みには使いません。</summary>
        /// <returns>読込みには使用できません。</returns>
        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) => throw new NotSupportedException();

        /// <summary>検証済みの字句を二重引用や再計算せず出力します。</summary>
        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
            => emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, ((Number)value!).Text, ScalarStyle.Plain, true, false));
    }
}
