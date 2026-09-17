using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TabloFireTv;

/// <summary>
/// Multi-view, borrowed from a tablo-web server on the same network.
///
/// A Fire TV cannot decode several broadcast channels at once, but tablo-web can combine them into a
/// single stream it can play. So the app looks for one: it broadcasts tablo-web's discovery query
/// (UDP 8788), and if a server answers that is connected to the same Tablo, signs in to it with
/// the same Tablo account and offers multi-view. Without one, multi-view simply does not appear.
/// </summary>
public sealed class MultiViewLink(ILogger log)
{
    public const int DiscoveryPort = 8788;
    private const string Query = "TABLOWEB_DISCOVER 1";

    private readonly CookieContainer _cookies = new();
    private HttpClient? _http;
    private Uri? _base;
    private DateTime _lastSearchUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The tablo-web server in use, or null when none has been found.</summary>
    public Uri? BaseUrl => _base;
    public bool Available => _base is not null;

    /// <summary>The Cookie header the native player needs to fetch the multi-view stream.</summary>
    public string CookieHeader => _base is null ? "" : _cookies.GetCookieHeader(_base);

    private sealed record Announcement(string? Service, string? Version, int Port, string? ServerId,
        bool Connected, bool LoginRequired, string[]? Features);

    /// <summary>
    /// Look for a server (at most once a minute), and keep the one found as long as it answers.
    /// <paramref name="serverId"/> is the Tablo this app is connected to; a server connected to a
    /// different one is ignored.
    /// </summary>
    public async Task RefreshAsync(Credentials? credentials, string? serverId)
    {
        if (credentials is null || string.IsNullOrEmpty(serverId)) return;
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (_base is not null && await StillThereAsync()) return;
            if (DateTime.UtcNow - _lastSearchUtc < TimeSpan.FromMinutes(1)) return;
            _lastSearchUtc = DateTime.UtcNow;
            Forget();

            foreach (var (address, found) in await SearchAsync())
            {
                if (found.Service != "tablo-web" || found.ServerId != serverId ||
                    !(found.Features ?? []).Contains("multiview")) continue;

                var candidate = new Uri($"http://{address}:{found.Port}/");
                if (await SignInAsync(candidate, credentials, found.LoginRequired))
                {
                    log.LogInformation("Multi-view available through tablo-web {Version} at {Url}", found.Version, candidate);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug("Looking for tablo-web failed: {Message}", ex.Message);
        }
        finally { _gate.Release(); }
    }

    private static async Task<List<(IPAddress, Announcement)>> SearchAsync()
    {
        var found = new List<(IPAddress, Announcement)>();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var query = Encoding.UTF8.GetBytes(Query);
        await udp.SendAsync(query, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (true)
            {
                var reply = await udp.ReceiveAsync(window.Token);
                try
                {
                    var note = JsonSerializer.Deserialize<Announcement>(reply.Buffer,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (note is not null) found.Add((reply.RemoteEndPoint.Address, note));
                }
                catch (JsonException) { /* not a tablo-web answer */ }
            }
        }
        catch (OperationCanceledException) { /* the listening window is over */ }
        return found;
    }

    /// <summary>Sign in with the Tablo account; the server checks it against Tablo's own login.</summary>
    private async Task<bool> SignInAsync(Uri server, Credentials credentials, bool loginRequired)
    {
        var http = new HttpClient(new SocketsHttpHandler { CookieContainer = _cookies, UseProxy = false })
        {
            BaseAddress = server,
            Timeout = TimeSpan.FromSeconds(60)
        };
        try
        {
            if (loginRequired)
            {
                using var res = await http.PostAsJsonAsync("api/login", new
                {
                    email = credentials.Email,
                    password = credentials.Password,
                    serverId = credentials.ServerId,
                    remember = false
                });
                if (!res.IsSuccessStatusCode)
                {
                    log.LogInformation("tablo-web at {Url} did not accept the sign-in ({Status})", server, (int)res.StatusCode);
                    http.Dispose();
                    return false;
                }
            }
            _http = http;
            _base = server;
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug("tablo-web at {Url} could not be reached: {Message}", server, ex.Message);
            http.Dispose();
            return false;
        }
    }

    private async Task<bool> StillThereAsync()
    {
        try
        {
            using var res = await _http!.GetAsync("api/me");
            if (res.IsSuccessStatusCode) return true;
        }
        catch { /* gone */ }
        Forget();
        return false;
    }

    private void Forget()
    {
        _http?.Dispose();
        _http = null;
        _base = null;
    }

    /// <summary>
    /// Pass a multi-view API call through to the server. A 401 (the server restarted, or the session
    /// lapsed) forgets the server, so the next refresh signs in again.
    /// </summary>
    public async Task<(int Status, string ContentType, byte[] Body)> ForwardAsync(string method, string pathAndQuery, byte[]? body)
    {
        if (_http is not { } http)
            return (404, "application/json", """{"error":"No tablo-web server with multi-view was found on this network."}"""u8.ToArray());

        using var request = new HttpRequestMessage(new HttpMethod(method), pathAndQuery.TrimStart('/'));
        if (body is { Length: > 0 })
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        try
        {
            using var res = await http.SendAsync(request);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                Forget();
                _lastSearchUtc = DateTime.MinValue;
            }
            return ((int)res.StatusCode,
                res.Content.Headers.ContentType?.ToString() ?? "application/json",
                await res.Content.ReadAsByteArrayAsync());
        }
        catch (Exception ex)
        {
            return (502, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { error = ex.Message }));
        }
    }
}
