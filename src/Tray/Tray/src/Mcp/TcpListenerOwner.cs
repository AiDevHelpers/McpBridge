using System.Net;
using System.Runtime.InteropServices;

namespace AiDevHelpers.Tray.Mcp;

/// <summary>Finds which process listens on a loopback port. The tray uses it to check that the server that answers is the one that wrote the endpoint record.</summary>
public interface IListenerOwner
{
    /// <summary>The pid of the process listening on the IPv4 port, or null when none does.</summary>
    int? OwnerPid(int port);
}

/// <summary>Asks Windows for the listening TCP table (<c>GetExtendedTcpTable</c>), which names the owner process of each listener across sessions.</summary>
public sealed class TcpListenerOwner : IListenerOwner
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint NoError = 0;
    private const uint InsufficientBuffer = 122;

    public int? OwnerPid(int port)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        for (var attempt = 0; attempt < 4 && result == InsufficientBuffer; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (result == NoError)
                {
                    return Find(buffer, port);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static int? Find(IntPtr table, int port)
    {
        var count = Marshal.ReadInt32(table);
        var row = table + sizeof(int);
        var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
        for (var i = 0; i < count; i++, row += rowSize)
        {
            var entry = Marshal.PtrToStructure<TcpRowOwnerPid>(row);
            // The port is in network byte order in the low 16 bits.
            var localPort = (ushort)IPAddress.NetworkToHostOrder((short)entry.LocalPort);
            if (localPort == port)
            {
                return (int)entry.OwningPid;
            }
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sort, int addressFamily, int tableClass, uint reserved);
}
