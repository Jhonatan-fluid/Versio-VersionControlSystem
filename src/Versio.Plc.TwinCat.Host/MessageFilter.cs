using System.Runtime.InteropServices;

namespace Versio.Plc.TwinCat.Host;

/// <summary>
/// COM message filter. Without it, <c>RPC_E_CALL_REJECTED</c> (0x80010001) is a flake,
/// not a failure — measured on this machine 2026-08-19: DTE create succeeded in 2.6 s
/// and the next member access was rejected. Beckhoff documents the filter; it is not
/// optional.
/// </summary>
internal sealed class MessageFilter : IOleMessageFilter
{
    public static void Register()
    {
        CoRegisterMessageFilter(new MessageFilter(), out _);
    }

    public static void Revoke()
    {
        CoRegisterMessageFilter(null, out _);
    }

    int IOleMessageFilter.HandleInComingCall(
        int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) =>
        0; // SERVERCALL_ISHANDLED

    int IOleMessageFilter.RetryRejectedCall(
        IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
    {
        // SERVERCALL_RETRYLATER. Returning 99 asks COM to retry immediately rather
        // than failing the call — the DTE is busy, not broken.
        if (dwRejectType == 2)
        {
            return 99;
        }

        return -1;
    }

    int IOleMessageFilter.MessagePending(
        IntPtr hTaskCallee, int dwTickCount, int dwPendingType) =>
        2; // PENDINGMSG_WAITDEFPROCESS

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(
        IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);
}

[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

    [PreserveSig]
    int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
}
