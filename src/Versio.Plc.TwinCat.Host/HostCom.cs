using System.Reflection;
using System.Runtime.InteropServices;

namespace Versio.Plc.TwinCat.Host;

/// <summary>
/// Late-bound COM against DTE / ITcSysManager. Types are never referenced, so this
/// project compiles without Beckhoff DLLs on the machine that builds Versio.
/// </summary>
internal static class Com
{
    private const int RpcECallRejected = unchecked((int)0x80010001);
    private const int RpcEServerCallRetryLater = unchecked((int)0x8001010A);

    public static T Retry<T>(Func<T> fn)
    {
        for (var i = 0; i < 80; i++)
        {
            try
            {
                return fn();
            }
            catch (COMException ex) when (ex.HResult is RpcECallRejected or RpcEServerCallRetryLater)
            {
                Thread.Sleep(100);
            }
            catch (TargetInvocationException ex)
                when (ex.InnerException is COMException com
                      && com.HResult is RpcECallRejected or RpcEServerCallRetryLater)
            {
                Thread.Sleep(100);
            }
        }

        return fn();
    }

    public static void Retry(Action fn) => Retry(() =>
    {
        fn();
        return 0;
    });

    public static object? Get(object target, string name) =>
        Retry(() => target.GetType().InvokeMember(
            name,
            System.Reflection.BindingFlags.GetProperty | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance,
            binder: null, target, args: null));

    public static object? GetIndexed(object target, string name, object index)
    {
        try
        {
            return Retry(() => target.GetType().InvokeMember(
                name,
                BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                binder: null, target, [index]));
        }
        catch
        {
            return null;
        }
    }

    public static void Set(object target, string name, object? value) =>
        Retry(() => target.GetType().InvokeMember(
            name,
            BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance,
            binder: null, target, [value]));

    public static object? Call(object target, string name, params object?[] args) =>
        Retry(() => target.GetType().InvokeMember(
            name,
            System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance,
            binder: null, target, args));

    public static string? Str(object? target, string name) =>
        target is null ? null : Get(target, name)?.ToString();

    public static int Int(object? target, string name)
    {
        var v = target is null ? null : Get(target, name);
        return v is null ? 0 : Convert.ToInt32(v);
    }
}
