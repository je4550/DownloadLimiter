using System.ComponentModel;
using System.Runtime.InteropServices;
using DownloadLimit.Core;

namespace DownloadLimit.Windows;

public sealed unsafe class WinDivertTransport : IPacketTransport
{
    private const int ErrorIoPending = 997, ErrorNoData = 232, ErrorAborted = 995;
    private readonly object _gate = new();
    private readonly ThreadLocal<ManualResetEvent> _sendEvents = new(() => new(false), true);
    private nint _handle;
    private bool _stopped, _closed, _resourcesDisposed;
    private int _activeOperations;

    public static void ConfigureLibrary(string directory)
    {
        NativeLibrary.SetDllImportResolver(typeof(WinDivertTransport).Assembly, (name, _, _) =>
            name == "WinDivert.dll" ? NativeLibrary.Load(Path.Combine(directory, "WinDivert.dll")) : 0);
    }

    public WinDivertTransport(string filter, bool sniff = false)
    {
        ValidateFilter(filter);
        _handle = Native.Open(filter, 0, sniff ? (short)0 : (short)100, sniff ? 1UL | 4UL : 0UL);
        if (_handle == -1 || _handle == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            _handle = 0;
            _sendEvents.Dispose();
            throw Explain(error, "opening WinDivert");
        }
        try
        {
            SetParameter(0, 1024);
            SetParameter(1, 100);
            SetParameter(2, 1024 * 1024);
        }
        catch { Dispose(); throw; }
    }

    public static void ValidateFilter(string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        if (!Native.CompileFilter(filter, 0, 0, 0, out nint error, out uint position))
            throw new ArgumentException($"Invalid WinDivert filter at character {position}: {Marshal.PtrToStringAnsi(error)}. " +
                $"Near: {filter.Substring(Math.Max(0, (int)position - 35), Math.Min(90, filter.Length - Math.Max(0, (int)position - 35)))}", nameof(filter));
    }

    private void SetParameter(uint name, ulong value)
    {
        if (!Native.SetParam(_handle, name, value))
            throw Explain(Marshal.GetLastPInvokeError(), "setting driver queue bounds");
    }

    private void BeginOperation()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _activeOperations++;
        }
    }

    private void EndOperation()
    {
        lock (_gate)
        {
            _activeOperations--;
            CleanupEvents();
        }
    }

    private void CleanupEvents()
    {
        if (!_closed || _activeOperations != 0 || _resourcesDisposed) return;
        _resourcesDisposed = true;
        foreach (var signal in _sendEvents.Values) signal.Dispose();
        _sendEvents.Dispose();
    }

    public void ReceiveLoop(PacketReceiver receiver)
    {
        BeginOperation();
        try
        {
            byte[] buffer = new byte[256 * 1024];
            PacketAddress[] addresses = new PacketAddress[64];
            using var completion = new ManualResetEvent(false);
            fixed (byte* packet = buffer)
            fixed (PacketAddress* metadata = addresses)
            {
                while (true)
                {
                    completion.Reset();
                    NativeOverlapped operation = new() { EventHandle = completion.SafeWaitHandle.DangerousGetHandle() };
                    uint received = 0, addressBytes = (uint)(addresses.Length * sizeof(PacketAddress));
                    bool success;
                    int error;
                    nint handle;
                    lock (_gate)
                    {
                        if (_closed) return;
                        handle = _handle;
                        success = Native.RecvEx(handle, packet, (uint)buffer.Length,
                            &received, 0, metadata, &addressBytes, &operation);
                        error = success ? 0 : Marshal.GetLastPInvokeError();
                    }
                    if (!success && error == ErrorIoPending)
                    {
                        // Pinning stays in force until the kernel completes/cancels the request.
                        completion.WaitOne();
                        lock (_gate)
                        {
                            if (_closed) return;
                            success = Native.GetOverlappedResult(handle, &operation, &received, false);
                            error = success ? 0 : Marshal.GetLastPInvokeError();
                        }
                    }
                    if (!success)
                    {
                        lock (_gate)
                            if (_closed || (_stopped && error is ErrorNoData or ErrorAborted)) return;
                        throw Explain(error, "receiving packets");
                    }
                    int count = checked((int)addressBytes / sizeof(PacketAddress));
                    int offset = 0;
                    for (int index = 0; index < count; index++)
                    {
                        var remaining = buffer.AsSpan(offset, checked((int)received) - offset);
                        if (!PacketParser.TryGetLength(remaining, out int length))
                            throw new InvalidDataException("WinDivert returned an invalid packet batch.");
                        receiver(remaining[..length], addresses[index]);
                        offset += length;
                    }
                    if (offset != received)
                        throw new InvalidDataException("WinDivert packet/address batch lengths disagree.");
                }
            }
        }
        finally { EndOperation(); }
    }

    public void Send(ReadOnlySpan<byte> bytes, PacketAddress address)
    {
        BeginOperation();
        try
        {
            ManualResetEvent completion = _sendEvents.Value!;
            completion.Reset();
            NativeOverlapped operation = new() { EventHandle = completion.SafeWaitHandle.DangerousGetHandle() };
            uint sent = 0;
            fixed (byte* packet = bytes)
            {
                nint handle;
                bool success;
                int error;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_closed, this);
                    handle = _handle;
                    // Recalculate only when offload metadata reports missing checksums.
                    PacketInfo info = PacketParser.Parse(bytes, address.Outbound);
                    bool needsChecksum = (info.AddressLength == 4 && (address.Flags & (1u << 21)) == 0) ||
                        (!info.Fragment && info.Protocol == 6 && (address.Flags & (1u << 22)) == 0) ||
                        (!info.Fragment && info.Protocol == 17 && (address.Flags & (1u << 23)) == 0);
                    if (needsChecksum && !Native.CalcChecksums(packet, (uint)bytes.Length, &address, 0))
                        throw new InvalidDataException("Could not repair offloaded packet checksums.");
                    success = Native.SendEx(handle, packet, (uint)bytes.Length,
                        &sent, 0, &address, (uint)sizeof(PacketAddress), &operation);
                    error = success ? 0 : Marshal.GetLastPInvokeError();
                }
                if (!success && error == ErrorIoPending)
                {
                    if (!completion.WaitOne(250))
                    {
                        // Make future traffic pass immediately. Retain the pinned packet until cancellation completes.
                        Dispose();
                        completion.WaitOne();
                        throw new TimeoutException("Packet injection stalled; shaping has been stopped.");
                    }
                    lock (_gate)
                    {
                        ObjectDisposedException.ThrowIf(_closed, this);
                        success = Native.GetOverlappedResult(handle, &operation, &sent, false);
                        error = success ? 0 : Marshal.GetLastPInvokeError();
                    }
                }
                if (!success) throw Explain(error, "reinjecting packets");
                if (sent != bytes.Length) throw new IOException("WinDivert reported a partial packet injection.");
            }
        }
        finally { EndOperation(); }
    }

    public void StopReceiving()
    {
        lock (_gate)
        {
            if (_closed || _stopped) return;
            _stopped = true;
            if (!Native.Shutdown(_handle, 1))
                throw Explain(Marshal.GetLastPInvokeError(), "stopping packet diversion");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            Native.CancelIoEx(_handle, null);
            Native.Close(_handle);
            _handle = 0;
            CleanupEvents();
        }
    }

    private static Exception Explain(int error, string operation)
    {
        string hint = error switch
        {
            2 => "The bundled WinDivert DLL/driver files are missing.",
            5 => "Administrator privileges are required.",
            87 => "The packet filter or driver parameters were rejected.",
            577 => "Windows rejected the driver's signature.",
            654 => "Another application loaded an incompatible WinDivert version.",
            1275 => "Windows or security software blocked the signed driver. Do not disable security protections.",
            1753 => "The Windows Base Filtering Engine service is unavailable.",
            _ => "Normal networking is restored when diversion is closed."
        };
        return new Win32Exception(error, $"Error {error} while {operation}. {hint}");
    }

    private static class Native
    {
        [DllImport("WinDivert.dll", EntryPoint = "WinDivertHelperCompileFilter", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CompileFilter([MarshalAs(UnmanagedType.LPStr)] string filter, int layer,
            nint compiled, uint length, out nint error, out uint position);
        [DllImport("WinDivert.dll", EntryPoint = "WinDivertOpen", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        public static extern nint Open([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);
        [DllImport("WinDivert.dll", EntryPoint = "WinDivertRecvEx", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RecvEx(nint handle, byte* packet, uint length, uint* received,
            ulong flags, PacketAddress* address, uint* addressLength, NativeOverlapped* operation);
        [DllImport("WinDivert.dll", EntryPoint = "WinDivertSendEx", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SendEx(nint handle, byte* packet, uint length, uint* sent,
            ulong flags, PacketAddress* address, uint addressLength, NativeOverlapped* operation);
        [DllImport("WinDivert.dll", EntryPoint = "WinDivertHelperCalcChecksums", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CalcChecksums(byte* packet, uint length, PacketAddress* address, ulong flags);
        [DllImport("WinDivert.dll", EntryPoint = "WinDivertShutdown", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Shutdown(nint handle, uint how);
        [DllImport("WinDivert.dll", EntryPoint = "WinDivertClose", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Close(nint handle);
        [DllImport("WinDivert.dll", EntryPoint = "WinDivertSetParam", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetParam(nint handle, uint parameter, ulong value);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetOverlappedResult(nint handle, NativeOverlapped* operation,
            uint* transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIoEx(nint handle, NativeOverlapped* operation);
    }
}
