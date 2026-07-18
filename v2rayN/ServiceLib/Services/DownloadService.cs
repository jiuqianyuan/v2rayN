using System.Net.Http.Headers;

namespace ServiceLib.Services;

/// <summary>
/// Download
/// </summary>
public class DownloadService
{
    public event EventHandler<UpdateResult>? UpdateCompleted;

    public event ErrorEventHandler? Error;

    private static readonly string _tag = "DownloadService";

    /// <summary>
    /// Downloads data with the specified proxy and reports progress messages.
    /// </summary>
    public async Task<int> DownloadDataAsync(string url, IWebProxy webProxy, int downloadTimeout, Func<bool, string, Task> updateFunc)
    {
        try
        {
            var progress = new Progress<string>();
            progress.ProgressChanged += (sender, value) => updateFunc?.Invoke(false, $"{value}");

            await DownloaderHelper.Instance.DownloadDataAsync4Speed(webProxy,
                  url,
                  progress,
                  downloadTimeout);
        }
        catch (Exception ex)
        {
            await updateFunc?.Invoke(false, ex.Message);
            if (ex.InnerException != null)
            {
                await updateFunc?.Invoke(false, ex.InnerException.Message);
            }
        }
        return 0;
    }

    /// <summary>
    /// Downloads a file and reports progress through events.
    /// </summary>
    public async Task DownloadFileAsync(string url, string fileName, bool blProxy, int downloadTimeout)
    {
        try
        {
            UpdateCompleted?.Invoke(this, new UpdateResult(false, $"{ResUI.Downloading}   {url}"));

            var progress = new Progress<double>();
            progress.ProgressChanged += (sender, value) => UpdateCompleted?.Invoke(this, new UpdateResult(value > 100, $"...{value}%"));

            var webProxy = await GetWebProxy(blProxy);
            await DownloaderHelper.Instance.DownloadFileAsync(webProxy,
                url,
                fileName,
                progress,
                downloadTimeout);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);

            Error?.Invoke(this, new ErrorEventArgs(ex));
            if (ex.InnerException != null)
            {
                Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
            }
        }
    }

    /// <summary>
    /// Gets redirect target URL without following redirects automatically.
    /// </summary>
    public async Task<string?> UrlRedirectAsync(string url, bool blProxy)
    {
        var webRequestHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            Proxy = await GetWebProxy(blProxy)
        };
        var client = new HttpClient(webRequestHandler);

        var response = await client.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location is not null)
        {
            return response.Headers.Location.ToString();
        }
        else
        {
            Error?.Invoke(this, new ErrorEventArgs(new Exception("StatusCode error: " + response.StatusCode)));
            Logging.SaveLog("StatusCode error: " + url);
            return null;
        }
    }

    /// <summary>
    /// Tries to download string content using proxy switch setting.
    /// </summary>
    public async Task<string?> TryDownloadString(string url, bool blProxy, string userAgent)
    {
        var webProxy = await GetWebProxy(blProxy);
        return await TryDownloadString(url, webProxy, userAgent);
    }

    /// <summary>
    /// Tries to download string content with a specified proxy.
    /// </summary>
    public async Task<string?> TryDownloadString(string url, IWebProxy? webProxy, string userAgent)
    {
        try
        {
            var result1 = await DownloadStringAsync(url, webProxy, userAgent, 15);
            if (result1.IsNotEmpty())
            {
                return result1;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            Error?.Invoke(this, new ErrorEventArgs(ex));
            if (ex.InnerException != null)
            {
                Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
            }
        }

        try
        {
            var result2 = await DownloadStringViaDownloader(url, webProxy, userAgent, 15);
            if (result2.IsNotEmpty())
            {
                return result2;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            Error?.Invoke(this, new ErrorEventArgs(ex));
            if (ex.InnerException != null)
            {
                Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads string content via HttpClient, with RFC 8305 (Happy Eyeballs) connect strategy
    /// to avoid hanging on a blocked/blackholed IPv6 route.
    /// </summary>
    private async Task<string?> DownloadStringAsync(string url, IWebProxy? webProxy, string userAgent, int timeout)
    {
        try
        {
            var perAttemptTimeout = TimeSpan.FromSeconds(Math.Max(2, Math.Min(5, timeout / 2)));

            var webRequestHandler = new SocketsHttpHandler
            {
                Proxy = webProxy,
                UseProxy = webProxy != null,
                ConnectTimeout = TimeSpan.FromSeconds(timeout),
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = await HappyEyeballsConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, perAttemptTimeout, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            };

            using var client = new HttpClient(webRequestHandler);

            if (userAgent.IsNullOrEmpty())
            {
                userAgent = Utils.GetVersion(false);
            }
            client.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent);

            Uri uri = new(url);
            //Authorization Header
            if (uri.UserInfo.IsNotEmpty())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Utils.Base64Encode(uri.UserInfo));
            }

            using var cts = new CancellationTokenSource();
            var result = await client.GetStringAsync(url, cts.Token).WaitAsync(TimeSpan.FromSeconds(timeout), cts.Token);
            return result;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            Error?.Invoke(this, new ErrorEventArgs(ex));
            if (ex.InnerException != null)
            {
                Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
            }
        }
        return null;
    }

    /// <summary>
    /// RFC 8305 Happy Eyeballs: race IPv6/IPv4 connection attempts concurrently.
    /// Each individual attempt has a hard timeout so a blackholed address family
    /// (e.g. IPv6 silently dropped by firewall/ISP) can never stall the whole request.
    /// </summary>
    private static async Task<Socket> HappyEyeballsConnectAsync(string host, int port, TimeSpan perAttemptTimeout, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var ipv6 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToList();
        var ipv4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = new List<(Task<Socket?> Task, CancellationTokenSource Cts)>();

        void StartFamily(List<IPAddress> family)
        {
            foreach (var ip in family)
            {
                var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(raceCts.Token);
                attemptCts.CancelAfter(perAttemptTimeout); // hard per-address cap, this is what fixes the 30s hang
                attempts.Add((ConnectSingleAsync(ip, port, attemptCts.Token), attemptCts));
            }
        }

        try
        {
            // Prefer IPv6 first per RFC 8305, but never let it block indefinitely.
            var primary = ipv6.Count > 0 ? ipv6 : ipv4;
            var secondary = ipv6.Count > 0 ? ipv4 : ipv6;

            StartFamily(primary);

            if (secondary.Count > 0)
            {
                var delayTask = Task.Delay(TimeSpan.FromMilliseconds(250), raceCts.Token);
                var winner = await RaceForWinnerAsync(attempts, delayTask).ConfigureAwait(false);
                if (winner != null)
                {
                    return winner;
                }
                // Primary family didn't succeed within the short window (still may be pending/blackholed) -> start secondary now.
                StartFamily(secondary);
            }

            while (attempts.Count > 0)
            {
                var finishedTask = await Task.WhenAny(attempts.Select(a => (Task)a.Task)).ConfigureAwait(false);
                var finished = attempts.First(a => (Task)a.Task == finishedTask);
                attempts.Remove(finished);

                var socket = await finished.Task.ConfigureAwait(false);
                finished.Cts.Dispose();
                if (socket != null)
                {
                    return socket;
                }
            }

            throw new SocketException((int)SocketError.TimedOut);
        }
        finally
        {
            raceCts.Cancel(); // cancel every still-running attempt (including a stuck IPv6 handshake) once we have a winner
            foreach (var (_, attemptCts) in attempts)
            {
                attemptCts.Dispose();
            }
        }
    }

    private static async Task<Socket?> RaceForWinnerAsync(List<(Task<Socket?> Task, CancellationTokenSource Cts)> attempts, Task delayTask)
    {
        var pending = attempts.Select(a => (Task)a.Task).Append(delayTask).ToList();
        while (pending.Count > 1)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            if (completed == delayTask)
            {
                return null; // window elapsed, let the caller start the other family
            }

            pending.Remove(completed);
            var attempt = attempts.First(a => (Task)a.Task == completed);
            var socket = await attempt.Task.ConfigureAwait(false);
            if (socket != null)
            {
                return socket;
            }
            // fast-failed (e.g. immediate ICMP unreachable/RST) - keep waiting within the window for siblings
        }
        return null;
    }

    private static async Task<Socket?> ConnectSingleAsync(IPAddress ip, int port, CancellationToken token)
    {
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Downloads string content via DownloaderHelper.
    /// </summary>
    private async Task<string?> DownloadStringViaDownloader(string url, IWebProxy? webProxy, string userAgent, int timeout)
    {
        try
        {
            if (userAgent.IsNullOrEmpty())
            {
                userAgent = Utils.GetVersion(false);
            }
            var result = await DownloaderHelper.Instance.DownloadStringAsync(webProxy, url, userAgent, timeout);
            return result;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            Error?.Invoke(this, new ErrorEventArgs(ex));
            if (ex.InnerException != null)
            {
                Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
            }
        }
        return null;
    }

    /// <summary>
    /// Creates local SOCKS proxy when proxy switch is enabled.
    /// </summary>
    private async Task<WebProxy?> GetWebProxy(bool blProxy)
    {
        if (!blProxy)
        {
            return null;
        }
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        if (await SocketCheck(Global.Loopback, port) == false)
        {
            return null;
        }

        return new WebProxy($"socks5://{Global.Loopback}:{port}");
    }

    /// <summary>
    /// Checks whether the specified TCP endpoint is reachable.
    /// </summary>
    private async Task<bool> SocketCheck(string ip, int port)
    {
        try
        {
            IPEndPoint point = new(IPAddress.Parse(ip), port);
            using Socket? sock = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await sock.ConnectAsync(point);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
