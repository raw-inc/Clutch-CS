using System.Reflection;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>単発・継続実行で同じ引数変換と固定パッケージを使用します。</summary>
internal static class CSharpScriptInvocation
{
    /// <summary>子プロセス内でだけ利用者コードをロードして呼び出します。</summary>
    /// <returns>同期値、Task、または非同期列挙です。</returns>
    internal static object? Invoke(CSharpWorkerRequest request, CancellationToken cancellationToken = default)
    {
        ScriptAssemblyLoadContext loadContext = new(request.Assemblies ?? [], request.NativeLibraries ?? []);
        Assembly assembly = loadContext.LoadFromAssemblyPath(request.AssemblyPath);
        MethodInfo method = assembly.GetType("WorkflowScript", true)!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidDataException("script.entryMissing");
        object?[] arguments = method.GetParameters().Select(parameter =>
        {
            if (request.SourcePipe is not null && parameter.ParameterType == typeof(CancellationToken))
            {
                return (object)cancellationToken;
            }
            bool present = request.Arguments.TryGetProperty(parameter.Name!, out JsonElement supplied);
            if ((!present || supplied.ValueKind == JsonValueKind.Null) && parameter.HasDefaultValue)
            {
                return parameter.DefaultValue;
            }
            if (parameter.IsDefined(typeof(ParamArrayAttribute), false) && parameter.ParameterType.GetElementType() is { } element)
            {
                JsonElement[] values = supplied.EnumerateArray().ToArray();
                Array array = Array.CreateInstance(element, values.Length);
                for (int index = 0; index < values.Length; index++)
                {
                    array.SetValue(JsonSerializer.Deserialize(values[index], element, CSharpScriptWorker.ValueOptions), index);
                }
                return array;
            }
            return JsonSerializer.Deserialize(supplied, parameter.ParameterType, CSharpScriptWorker.ValueOptions);
        }).ToArray();
        return method.Invoke(null, arguments);
    }
}
