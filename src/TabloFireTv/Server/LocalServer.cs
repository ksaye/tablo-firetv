using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TabloFireTv.Models;
using TabloFireTv.Services;

namespace TabloFireTv;

/// <summary>
/// The app's own back end, running inside the app on 127.0.0.1.
///
/// The interface is the tablo-web page (Resources/Raw/wwwroot) shown in a WebView, and it expects
/// the same /api endpoints tablo-web's server provides. Instead of a server on another machine,
/// this answers them here, talking straight to the Tablo. The one real difference is playback:
/// tablo-web transcodes, because no browser can decode the Tablo's MPEG-2/AC3; here nothing is
/// transcoded, because video never plays in the WebView at all — the Fire TV's native player
/// (see MainActivity) decodes the Tablo's stream in hardware.
///
/// Only this app can use it: it listens on loopback, and every request must carry a random
/// cookie that the app hands to its own WebView and player at start-up.
/// </summary>
public sealed class LocalServer
{
    public const string CookieName = "tablofiretv";
    private const string TabloUserAgent = "Tablo-FAST/1.7.0 (Mobile; iPhone; iOS 18.4)";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILogger _log;
    private readonly HttpListener _listener = new();
    private readonly HttpClient _device = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly ConcurrentDictionary<string, (string Url, DateTime Created)> _recordingPlaylists = new();

    public TabloSession Tablo { get; }
    /// <summary>Multi-view through a tablo-web server on the network, when there is one.</summary>
    public MultiViewLink MultiView { get; }
    public int Port { get; }
    public string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public LocalServer(ILoggerFactory loggers)
    {
        _log = loggers.CreateLogger("TabloFireTv.Server");
        Tablo = new TabloSession(loggers.CreateLogger<TabloSession>());
        MultiView = new MultiViewLink(loggers.CreateLogger("TabloFireTv.MultiView"));
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _device.DefaultRequestHeaders.UserAgent.ParseAdd(TabloUserAgent);
    }

    public void Start()
    {
        Tablo.Restore();
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
        _ = Task.Run(() => Tablo.WarmAsync(CancellationToken.None));
        _ = Task.Run(LookForMultiViewAsync);
        _log.LogInformation("Listening on {Url}", BaseUrl);
    }

    /// <summary>Keep looking for (and checking on) a tablo-web server that can do multi-view.</summary>
    private async Task LookForMultiViewAsync()
    {
        while (_listener.IsListening)
        {
            try { await MultiView.RefreshAsync(Tablo.CurrentCredentials, Tablo.Device?.ServerId); }
            catch { /* never fatal */ }
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    // ------------------------------------------------------------------------------ routing

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            if (request.Cookies[CookieName]?.Value != Secret)
            {
                await WriteAsync(response, 403, "text/plain", "Forbidden"u8.ToArray());
                return;
            }

            var path = request.Url!.AbsolutePath;
            var method = request.HttpMethod;

            if (path.StartsWith("/api/"))
                await ApiAsync(context, method, path);
            else if (path == "/login")
                await WriteTextAsync(response, 200, "text/html", LoginPages.LoginPage(Tablo.NeedsCredentials));
            else
                await StaticAsync(response, path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Method} {Path} failed", request.HttpMethod, request.Url?.AbsolutePath);
            try { await WriteJsonAsync(response, 502, new { error = ex.Message }); }
            catch { /* the client has gone */ }
        }
    }

    private async Task ApiAsync(HttpListenerContext context, string method, string path)
    {
        var request = context.Request;
        var response = context.Response;
        var query = request.QueryString;
        var refresh = query["refresh"] == "true";

        switch (method, path)
        {
            case ("GET", "/api/me"):
                await WriteJsonAsync(response, 200, new
                {
                    loginRequired = true,
                    signedIn = !Tablo.NeedsCredentials,
                    name = Tablo.AccountEmail,
                    device = Tablo.Device?.Name,
                    canSaveCredentials = true,
                    multiView = MultiView.Available,
                    credentialsSaved = CredentialStore.HasSaved
                });
                return;

            case ("POST", "/api/login"):
                await LoginAsync(context);
                return;

            case ("POST", "/api/signout"):
                // One TV, one account: signing out always forgets the saved sign-in.
                CredentialStore.Clear();
                Tablo.Disconnect();
                await WriteJsonAsync(response, 200, new { ok = true });
                return;

            case ("GET", "/api/status"):
            {
                StorageBox? storage = null;
                if (Tablo.Connected)
                {
                    try { storage = await Tablo.StorageAsync(); } catch { /* reported as zero */ }
                }
                await WriteJsonAsync(response, 200, new StatusDto(
                    Tablo.Connected, Tablo.State,
                    Tablo.Device?.Name, Tablo.Device?.Host,
                    Tablo.ServerInfo?.Model.Name, Tablo.ServerInfo?.Version,
                    Tablo.ServerInfo?.Model.Tuners ?? 0,
                    Tablo.GuideReady, Tablo.GuideProgress,
                    storage?.Info?.TotalBytes ?? 0, storage?.Info?.FreeBytes ?? 0,
                    0, Tablo.NeedsCredentials));
                return;
            }

            case ("GET", "/api/channels"):
                await WriteJsonAsync(response, 200, (await Tablo.ChannelsAsync(refresh)).Select(ChannelDto.From));
                return;

            case ("GET", "/api/recordings"):
                await WriteJsonAsync(response, 200, (await Tablo.RecordingsAsync(refresh)).Select(RecordingDto.From));
                return;

            case ("GET", "/api/guide"):
                await GuideAsync(response, query["start"], query["hours"], refresh);
                return;

            case ("GET", "/api/now"):
                await WriteJsonAsync(response, 200, await NowAsync());
                return;

            case ("POST", "/api/play"):
            {
                var body = await JsonSerializer.DeserializeAsync<PlayRequest>(request.InputStream, Json);
                if (body is null || string.IsNullOrWhiteSpace(body.Path))
                {
                    await WriteJsonAsync(response, 400, new { error = "No program was given." });
                    return;
                }
                try
                {
                    await WriteJsonAsync(response, 200, await PlayAsync(body.Path, body.Live, body.Position ?? 0, body.Duration ?? 0));
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Play failed for {Path}: {Message}", body.Path, ex.Message);
                    await WriteJsonAsync(response, 502, new { error = ex.Message });
                }
                return;
            }

            // Nothing to stop: there is no transcoder. The Tablo frees the tuner once the
            // player stops fetching the stream.
            case ("POST", _) when path.StartsWith("/api/stop/"):
                await WriteJsonAsync(response, 200, new { ok = true });
                return;

            // tablo-web features that need a server: nothing to offer here.
            case ("GET", "/api/update"):
                await WriteJsonAsync(response, 200, new { available = false });
                return;
            // A multi-view already running on the server (from a browser, say) is not rejoined: the
            // TV starts its own, which replaces it.
            case ("GET", "/api/mosaic"):
                await WriteJsonAsync(response, 200, new { running = false });
                return;

            case (_, _) when path.StartsWith("/api/mosaic"):
            {
                byte[]? body = null;
                if (request.HasEntityBody)
                {
                    using var buffer = new MemoryStream();
                    await request.InputStream.CopyToAsync(buffer);
                    body = buffer.ToArray();
                }
                var (status, type, bytes) = await MultiView.ForwardAsync(method, request.Url!.PathAndQuery, body);
                await WriteAsync(response, status, type, bytes);
                return;
            }
        }

        if (method == "GET" && path.StartsWith("/api/image/") && long.TryParse(path["/api/image/".Length..], out var imageId))
        {
            await ImageAsync(response, imageId);
            return;
        }

        if (method == "GET" && path.StartsWith("/api/direct/") && path.EndsWith(".m3u8"))
        {
            var id = path["/api/direct/".Length..^".m3u8".Length];
            var playlist = await RecordingPlaylistAsync(id);
            if (playlist is null) await WriteJsonAsync(response, 404, new { error = "Unknown playlist." });
            else await WriteTextAsync(response, 200, "application/vnd.apple.mpegurl", playlist);
            return;
        }

        await WriteJsonAsync(response, 404, new { error = "Not found." });
    }

    // ------------------------------------------------------------------------------ sign-in

    private async Task LoginAsync(HttpListenerContext context)
    {
        var request = await JsonSerializer.DeserializeAsync<LoginRequest>(context.Request.InputStream, Json);
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            await WriteJsonAsync(context.Response, 400, new { error = "Enter the email and password for your Tablo account." });
            return;
        }

        try
        {
            var credentials = new Credentials(request.Email.Trim(), request.Password, request.ServerId);
            var result = await Tablo.SignInAsync(credentials, CancellationToken.None);

            if (result.Devices is { Count: > 1 } && string.IsNullOrWhiteSpace(request.ServerId))
            {
                await WriteJsonAsync(context.Response, 200, new { chooseDevice = true, devices = result.Devices });
                return;
            }

            if (request.Remember != false)
                CredentialStore.Save(credentials with { ServerId = result.ServerId }, _log);

            await WriteJsonAsync(context.Response, 200, new { ok = true, device = result.DeviceName });
        }
        catch (Exception ex)
        {
            _log.LogWarning("Sign-in failed: {Message}", ex.Message);
            await WriteJsonAsync(context.Response, 401, new { error = ex.Message });
        }
    }

    // ------------------------------------------------------------------------------ content

    private async Task GuideAsync(HttpListenerResponse response, string? start, string? hours, bool refresh)
    {
        if (!Tablo.GuideReady && !refresh)
        {
            await WriteJsonAsync(response, 202, new { loading = true, progress = Tablo.GuideProgress ?? 0, state = Tablo.State });
            return;
        }

        var from = DateTime.TryParse(start, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed : DateTime.UtcNow;
        var span = double.TryParse(hours, System.Globalization.CultureInfo.InvariantCulture, out var h) && h is > 0 and <= 48 ? h : 4;
        var to = from.AddHours(span);

        var airings = await Tablo.GuideAsync(refresh);
        var window = airings
            .Select(AiringDto.From)
            .Where(a => a is not null && a.StartUtc < to && a.EndUtc > from)
            .Select(a => a!)
            .OrderBy(a => a.StartUtc)
            .ToList();

        await WriteJsonAsync(response, 200, new
        {
            loading = false,
            start = from,
            end = to,
            channels = (await Tablo.ChannelsAsync()).Select(ChannelDto.From).ToList(),
            airings = window
        });
    }

    /// <summary>What is on each channel right now, in Live TV order. Also used by channel up/down.</summary>
    public async Task<List<NowDto>> NowAsync()
    {
        var channels = (await Tablo.ChannelsAsync()).Select(ChannelDto.From).ToList();
        if (!Tablo.GuideReady)
            return channels.Select(c => new NowDto(c, null, 0)).ToList();

        var now = DateTime.UtcNow;
        // The session keeps this ready; working it out from the whole guide here cost a second or
        // more on a Fire TV Stick, on the channel-change key press.
        var onNow = await Tablo.NowByChannelAsync();

        return channels.Select(c =>
        {
            var airing = onNow.TryGetValue(c.Path, out var a) ? AiringDto.From(a) : null;
            var progress = airing is { DurationSeconds: > 0 }
                ? Math.Clamp((now - airing.StartUtc).TotalSeconds / airing.DurationSeconds, 0, 1)
                : 0;
            return new NowDto(c, airing, progress);
        }).ToList();
    }

    private async Task ImageAsync(HttpListenerResponse response, long id)
    {
        if (!Tablo.Connected)
        {
            await WriteJsonAsync(response, 404, new { error = "Not connected." });
            return;
        }
        var url = (await Tablo.ClientAsync()).SnapshotUrl(id);
        using var res = await _device.GetAsync(url);
        if (!res.IsSuccessStatusCode)
        {
            await WriteJsonAsync(response, 404, new { error = "No image." });
            return;
        }
        var bytes = await res.Content.ReadAsByteArrayAsync();
        response.Headers["Cache-Control"] = "max-age=86400";
        await WriteAsync(response, 200, res.Content.Headers.ContentType?.MediaType ?? "image/jpeg", bytes);
    }

    private async Task StaticAsync(HttpListenerResponse response, string path)
    {
        if (path == "/") path = "/index.html";
        if (path.Contains("..")) { await WriteAsync(response, 404, "text/plain", []); return; }

        byte[] bytes;
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync("wwwroot" + path);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or Java.IO.FileNotFoundException)
        {
            await WriteAsync(response, 404, "text/plain", "Not found"u8.ToArray());
            return;
        }

        var type = Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            _ => "application/octet-stream"
        };
        await WriteAsync(response, 200, type, bytes);
    }

    // ------------------------------------------------------------------------------ playback

    /// <summary>
    /// Start playing something and say where the native player should fetch it from. Nothing is
    /// transcoded: a live channel is the Tablo's own playlist, a recording a rewritten copy of it
    /// (see <see cref="RecordingPlaylistAsync"/>), and a free streaming channel its CDN playlist.
    /// </summary>
    public async Task<PlayDto> PlayAsync(string path, bool live, double position, int duration)
    {
        if (TabloClient.IsFast(path))
        {
            var fast = await Tablo.WithRetryAsync(c => c.WatchAsync(path));
            if (string.IsNullOrWhiteSpace(fast?.PlaylistUrl))
                throw new InvalidOperationException("That channel has no stream.");
            return new PlayDto("", fast!.PlaylistUrl!, true, 0, 0);
        }

        var watch = await TuneAsync(path);
        if (live) return new PlayDto("", watch.PlaylistUrl!, true, 0, 0);

        var now = DateTime.UtcNow;
        foreach (var (key, entry) in _recordingPlaylists)
            if (now - entry.Created > TimeSpan.FromHours(12)) _recordingPlaylists.TryRemove(key, out _);
        var id = Guid.NewGuid().ToString("n")[..12];
        _recordingPlaylists[id] = (watch.PlaylistUrl!, now);
        return new PlayDto("", $"/api/direct/{id}.m3u8", false, position, duration);
    }

    /// <summary>
    /// Ask the Tablo to start streaming, retrying a quick "no signal lock". Tuning is not
    /// deterministic: a station that plays perfectly can refuse once and then work a second later.
    /// A refusal that took the tuner a long time is genuine, and is not worth waiting out again.
    /// </summary>
    private async Task<WatchResponse> TuneAsync(string path)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var started = DateTime.UtcNow;
            try
            {
                var watch = await Tablo.WithRetryAsync(c => c.WatchAsync(path))
                            ?? throw new InvalidOperationException("The Tablo did not answer the play request.");
                if (string.IsNullOrWhiteSpace(watch.PlaylistUrl))
                    throw new InvalidOperationException("The Tablo returned no playlist for this program.");
                return watch;
            }
            catch (TabloHttpException ex) when (ex.Status == 503 && ex.Message.Contains("no_signal_lock", StringComparison.OrdinalIgnoreCase))
            {
                if (attempt >= attempts || DateTime.UtcNow - started > TimeSpan.FromSeconds(15))
                    throw new InvalidOperationException(
                        "The Tablo could not get a signal on this channel. That is reception at the antenna.");
                await Task.Delay(1500);
            }
        }
    }

    /// <summary>
    /// A recording's playlist, made seekable. The Tablo serves a finished recording as a live-style
    /// playlist with no #EXT-X-ENDLIST, so a player treats it as live, starts at the very end and
    /// refuses to seek. This copy is marked VOD, with every segment URI made absolute so the video
    /// itself still comes straight from the Tablo — only the playlist passes through here.
    /// </summary>
    private async Task<string?> RecordingPlaylistAsync(string id)
    {
        if (!_recordingPlaylists.TryGetValue(id, out var source)) return null;

        var url = new Uri(source.Url);
        var text = await _device.GetStringAsync(url);

        // The watch URL is a master playlist with a single variant; follow it to the media playlist.
        if (text.Contains("#EXT-X-STREAM-INF"))
        {
            var variant = Lines(text).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'))
                          ?? throw new InvalidOperationException("The Tablo's playlist names no stream.");
            url = new Uri(url, variant);
            text = await _device.GetStringAsync(url);
        }

        var output = new StringBuilder();
        var typed = text.Contains("#EXT-X-PLAYLIST-TYPE");
        foreach (var line in Lines(text))
        {
            output.Append(line.Length > 0 && !line.StartsWith('#') ? new Uri(url, line).AbsoluteUri : line).Append('\n');
            if (!typed && line.StartsWith("#EXTM3U")) output.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        }
        if (!text.Contains("#EXT-X-ENDLIST")) output.Append("#EXT-X-ENDLIST\n");
        return output.ToString();

        static IEnumerable<string> Lines(string t) => t.Split('\n').Select(l => l.TrimEnd('\r'));
    }

    // ------------------------------------------------------------------------------ plumbing

    private static Task WriteJsonAsync(HttpListenerResponse response, int status, object value) =>
        WriteAsync(response, status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private static Task WriteTextAsync(HttpListenerResponse response, int status, string type, string text) =>
        WriteAsync(response, status, type, Encoding.UTF8.GetBytes(text));

    private static async Task WriteAsync(HttpListenerResponse response, int status, string type, byte[] body)
    {
        response.StatusCode = status;
        response.ContentType = type;
        response.ContentLength64 = body.Length;
        if (body.Length > 0) await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    private sealed record PlayRequest(string Path, bool Live, double? Position, int? Duration);
    private sealed record LoginRequest(string Email, string Password, string? ServerId, bool? Remember);
}
