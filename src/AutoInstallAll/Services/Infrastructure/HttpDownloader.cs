using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AutoInstallAll.Services.Infrastructure;

/// <summary>Thông tin HTTP Header của file trên máy chủ (dùng cho Evergreen URL).</summary>
public sealed record RemoteFileInfo(
    Uri FinalUri,
    string? ETag,
    DateTimeOffset? LastModified,
    long? ContentLength,
    string? FileName,
    string? ContentType);

public sealed record DownloadResult(string Path, long Bytes, string Sha256);

/// <summary>Tải file với tiến độ %, tính SHA-256 trong lúc tải (không phải đọc lại file trên USB).</summary>
public sealed class HttpDownloader : IDisposable
{
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http;

    public HttpDownloader(string userAgent)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(userAgent) ? "AutoInstallAll/1.0" : userAgent);
    }

    public HttpClient Client => _http;

    /// <summary>HEAD (fallback GET Range 0-0 nếu máy chủ không hỗ trợ HEAD) để đọc ETag / Last-Modified / Content-Length.</summary>
    public async Task<RemoteFileInfo> GetRemoteInfoAsync(string url, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(HeaderTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            using (var head = new HttpRequestMessage(HttpMethod.Head, url))
            using (var resp = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false))
            {
                if (resp.IsSuccessStatusCode)
                    return ToInfo(resp, resp.Content.Headers.ContentLength);
            }

            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Range = new RangeHeaderValue(0, 0);
            using var resp2 = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            resp2.EnsureSuccessStatusCode();
            long? length = resp2.StatusCode == HttpStatusCode.PartialContent
                ? resp2.Content.Headers.ContentRange?.Length
                : resp2.Content.Headers.ContentLength;
            return ToInfo(resp2, length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Máy chủ không phản hồi header sau {HeaderTimeout.TotalSeconds:0}s: {url}");
        }
    }

    public async Task<DownloadResult> DownloadAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linked.Token;
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             1024 * 1024, FileOptions.Asynchronous))
            {
                var lastReport = Stopwatch.StartNew();
                int read;
                while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    received += read;
                    if (total is > 0 && lastReport.ElapsedMilliseconds >= 200)
                    {
                        progress?.Report(Math.Min(99.9, received * 100.0 / total.Value));
                        lastReport.Restart();
                    }
                }
                await dst.FlushAsync(token).ConfigureAwait(false);
            }

            if (total.HasValue && received != total.Value)
                throw new IOException($"Tải chưa đủ dung lượng: {received:N0}/{total.Value:N0} byte.");

            progress?.Report(100);
            return new DownloadResult(destinationPath, received, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch (Exception ex)
        {
            TryDelete(destinationPath);
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                throw new TimeoutException($"Quá thời gian tải {timeout.TotalMinutes:0} phút: {url}", ex);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        using var timeoutCts = new CancellationTokenSource(HeaderTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
            req.Headers.TryAddWithoutValidation(name, value);
        using var resp = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();

    private static RemoteFileInfo ToInfo(HttpResponseMessage resp, long? length)
    {
        var finalUri = resp.RequestMessage?.RequestUri ?? throw new InvalidOperationException("Thiếu RequestUri.");
        var cd = resp.Content.Headers.ContentDisposition;
        var name = (cd?.FileNameStar ?? cd?.FileName)?.Trim('"', ' ');
        if (string.IsNullOrWhiteSpace(name))
            name = Uri.UnescapeDataString(Path.GetFileName(finalUri.AbsolutePath));
        return new RemoteFileInfo(
            finalUri,
            resp.Headers.ETag?.Tag,
            resp.Content.Headers.LastModified,
            length,
            string.IsNullOrWhiteSpace(name) ? null : name,
            resp.Content.Headers.ContentType?.MediaType);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
