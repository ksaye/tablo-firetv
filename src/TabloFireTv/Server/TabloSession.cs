using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using TabloFireTv.Models;
using TabloFireTv.Services;

namespace TabloFireTv;

/// <summary>
/// Owns the single connection to the Tablo and everything cached from it.
///
/// The Tablo is a small appliance and it is shared: the phone apps and anything else on the
/// account talk to the same box, and a full guide load is tens of thousands of airings across
/// hundreds of batch calls. Hammering it makes it refuse connections outright, so this class is
/// deliberately stingy — one connection, one in-flight load of any given thing, and generous
/// cache lifetimes. The guide is also kept on disk (see <see cref="GuideStore"/>).
/// </summary>
public sealed class TabloSession(ILogger<TabloSession> log)
{
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private TabloClient? _client;
    private Credentials? _credentials;

    /// <summary>Human-readable connection state, surfaced to the UI so a cold start explains itself.</summary>
    public string State { get; private set; } = "Starting up…";
    public bool Connected => _client is not null;
    public ServerInfo? ServerInfo => _client?.ServerInfo;
    public TabloDevice? Device => _client?.Device;

    /// <summary>Nobody has signed in and there is nothing saved, so there is no DVR to talk to yet.</summary>
    public bool NeedsCredentials => _credentials is null;
    public string? AccountEmail => _credentials?.Email;
    /// <summary>The Tablo sign-in in use, for signing in to a tablo-web server with the same account.</summary>
    public Credentials? CurrentCredentials => _credentials;

    private readonly Cache<List<GuideChannelWrap>> _channels = new(TimeSpan.FromHours(6));
    // The free streaming channels and their listings come from the cloud, not the device, so
    // they are loaded and cached on their own — a busy DVR has nothing to do with them.
    private readonly Cache<List<GuideAiring>> _fastGuide = new(TimeSpan.FromHours(25));
    private readonly Cache<List<RecordingAiring>> _recordings = new(TimeSpan.FromMinutes(2));
    // The guide's real schedule is the daily reload in WarmAsync (3am local) plus whatever a
    // restart does; this lifetime is only a backstop for a day the schedule somehow misses.
    private readonly Cache<List<GuideAiring>> _guide = new(TimeSpan.FromHours(25));
    private readonly Cache<StorageBox> _storage = new(TimeSpan.FromMinutes(5));

    /// <summary>Progress of a guide load in flight, 0..1, or null when not loading.</summary>
    public double? GuideProgress { get; private set; }

    // What is on now on each channel, worked out once and kept until it stops being true.
    private readonly object _nowGate = new();
    private List<GuideAiring>? _nowFromDevice;
    private List<GuideAiring>? _nowFromFast;
    private Dictionary<string, GuideAiring>? _nowByChannel;
    private DateTime _nowGoodUntil = DateTime.MinValue;

    // ------------------------------------------------------------------ credentials

    /// <summary>
    /// Pick up a sign-in remembered from a previous launch, and the guide saved with it, so the
    /// app opens straight onto the channels instead of asking again or reloading the guide.
    /// </summary>
    public void Restore()
    {
        if (CredentialStore.Load(log) is not { } saved)
        {
            State = "Waiting for a Tablo sign-in.";
            return;
        }

        _credentials = saved;
        log.LogInformation("Using the saved Tablo sign-in ({Email})", saved.Email);

        if (GuideStore.Load(saved.ServerId, TimeSpan.FromHours(25), log) is { } guide)
        {
            _guide.Seed(guide.Device, guide.SavedUtc);
            _fastGuide.Seed(guide.Fast, guide.SavedUtc);
            log.LogInformation("Guide restored from disk: {Count} airings saved {Saved}",
                guide.Device.Count + guide.Fast.Count, guide.SavedUtc);
        }
    }

    /// <summary>
    /// Check credentials against the Tablo cloud and, unless we are already connected with them,
    /// connect the server to the DVR.
    ///
    /// The cloud login is the real authentication: signing in to this site *is* proving you can
    /// sign in to the Tablo account. When the account owns more than one DVR and none was chosen,
    /// this returns the list instead of guessing.
    /// </summary>
    public async Task<SignInResult> SignInAsync(Credentials credentials, CancellationToken ct)
    {
        var client = new TabloClient();
        await client.LoginAsync(credentials.Email, credentials.Password, ct);

        // Already serving this account and this box? Then the caller has proved who they are and
        // there is nothing to rebuild — leave the live connection and its caches alone. Rebuilding
        // would throw away a guide that took minutes to load, and reload it off a busy device.
        if (_client is { Device: not null } live &&
            string.Equals(_credentials?.Email, credentials.Email, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(credentials.ServerId) || credentials.ServerId == live.Device.ServerId))
        {
            _credentials = _credentials! with { Password = credentials.Password };
            return new SignInResult(null, live.Device.ServerId, live.Device.Name);
        }

        var device = await ChooseDeviceAsync(client, credentials.ServerId, ct);
        if (device is null)
        {
            // Several reachable devices and no choice made — hand back the menu.
            var reachable = await ReachableAsync(client, ct);
            return new SignInResult(
                reachable.Select(d => new DeviceOption(d.ServerId, d.Name, d.Host)).ToList(), null, null);
        }

        await client.SelectDeviceAsync(client.Account!.Profiles.First(), device, ct);

        _credentials = credentials with { ServerId = device.ServerId };
        Install(client, device);
        return new SignInResult(null, device.ServerId, device.Name);
    }

    /// <summary>Forget the connection and the credentials — the site goes back to asking for a sign-in.</summary>
    public void Disconnect()
    {
        GuideStore.Clear();
        _client = null;
        _credentials = null;
        _channels.Clear();
        _recordings.Clear();
        _guide.Clear();
        _fastGuide.Clear();
        _storage.Clear();
        State = "Waiting for a Tablo sign-in.";
    }

    private void Install(TabloClient client, TabloDevice device)
    {
        var replacing = _client is not null;
        _client = client;
        if (replacing)
        {
            // A different box means everything cached describes the wrong DVR.
            _channels.Clear();
            _recordings.Clear();
            _guide.Clear();
            _fastGuide.Clear();
            _storage.Clear();
        }
        State = $"Connected to {device.Display}";
        log.LogInformation("Connected to {Device} ({Model})", device.Display, client.ServerInfo?.Model.Name);
    }

    // ------------------------------------------------------------------ connection

    /// <summary>
    /// Get a connected client, connecting on first use. Concurrent callers share one attempt
    /// rather than each starting their own login.
    /// </summary>
    public async Task<TabloClient> ClientAsync(CancellationToken ct = default)
    {
        if (_client is { } ready) return ready;

        await _connectGate.WaitAsync(ct);
        try
        {
            if (_client is { } racedIn) return racedIn;
            _client = await ConnectAsync(ct);
            return _client;
        }
        finally { _connectGate.Release(); }
    }

    private async Task<TabloClient> ConnectAsync(CancellationToken ct)
    {
        if (_credentials is not { } credentials)
        {
            State = "Waiting for a Tablo sign-in.";
            throw new InvalidOperationException("Nobody has signed in to a Tablo account yet.");
        }

        try
        {
            State = "Signing in to the Tablo account…";
            var client = new TabloClient();
            await client.LoginAsync(credentials.Email, credentials.Password, ct);

            State = "Looking for a Tablo on the network…";
            var device = await ChooseDeviceAsync(client, credentials.ServerId, ct)
                         ?? (await ReachableAsync(client, ct)).First();
            await client.SelectDeviceAsync(client.Account!.Profiles.First(), device, ct);

            State = $"Connected to {device.Display}";
            log.LogInformation("Connected to {Device} ({Model})", device.Display, client.ServerInfo?.Model.Name);
            return client;
        }
        catch (Exception ex)
        {
            State = "Not connected: " + ex.Message;
            log.LogWarning(ex, "Connect failed");
            throw;
        }
    }

    /// <summary>
    /// Which DVR to talk to. Returns null when the account has several that answer and none was
    /// asked for, so the caller can offer the choice rather than picking one at random.
    /// </summary>
    private static async Task<TabloDevice?> ChooseDeviceAsync(TabloClient client, string? serverId, CancellationToken ct)
    {
        var reachable = await ReachableAsync(client, ct);
        if (!string.IsNullOrWhiteSpace(serverId))
            return reachable.FirstOrDefault(d => d.ServerId == serverId)
                   ?? throw new InvalidOperationException(
                       "The Tablo that was chosen is not answering on this network.");
        return reachable.Count == 1 ? reachable[0] : null;
    }

    /// <summary>
    /// The devices on the account that actually answer. An account keeps listing units that were
    /// returned or replaced, so probe rather than trusting the order the cloud gives them in.
    /// </summary>
    private static async Task<List<TabloDevice>> ReachableAsync(TabloClient client, CancellationToken ct)
    {
        var devices = client.Account!.Devices;
        var probes = await Task.WhenAll(devices.Select(async d =>
            (device: d, info: await TabloClient.ProbeAsync(d, ct))));
        var reachable = probes.Where(p => p.info is not null).Select(p => p.device).ToList();

        if (reachable.Count == 0)
            throw new InvalidOperationException(devices.Count == 0
                ? "That account has no Tablo on it."
                : $"None of the {devices.Count} Tablo(s) on the account answered on this network. " +
                  "This Fire TV has to be on the same network as the DVR.");
        return reachable;
    }

    /// <summary>
    /// Drop the connection so the next call signs in again. The Lighthouse token does expire,
    /// and the failure mode is a 401 on an otherwise valid request.
    /// </summary>
    public void Invalidate()
    {
        _client = null;
        State = "Reconnecting…";
    }

    /// <summary>
    /// Run a device call, signing in again once if the token has expired. Anything else
    /// propagates — a caller that gets an error should see the real one.
    /// </summary>
    public async Task<T> WithRetryAsync<T>(Func<TabloClient, Task<T>> work, CancellationToken ct = default)
    {
        try
        {
            return await work(await ClientAsync(ct));
        }
        catch (TabloHttpException ex) when (ex.Status is 401 or 403)
        {
            log.LogInformation("Device returned {Status}; re-authenticating", ex.Status);
            Invalidate();
            return await work(await ClientAsync(ct));
        }
    }

    // ------------------------------------------------------------------ cached content

    /// <summary>
    /// Every channel: the antenna ones the device scanned, plus the account's free streaming
    /// (FAST) channels, which only the cloud lineup knows about. A cloud hiccup costs the FAST
    /// channels, never the antenna ones.
    /// </summary>
    public Task<List<GuideChannelWrap>> ChannelsAsync(bool force = false, CancellationToken ct = default) =>
        _channels.GetAsync(force, () => WithRetryAsync(async c =>
        {
            var all = await c.GetAllChannelsAsync(ct);
            var fast = all.Count(x => TabloClient.IsFast(x.Path));
            log.LogInformation("Channels: {Ota} antenna + {Fast} free streaming", all.Count - fast, fast);
            return all;
        }, ct));

    public Task<List<RecordingAiring>> RecordingsAsync(bool force = false, CancellationToken ct = default) =>
        _recordings.GetAsync(force, () => WithRetryAsync(c => c.GetRecordingsAsync(ct), ct));

    /// <summary>
    /// The guide: loaded at startup, once a day at <see cref="GuideHour"/>, and on an explicit
    /// refresh — never on a visitor's page load.
    ///
    /// A load that lost batches to a busy device is NOT trusted: it expires in minutes rather
    /// than hours, and if it came back smaller than what we already have, the older, fuller
    /// guide is kept. A 3am refresh that only managed 7,050 of 19,951 airings otherwise
    /// replaced a complete guide with a mostly-empty one for the next six hours.
    /// </summary>
    public async Task<List<GuideAiring>> GuideAsync(bool force = false, CancellationToken ct = default)
    {
        var deviceFresh = _guide.Fresh;
        var device = await DeviceGuideAsync(force, ct);
        var fast = await FastGuideAsync(force, ct);

        // A newly loaded guide goes to disk, so the next launch does not load it again.
        if ((force || !deviceFresh) && _guide.Fresh && _credentials?.ServerId is { } serverId)
            GuideStore.Save(serverId, device, fast, log);

        return fast.Count == 0 ? device : device.Concat(fast).ToList();
    }

    /// <summary>
    /// What is on now on each channel, by channel path.
    ///
    /// Worked out once and then kept, because the obvious way is far too slow here: the guide is
    /// forty thousand airings, and picking today's programme out of it means reading every one of
    /// their start times. On a Fire TV Stick that took a second or more — and the Live TV screen
    /// and every channel-change key press asked for it. This answer is rebuilt only when the guide
    /// itself reloads, or when a programme in it starts or ends, which is exactly when it changes.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, GuideAiring>> NowByChannelAsync(CancellationToken ct = default)
    {
        var device = await DeviceGuideAsync(false, ct);
        var fast = await FastGuideAsync(false, ct);
        var now = DateTime.UtcNow;

        lock (_nowGate)
        {
            if (_nowByChannel is { } cached && now < _nowGoodUntil
                && ReferenceEquals(device, _nowFromDevice) && ReferenceEquals(fast, _nowFromFast))
                return cached;

            var byChannel = new Dictionary<string, GuideAiring>();
            // Also the moment this answer expires: the first programme boundary ahead of us.
            // Capped, so a channel with no listings at all is still looked at again before long.
            var goodUntil = now.AddMinutes(15);

            foreach (var airing in device.Count == 0 ? fast : fast.Count == 0 ? device : device.Concat(fast))
            {
                var channel = airing.AiringDetails.ChannelPath;
                if (string.IsNullOrEmpty(channel)) continue;

                var start = TabloClient.ParseDate(airing.AiringDetails.Datetime);
                if (start == DateTime.MinValue) continue;
                var end = start.AddSeconds(airing.AiringDetails.Duration);

                if (start <= now && end > now)
                {
                    byChannel[channel] = airing;
                    if (end < goodUntil) goodUntil = end;
                }
                else if (start > now && start < goodUntil)
                {
                    goodUntil = start;
                }
            }

            _nowFromDevice = device;
            _nowFromFast = fast;
            _nowByChannel = byChannel;
            _nowGoodUntil = goodUntil;
            return byChannel;
        }
    }

    /// <summary>
    /// Listings for the free streaming channels, from the cloud. One call per channel per day,
    /// so it is loaded on the same daily schedule as the DVR's own guide — but it is cheap for
    /// the DVR (it never sees it) and a failure here leaves the antenna guide untouched.
    /// </summary>
    private Task<List<GuideAiring>> FastGuideAsync(bool force, CancellationToken ct) =>
        _fastGuide.GetAsync(force, async () =>
        {
            try
            {
                var fastChannels = (await ChannelsAsync(false, ct))
                    .Where(c => TabloClient.IsFast(c.Path)).ToList();
                if (fastChannels.Count == 0) return new List<GuideAiring>();

                var airings = await WithRetryAsync(
                    c => c.GetFastGuideAiringsAsync(fastChannels, ct: ct), ct);
                log.LogInformation("Free streaming guide loaded: {Count} airings across {Channels} channels",
                    airings.Count, fastChannels.Count);
                return airings;
            }
            catch (Exception ex)
            {
                log.LogWarning("Free streaming guide failed ({Message})", ex.Message);
                return new List<GuideAiring>();
            }
        }, list => list.Count > 0);

    private Task<List<GuideAiring>> DeviceGuideAsync(bool force, CancellationToken ct) =>
        _guide.GetAsync(force, async previous =>
        {
            var progress = new Progress<double>(p => GuideProgress = p);
            GuideProgress = 0;
            try
            {
                var stats = new GuideLoadStats();
                var airings = await WithRetryAsync(
                    c => c.GetGuideAiringsAsync(progress, ct, stats), ct);

                if (stats.Complete)
                {
                    log.LogInformation("Guide loaded: {Count} airings ({Recovered} batches needed the " +
                        "slow sweep)", airings.Count, stats.RecoveredBatches);
                    return (airings, true);
                }

                log.LogWarning(
                    "Guide loaded PARTIALLY: {Count} of {Expected} airings ({Failed} of {Batches} " +
                    "batches never answered — the device is busy). Retrying in {Retry}.",
                    airings.Count, stats.Expected, stats.FailedBatches, stats.Batches, GuideRetry);

                if (previous is not null && previous.Count > airings.Count)
                {
                    log.LogWarning("Keeping the previous guide ({Count} airings) — it is fuller.",
                        previous.Count);
                    return (previous, false);
                }
                return (airings, false);
            }
            finally { GuideProgress = null; }
        }, GuideRetry);

    /// <summary>
    /// Recording space. Wrapped because the cache holds reference types and the endpoint may
    /// legitimately have nothing to report on some firmware.
    /// </summary>
    public Task<StorageBox> StorageAsync(CancellationToken ct = default) =>
        _storage.GetAsync(false, async () =>
        {
            var info = await WithRetryAsync(c => c.GetStorageAsync(ct), ct);
            if (info is null)
            {
                // GetStorageAsync swallows everything and returns null, which used to leave the
                // page reporting zero bytes with nothing in the log to explain it.
                var raw = await WithRetryAsync(c => c.DeviceRawGetAsync("/server/harddrives", ct), ct);
                log.LogWarning("No storage figures: /server/harddrives answered {Status}: {Body}",
                    raw.Status, raw.Body.Length > 300 ? raw.Body[..300] : raw.Body);
            }
            return new StorageBox(info?.Normalized());
        }, box => box.Info is not null);

    /// <summary>
    /// How long to wait before another go at a guide that came back incomplete. Long enough
    /// that a busy device is left alone — a guide load is hundreds of calls and hammering it is
    /// what wedges the box — but short enough that the listings heal within one sitting.
    /// </summary>
    private static readonly TimeSpan GuideRetry = TimeSpan.FromMinutes(10);

    /// <summary>True while the guide has never finished loading — the UI shows a progress bar.</summary>
    public bool GuideReady => _channels.HasValue && _guide.HasValue;

    /// <summary>
    /// Local hour of the daily guide refresh while the app is left running — 3am, when nothing
    /// is watching the DVR. A full load is hundreds of calls the device would rather not field
    /// while someone is using it.
    /// </summary>
    private const int GuideHour = 3;

    /// <summary>
    /// Minutes past <see cref="GuideHour"/> this particular install reloads at, fixed per device.
    ///
    /// A DVR is usually shared with other things that reload their own guide — another copy of
    /// this app, a browser front end, a recording picker — and a full load is hundreds of calls.
    /// All of them starting on the same stroke of 3am is what makes a Tablo refuse connections
    /// for half an hour (measured on a gen-4 box in September 2026: one client reloading took
    /// five minutes of refusals, four at once took nearly thirty, and one of them came back with
    /// a partial guide). Spreading installs across the hour costs nothing and keeps them apart.
    /// </summary>
    private static readonly int GuideMinute = PickGuideMinute();

    private static int PickGuideMinute()
    {
        try
        {
            const string key = "guide_refresh_minute";
            var stored = Preferences.Get(key, -1);
            if (stored is >= 0 and < 60) return stored;

            var minute = Random.Shared.Next(60);
            Preferences.Set(key, minute);
            return minute;
        }
        catch
        {
            // No settings storage: 3am sharp, which is what this did before.
            return 0;
        }
    }

    /// <summary>The next refresh time strictly after <paramref name="after"/>, local time.</summary>
    private static DateTime NextGuideRefresh(DateTime after)
    {
        var today = after.Date.AddHours(GuideHour).AddMinutes(GuideMinute);
        return today > after ? today : today.AddDays(1);
    }

    /// <summary>
    /// Warm the caches in the background at startup so the first visitor doesn't wait out a
    /// full guide load, and reload the guide once a day. Failures are logged and retried,
    /// never fatal.
    ///
    /// The guide is on a clock rather than a cache lifetime: a lifetime makes the reload drift
    /// into whatever time of day the service last restarted, and the reload is the single most
    /// expensive thing done to the device.
    /// </summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        var nextGuide = NextGuideRefresh(DateTime.Now);
        log.LogInformation("Daily guide refresh at {Hour:00}:00 local; next at {Next}",
            GuideHour, nextGuide);

        while (!ct.IsCancellationRequested)
        {
            // Nothing to warm until somebody has signed in. Wait quietly rather than logging a
            // failure a minute forever on a fresh install.
            if (!NeedsCredentials)
            {
                var due = DateTime.Now >= nextGuide;
                try
                {
                    await ChannelsAsync(ct: ct);
                    await RecordingsAsync(ct: ct);

                    // Reschedule before loading, not after: a load that throws must not leave
                    // the loop trying again every minute.
                    if (due)
                    {
                        nextGuide = NextGuideRefresh(DateTime.Now);
                        log.LogInformation("Daily guide refresh starting; next at {Next}", nextGuide);
                    }
                    await GuideAsync(due, ct);
                }
                // Only a real shutdown ends the loop. An HttpClient timeout also surfaces as a
                // TaskCanceledException, and treating that as shutdown silently killed the warmer —
                // leaving the guide, which nothing else loads, spinning forever in the browser.
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    log.LogWarning("Warm-up failed ({Message}); will retry", ex.Message);

                    // A daily refresh that blew up should not leave yesterday's listings for
                    // another 24 hours — try again shortly instead.
                    if (due && nextGuide - DateTime.Now > GuideRetry)
                        nextGuide = DateTime.Now + GuideRetry;
                }
            }

            // Poll often enough to notice an expired cache promptly, but the caches themselves
            // decide when real work happens.
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { return; }
        }

        log.LogInformation("Warm-up loop stopped");
    }
}

public sealed record StorageBox(StorageInfo? Info);

/// <summary>
/// The outcome of a sign-in: either the DVR that is now connected, or — when the account has
/// several and none was chosen — the list to choose from.
/// </summary>
public sealed record SignInResult(List<DeviceOption>? Devices, string? ServerId, string? DeviceName);

public sealed record DeviceOption(string ServerId, string Name, string Host);

/// <summary>
/// A value with a lifetime, refreshed by at most one caller at a time. While a refresh is in
/// flight other callers get the stale value if there is one, so a slow guide reload never
/// stalls a page load.
/// </summary>
internal sealed class Cache<T>(TimeSpan lifetime) where T : class
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private T? _value;
    private DateTime _loadedUtc = DateTime.MinValue;

    public bool HasValue => _value is not null;
    public bool Fresh => _value is not null && DateTime.UtcNow - _loadedUtc < lifetime;

    /// <summary>How soon to try again after a load that came back empty.</summary>
    private static readonly TimeSpan RetrySoon = TimeSpan.FromSeconds(30);

    /// <param name="valid">
    /// Optional test for "this load actually worked". A value that fails it is still returned,
    /// but expires in seconds rather than being trusted for the full lifetime — which is what
    /// a device call that lost a race with the guide load deserves.
    /// </param>
    public Task<T> GetAsync(bool force, Func<Task<T>> load, Func<T, bool>? valid = null) =>
        GetAsync(force, async _ =>
        {
            var v = await load();
            return (v, valid is null || valid(v));
        }, RetrySoon);

    /// <summary>
    /// As above, but the loader is handed whatever is cached now and says for itself whether
    /// the load is trustworthy — which lets it fall back to the previous value. An untrusted
    /// value expires after <paramref name="retryAfter"/> instead of the full lifetime.
    /// </summary>
    public async Task<T> GetAsync(
        bool force, Func<T?, Task<(T Value, bool Valid)>> load, TimeSpan retryAfter)
    {
        if (!force && Fresh) return _value!;

        // Someone else is already refreshing. If we have anything at all, hand back the stale
        // copy instead of queueing — a page load must never wait out a guide reload.
        if (!_gate.Wait(0))
        {
            if (_value is not null && !force) return _value;
            await _gate.WaitAsync();
        }

        try
        {
            if (!force && Fresh) return _value!;
            var (value, valid) = await load(_value);
            _value = value;
            _loadedUtc = valid ? DateTime.UtcNow : DateTime.UtcNow - lifetime + retryAfter;
            return _value;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Start from a value loaded earlier (from disk), as if it had been loaded at <paramref name="loadedUtc"/>.</summary>
    public void Seed(T value, DateTime loadedUtc)
    {
        _value = value;
        _loadedUtc = loadedUtc;
    }

    /// <summary>Throw the value away — the DVR it described is no longer the one we talk to.</summary>
    public void Clear()
    {
        _value = null;
        _loadedUtc = DateTime.MinValue;
    }
}
