using DownloadLimit.Core;

namespace DownloadLimit.Windows;

public sealed class SpeedMonitor : IAsyncDisposable
{
    private readonly IPacketTransport _transport;
    private readonly Task _receiver;
    private readonly NetworkClassifier _classifier;
    private volatile bool _stopping;
    private long _download, _upload;
    private Exception? _error;
    public Exception? Error => Volatile.Read(ref _error);
    public (long Download, long Upload) Totals =>
        (Interlocked.Read(ref _download), Interlocked.Read(ref _upload));

    public SpeedMonitor(NetworkClassifier classifier)
        : this(classifier, new WinDivertTransport(classifier.BuildFilter(false), sniff: true)) { }

    internal SpeedMonitor(NetworkClassifier classifier, IPacketTransport transport)
    {
        _classifier = classifier;
        _transport = transport;
        try
        {
            _receiver = Task.Factory.StartNew(Receive, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { _transport.Dispose(); throw; }
    }

    private void Receive()
    {
        try
        {
            _transport.ReceiveLoop((ReadOnlySpan<byte> bytes, PacketAddress address) =>
            {
                PacketInfo info = PacketParser.Parse(bytes, address.Outbound);
                if (info.Class == PacketClass.Invalid ||
                    !_classifier.IsInternet(bytes.Slice(info.AddressOffset, info.AddressLength))) return;
                if (address.Outbound) Interlocked.Add(ref _upload, bytes.Length);
                else Interlocked.Add(ref _download, bytes.Length);
            });
            if (!_stopping)
                Volatile.Write(ref _error, new IOException("Download speed monitoring stopped unexpectedly. Enable again to retry."));
        }
        catch (Exception ex) { if (!_stopping) Volatile.Write(ref _error, ex); }
        finally { _transport.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        try { _transport.StopReceiving(); } catch { }
        try { await Task.WhenAny(_receiver, Task.Delay(250)).ConfigureAwait(false); }
        finally { _transport.Dispose(); }
        // Closing cancels a pending native receive. Allow its pinned memory and
        // completion event to be released before a replacement monitor starts.
        await Task.WhenAny(_receiver, Task.Delay(250)).ConfigureAwait(false);
    }
}
