using Microsoft.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>CLR型を、WorkflowEditorが表現できるJSON構造とポート直下のバイナリへ限定します。</summary>
internal static class CSharpScriptTypes
{
    /// <summary>objectはanyとし、循環型や未対応のCLR型・コレクションは拒否します。</summary>
    /// <returns>対応する型か、非対応の場合のnullです。</returns>
    internal static ValueTypeDefinition? Read(ITypeSymbol type, int depth = 0)
    {
        if (16 <= depth)
        {
            return null;
        }
        bool optional = type.NullableAnnotation == NullableAnnotation.Annotated;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            ValueTypeDefinition? underlying = Read(nullable.TypeArguments[0], depth + 1);
            return underlying is null ? null
                                      : underlying with { Optional = true };
        }
        string? primitive = type.SpecialType switch
        {
            SpecialType.System_Object => "any",
            SpecialType.System_Boolean => "boolean",
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 => "integer",
            SpecialType.System_Int64 => "int64",
            SpecialType.System_Single or SpecialType.System_Double => "number",
            SpecialType.System_Decimal or SpecialType.System_UInt64 => "decimal",
            SpecialType.System_Char => "char",
            SpecialType.System_String => "string",
            _ => null,
        };
        if (primitive is not null)
        {
            return new(primitive, Optional: optional);
        }
        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            // ポート直下はバイナリとして渡し、JSONオブジェクト内のbyte[]はJSONのBase64表現を維持します。
            if (array.ElementType.SpecialType == SpecialType.System_Byte)
            {
                return new(depth == 0 ? "binary"
                                      : "string", Optional: optional);
            }
            ValueTypeDefinition? element = Read(array.ElementType, depth + 1);
            return element is null ? null
                                   : new("array", element, Optional: optional);
        }
        string name = type.OriginalDefinition.ToDisplayString();
        bool textRepresentation = name is "System.DateTime" or "System.DateTimeOffset" or "System.Guid";
        if (textRepresentation)
        {
            return new("string", Optional: optional);
        }
        string list = "System.Collections.Generic.List<T>";
        if (type is INamedTypeSymbol generic && string.Equals(name, list, StringComparison.Ordinal))
        {
            ValueTypeDefinition? element = Read(generic.TypeArguments[0], depth + 1);
            return element is null ? null
                                   : new("array", element, Optional: optional);
        }
        string dictionary = "System.Collections.Generic.Dictionary<TKey, TValue>";
        if (type is INamedTypeSymbol map && string.Equals(name, dictionary, StringComparison.Ordinal)
            && Read(map.TypeArguments[0], depth + 1) is { } keyType && DictionaryKeys.Supported(keyType)
            && Read(map.TypeArguments[1], depth + 1) is { } valueType)
        {
            return new("object", Optional: optional, KeyType: keyType, ValueType: valueType);
        }
        if (type is not INamedTypeSymbol named || named.DeclaringSyntaxReferences.Length == 0 || named.IsAbstract || named.IsGenericType || named.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return null;
        }
        if (named.BaseType is { SpecialType: not (SpecialType.System_Object or SpecialType.System_ValueType) })
        {
            return null;
        }
        Dictionary<string, ValueTypeDefinition> properties = new(StringComparer.Ordinal);
        foreach (ISymbol member in named.GetMembers())
        {
            if (member is IFieldSymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false })
            {
                return null;
            }
            if (member is not IPropertySymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public, IsImplicitlyDeclared: false } property)
            {
                continue;
            }
            if (property.IsIndexer || property.GetMethod?.DeclaredAccessibility != Accessibility.Public || property.SetMethod?.DeclaredAccessibility != Accessibility.Public || property.GetAttributes().Length != 0)
            {
                return null;
            }
            ValueTypeDefinition? converted = Read(property.Type, depth + 1);
            if (converted is null || 128 <= properties.Count || !properties.TryAdd(property.Name, converted))
            {
                return null;
            }
        }
        bool constructible = named.IsValueType || named.InstanceConstructors.Any(constructor => constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 0);
        if (!constructible || properties.Count == 0 || named.GetAttributes().Length != 0)
        {
            return null;
        }
        return new("object", Properties: properties, Optional: optional);
    }
}
