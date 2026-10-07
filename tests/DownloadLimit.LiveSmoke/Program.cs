using System.Diagnostics;
using System.Net;
using System.Text.Json;

// Explicit opt-in: generates real internet traffic. Does not change shaping settings,
// disable protection, register startup tasks, or reboot/sleep the computer.
if (args.Length != 1 || args[0] != "--live")
{
    Console.Error.WriteLine("Run with --live only when internet traffic generation is authorized.");
    return 2;
}
const string endpoint = "https://speed.cloudflare.com";
using var client = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 12, AutomaticDecompression = DecompressionMethods.None })
{ Timeout = TimeSpan.FromSeconds(35) };
var app = Process.GetProcessesByName("DownloadLimit").FirstOrDefault();
var results = new List<object>();
int totalErrors = 0;
var idle = await Latencies(5, CancellationToken.None);
Console.WriteLine($"Idle HTTPS request latency: median {Median(idle):F1} ms (includes HTTP processing; not game latency).");
await Phase("download", 4, 0);
await Phase("upload", 0, 2);
await Phase("both directions", 4, 2);
string resultPath = Path.GetFullPath("artifacts/live-smoke-results.json");
Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
File.WriteAllText(resultPath, JsonSerializer.Serialize(new
{
    TimestampUtc = DateTime.UtcNow,
    Endpoint = endpoint,
    CapsChangedByTest = false,
    IdleHttpsLatencyMs = idle,
    Results = results,
    Limitations = "Parallel connections from one application only. Payload averages include setup/tail. No unrestricted baseline, strict-cap proof, game/UDP latency, VPN switching, Windows 10, sleep/resume, or live shutdown test."
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Saved {resultPath}");
return totalErrors == 0 ? 0 : 1;

async Task Phase(string name, int downloads, int uploads)
{
    const int downloadBytes = 50_000_000, uploadBytes = 25_000_000;
    long downloaded = 0, uploadBodyWritten = 0, peakWorkingSet = 0;
    long downloadFinishedTicks = 0, uploadFinishedTicks = 0;
    int completedDownloads = 0, completedUploads = 0;
    var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var stopSamples = new CancellationTokenSource();
    var latencies = new List<double>();
    TimeSpan cpuStart = app?.TotalProcessorTime ?? TimeSpan.Zero;
    var stopwatch = Stopwatch.StartNew();
    Console.WriteLine($"Starting {name}: {downloads} download and {uploads} upload connections, 30-second maximum.");
    var sampler = Task.Run(async () =>
    {
        while (!stopSamples.IsCancellationRequested)
        {
            try
            {
                if (app is not null) { app.Refresh(); peakWorkingSet = Math.Max(peakWorkingSet, app.WorkingSet64); }
                latencies.AddRange(await Latencies(1, stopSamples.Token));
                await Task.Delay(250, stopSamples.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception error) { errors.Enqueue("sample: " + error.Message); }
        }
    });
    var transfers = Enumerable.Range(0, downloads).Select(async _ =>
    {
        try
        {
            using var response = await client.GetAsync($"{endpoint}/__down?bytes={downloadBytes}&nonce={Guid.NewGuid():N}", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] buffer = new byte[64 * 1024];
            int read; long total = 0;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            { total += read; Interlocked.Add(ref downloaded, read); }
            if (total != downloadBytes) throw new IOException($"Short download: {total}/{downloadBytes}");
            if (Interlocked.Increment(ref completedDownloads) == downloads)
                Interlocked.Exchange(ref downloadFinishedTicks, stopwatch.ElapsedTicks);
        }
        catch (Exception error) { errors.Enqueue("download: " + error.Message); }
    }).Concat(Enumerable.Range(0, uploads).Select(async _ =>
    {
        try
        {
            using var body = new GeneratedBody(uploadBytes, count => Interlocked.Add(ref uploadBodyWritten, count));
            using var response = await client.PostAsync($"{endpoint}/__up", body, timeout.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if (Interlocked.Increment(ref completedUploads) == uploads)
                Interlocked.Exchange(ref uploadFinishedTicks, stopwatch.ElapsedTicks);
        }
        catch (Exception error) { errors.Enqueue("upload: " + error.Message); }
    })).ToArray();
    await Task.WhenAll(transfers);
    stopwatch.Stop();
    stopSamples.Cancel();
    await sampler;
    double seconds = stopwatch.Elapsed.TotalSeconds;
    double downloadSeconds = downloadFinishedTicks > 0 ? downloadFinishedTicks / (double)Stopwatch.Frequency : seconds;
    double uploadSeconds = uploadFinishedTicks > 0 ? uploadFinishedTicks / (double)Stopwatch.Frequency : seconds;
    double downMbps = downloaded * 8 / downloadSeconds / 1_000_000;
    // Bytes written to the HTTP transport are not proof of delivery if the request failed.
    double? uploadMbps = completedUploads == uploads && uploads > 0 ? uploadBodyWritten * 8 / uploadSeconds / 1_000_000 : null;
    TimeSpan cpu = (app?.TotalProcessorTime ?? TimeSpan.Zero) - cpuStart;
    totalErrors += errors.Count;
    var result = new { Phase = name, Seconds = seconds,
        DownloadSeconds = downloads > 0 ? (double?)downloadSeconds : null,
        UploadSeconds = uploads > 0 ? (double?)uploadSeconds : null, DownloadBytes = downloaded,
        UploadBodyBytesWritten = uploadBodyWritten, DownloadPayloadMbps = downMbps,
        UploadPayloadMbps = uploadMbps, CompletedDownloads = completedDownloads,
        CompletedUploads = completedUploads, Errors = errors.ToArray(),
        PeakAppWorkingSetBytes = peakWorkingSet, AppCpuSeconds = cpu.TotalSeconds,
        LoadedHttpsLatencyMs = latencies.ToArray() };
    results.Add(result);
    Console.WriteLine($"{name}: download {downMbps:F1} Mbps; upload {(uploadMbps.HasValue ? uploadMbps.Value.ToString("F1") : "unconfirmed")} Mbps; completed {completedDownloads}/{downloads} down, {completedUploads}/{uploads} up; app peak {peakWorkingSet / 1048576.0:F1} MiB; HTTPS median {Median(latencies):F1} ms; errors {errors.Count}.");
}

async Task<List<double>> Latencies(int count, CancellationToken cancellation)
{
    var times = new List<double>();
    for (int i = 0; i < count; i++)
    {
        var watch = Stopwatch.StartNew();
        using var response = await client.GetAsync($"{endpoint}/__down?bytes=0&nonce={Guid.NewGuid():N}", cancellation);
        response.EnsureSuccessStatusCode();
        times.Add(watch.Elapsed.TotalMilliseconds);
    }
    return times;
}
static double Median(IEnumerable<double> values)
{
    double[] ordered = values.Order().ToArray();
    return ordered.Length == 0 ? double.NaN : ordered[ordered.Length / 2];
}

sealed class GeneratedBody(int length, Action<int> written) : HttpContent
{
    protected override bool TryComputeLength(out long result) { result = length; return true; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Write(stream, CancellationToken.None);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellation) => Write(stream, cancellation);
    private async Task Write(Stream stream, CancellationToken cancellation)
    {
        byte[] zeros = new byte[64 * 1024];
        for (int remaining = length; remaining > 0;)
        {
            int count = Math.Min(remaining, zeros.Length);
            await stream.WriteAsync(zeros.AsMemory(0, count), cancellation);
            written(count); remaining -= count;
        }
    }
}
