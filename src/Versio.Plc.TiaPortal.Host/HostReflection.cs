using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Versio.Plc.TiaPortal.Host;

internal static partial class HostApp
{
    private static bool Has(string hay, string needle) =>
        hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static object? GetService(object provider, string typeName)
    {
        var serviceType = FindType(typeName);
        if (serviceType is null)
        {
            return null;
        }

        var method = provider.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition
                                 && m.GetParameters().Length == 0);
        try
        {
            return method?.MakeGenericMethod(serviceType).Invoke(provider, null);
        }
        catch
        {
            return null;
        }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName, throwOnError: false, ignoreCase: false);
            if (t is not null)
            {
                return t;
            }
        }

        return null;
    }

    private static object? GetProp(object? target, string name) =>
        target?.GetType().GetProperty(name)?.GetValue(target);

    private static void SetProp(object target, string name, object value) =>
        target.GetType().GetProperty(name)?.SetValue(target, value);

    private static void Call(object target, string name) =>
        target.GetType().GetMethod(name, Type.EmptyTypes)?.Invoke(target, null);

    private static IEnumerable<object> Enumerate(object? composition)
    {
        if (composition is null)
        {
            yield break;
        }

        if (composition is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }

            yield break;
        }

        var getEnumerator = composition.GetType().GetMethod("GetEnumerator", Type.EmptyTypes);
        if (getEnumerator?.Invoke(composition, null) is IEnumerator en)
        {
            while (en.MoveNext())
            {
                if (en.Current is not null)
                {
                    yield return en.Current;
                }
            }
        }
    }

    private static object? FindNamed(object? composition, string name) =>
        Enumerate(composition).FirstOrDefault(o =>
            string.Equals(Str(o, "Name"), name, StringComparison.OrdinalIgnoreCase));

    private static object? GetEnumerable(object target, string name) => GetProp(target, name);

    private static string? Str(object target, string name) =>
        GetProp(target, name)?.ToString();

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

    private static bool Bool(object target, string name)
    {
        var v = GetProp(target, name);
        return v is true;
    }

    private static void TryDispose(object? obj)
    {
        try
        {
            (obj as IDisposable)?.Dispose();
        }
        catch
        {
            // Closing TIA after a failed open still has to return a fatal we already emitted.
        }
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex.Message;
    }

    private static void Emit(string t, params string[] kv)
    {
        var sb = new StringBuilder();
        sb.Append("{\"t\":\"").Append(t).Append('"');
        for (var i = 0; i + 1 < kv.Length; i += 2)
        {
            sb.Append(",\"").Append(kv[i]).Append("\":\"").Append(Esc(kv[i + 1])).Append('"');
        }

        sb.Append('}');
        Console.WriteLine("@@VERSIO@@" + sb);
    }

    private static void EmitFatal(string msg) => Emit("fatal", "msg", msg);

    private static void EmitDone() => Emit("done", "ok", "true");

    private static string Esc(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

    private sealed record PlcRef(string DeviceName, string PlcName, object Software, object DeviceItem);
}
