using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.UI;
using Java.Interop;
using Microsoft.Extensions.Logging;
using TabloFireTv.Updates;
using AWebView = Android.Webkit.WebView;
using AProgressBar = Android.Widget.ProgressBar;

namespace TabloFireTv;

/// <summary>
/// The whole app on screen: the web interface in a WebView, and a native Media3 ExoPlayer on top
/// of it whenever something plays.
///
/// The interface navigates itself with the remote (wwwroot/nav.js moves DOM focus). Native code
/// is left with the jobs that need it: pumping key repeats the Fire TV remote does not send
/// reliably, playing video (the Tablo streams MPEG-2 video with AC3 audio, which the Fire TV
/// decodes in hardware but no WebView can play), and the remote's live-TV controls.
/// </summary>
// Not MainLauncher=true: that only tags the phone launcher category, and this activity needs the
// Leanback one too so Fire TV puts a tile for it on the home screen.
[Activity(
    Theme = "@style/Maui.SplashTheme",
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
                           ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionMain },
    Categories = new[] { Intent.CategoryLauncher, "android.intent.category.LEANBACK_LAUNCHER" })]
public class MainActivity : MauiAppCompatActivity
{
    // The app's back end (Server/LocalServer.cs): the web interface and its /api, answered
    // in-process by talking straight to the Tablo. One per process, surviving activity restarts.
    private static LocalServer? _server;
    private static LocalServer Server => _server!;
    private static string HomeUrl => Server.BaseUrl;

    /// <summary>
    /// Injected into the page. When app.js asks the server to play something, the answer's URL is
    /// handed to the native player instead of the page's own &lt;video&gt;, and the page is kept
    /// from trying to play it too. app.js itself is unchanged from tablo-web.
    /// </summary>
    private const string PageScript = @"(function(){
        if (window.__tvPlayIntercept) return;
        window.__tvPlayIntercept = true;

        var video = document.getElementById('video');
        var player = document.getElementById('player');
        if (video) {
            // app.js still assigns video.src; make that a no-op while the native player has it.
            var nativeSrc = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'src');
            Object.defineProperty(video, 'src', {
                configurable: true,
                get: function(){ return nativeSrc.get.call(this); },
                set: function(v){ if (!window.__tvNativeTakeover) nativeSrc.set.call(this, v); }
            });
        }

        // app.js prefers hls.js for playlists; while native playback has the stream, say it is
        // unsupported so app.js falls through to the (disabled) src assignment above.
        function hookHls(){
            if (!window.Hls || window.Hls.__tvHooked) return;
            var real = window.Hls.isSupported;
            window.Hls.isSupported = function(){
                return window.__tvNativeTakeover ? false : real.apply(this, arguments);
            };
            window.Hls.__tvHooked = true;
        }
        hookHls();
        var hlsTries = 0;
        var hlsTimer = setInterval(function(){
            hookHls();
            if ((window.Hls && window.Hls.__tvHooked) || ++hlsTries > 40) clearInterval(hlsTimer);
        }, 250);

        // Tell native code whether the player panel or a details sheet is open, so Back closes it
        // rather than leaving the app.
        var sheet = document.getElementById('sheet');
        if (window.AndroidTV) {
            var report = function(){
                window.AndroidTV.playerModalState((player && !player.hidden) || (sheet && !sheet.hidden));
            };
            [player, sheet].forEach(function(el){
                if (el) new MutationObserver(report).observe(el, { attributes: true, attributeFilter: ['hidden'] });
            });
        }

        var origFetch = window.fetch;
        window.fetch = function(input, init){
            var url = typeof input === 'string' ? input : (input && input.url) || '';
            var isMosaic = init && init.method === 'POST' && /\/api\/mosaic$/.test(url);
            if (isMosaic) {
                return origFetch.apply(this, arguments).then(function(res){
                    return res.clone().json().then(function(body){
                        if (body.url && body.id && window.AndroidTV) {
                            window.__tvNativeTakeover = true;
                            window.AndroidTV.playMosaic(body.url, body.id, JSON.stringify(body.channels || []));
                            // The native player owns the sound; stop the page's own auto-cycle,
                            // which app.js starts right after this resolves.
                            [0, 500, 1500].forEach(function(ms){
                                setTimeout(function(){ if (typeof stopRotate === 'function') stopRotate(); }, ms);
                            });
                        }
                        return res;
                    }).catch(function(){ return res; });
                });
            }
            if (!(init && init.method === 'POST' && url.indexOf('/api/play') !== -1)) {
                return origFetch.apply(this, arguments);
            }
            var position = 0, path = '';
            try {
                var req = JSON.parse(init.body);
                position = req.position || 0;
                path = req.path || '';
            } catch (e) {}

            // Resolve only once the takeover flag is set: app.js chains its own attach() onto
            // this promise, and attach() must already see it.
            return origFetch.apply(this, arguments).then(function(res){
                return res.clone().json().then(function(body){
                    if (body.url) {
                        window.__tvNativeTakeover = true;
                        var abs = new URL(body.url, location.href).href;
                        var title = (document.getElementById('playerTitle') || {}).textContent || '';
                        var subtitle = (document.getElementById('playerSub') || {}).textContent || '';
                        if (window.AndroidTV) window.AndroidTV.playNative(abs, title, subtitle, position, !!body.live, path);
                    } else {
                        window.__tvNativeTakeover = false;
                    }
                    return res;
                }).catch(function(){ return res; });
            });
        };
    })();";

    // Without a User-Agent the Tablo answers 403, and the video is fetched from it directly.
    private const string TabloUserAgent = "Tablo-FAST/1.7.0 (Mobile; iPhone; iOS 18.4)";

    // How long a channel press waits for another before tuning: flicking through five channels
    // should tune once, not five times, each holding a tuner until the Tablo notices it is idle.
    private const int ChannelSettleMs = 700;

    // How far behind the live edge live playback sits. The Tablo declares a 1s target duration
    // for ~2s segments, so ExoPlayer's default (three target durations) rebuffers on any hiccup.
    private const long LiveOffsetMs = 6000;

    private AWebView _webView = null!;
    private FrameLayout _root = null!;
    private float _density;

    private bool _playerModalOpen;
    private bool _nativePlayerActive;
    private bool _nativeLive;
    private int _nativeErrorRetries;

    // Channel up/down: the Tablo path playing now, where Up/Down has moved to but not tuned yet,
    // what is being tuned right now, and a generation counter so only the last press tunes.
    private string _nativePath = "";
    private string? _targetPath;
    private string? _tuningPath;
    private int _tuneGeneration;
    private List<NowDto>? _lineup;
    private DateTime _lineupFetchedUtc;
    private int _lineupRefreshing;

    // Multi-view (a tablo-web server's combined stream): its session id, the panes' names, and
    // which pane has the sound. Null id when not in multi-view.
    private string? _mosaicId;
    private List<string> _mosaicLabels = [];
    private int _mosaicPane;

    private IExoPlayer? _player;
    private PlayerView? _playerView;
    private FrameLayout? _nativeOverlay;
    private TextView? _overlayTitle;
    private TextView? _overlayTime;
    private AProgressBar? _overlayProgress;
    private readonly Handler _overlayHandler = new(Looper.MainLooper!);

    private readonly Handler _repeatHandler = new(Looper.MainLooper!);
    private Keycode? _heldKey;
    private int _holdTicks;

    private static bool IsCenterKey(Keycode key) =>
        key is Keycode.DpadCenter or Keycode.Enter or Keycode.NumpadEnter;

    private static bool IsDirectional(Keycode key) =>
        key is Keycode.DpadUp or Keycode.DpadDown or Keycode.DpadLeft or Keycode.DpadRight;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        if (_server is null)
        {
            _server = new LocalServer(IPlatformApplication.Current!.Services.GetRequiredService<ILoggerFactory>());
            _server.Start();
        }
        // The in-app server only answers requests carrying this cookie: this WebView's, and the
        // native player's (which copies it).
        CookieManager.Instance!.SetCookie(HomeUrl, $"{LocalServer.CookieName}={Server.Secret}; path=/");

        // Add our layer over MAUI's content view rather than replacing it: MAUI attaches its own
        // page to that view after OnCreate returns, and replacing it crashes on start-up.
        HideSystemBars();
        _density = Resources!.DisplayMetrics!.Density;
        _root = new FrameLayout(this);

        _webView = new AWebView(this)
        {
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
        };
        ConfigureWebView(_webView);
        _root.AddView(_webView);

        var mauiContent = (ViewGroup)Window!.DecorView.FindViewById(Android.Resource.Id.Content)!;
        mauiContent.AddView(_root, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _webView.LoadUrl(HomeUrl);

        _ = CheckForUpdateAsync();
    }

    // ------------------------------------------------------------------------------ updates

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var check = await AppUpdater.CheckAsync();
            if (check.Available) RunOnUiThread(() => ShowUpdateDialog(check));
        }
        catch
        {
            // No network, or GitHub's rate limit: never worth interrupting anything over.
        }
    }

    private void ShowUpdateDialog(UpdateCheck check)
    {
        if (IsFinishing || IsDestroyed) return;

        new AlertDialog.Builder(this)
            .SetTitle("Update available")
            .SetMessage($"Tablo for Fire TV {check.Version} is ready to install."
                + (string.IsNullOrWhiteSpace(check.Notes) ? "" : $"\n\n{check.Notes}"))
            .SetPositiveButton("Install", (_, _) => _ = InstallUpdateAsync(check))
            .SetNegativeButton("Later", (_, _) => { })
            .Show();
    }

    private async Task InstallUpdateAsync(UpdateCheck check)
    {
        var problem = await AppUpdater.DownloadAndInstallAsync(check);
        if (problem is not null)
            RunOnUiThread(() => Toast.MakeText(this, problem, ToastLength.Long)!.Show());
    }

    // ------------------------------------------------------------------------------ web view

    private void ConfigureWebView(AWebView webView)
    {
        var s = webView.Settings!;
        s.JavaScriptEnabled = true;
        s.DomStorageEnabled = true;
        s.MediaPlaybackRequiresUserGesture = false;
        s.LoadWithOverviewMode = true;
        s.UseWideViewPort = true;
        // The interface is served from inside the app; there is nothing to gain from caching it,
        // and a stale copy after an update would be confusing.
        s.CacheMode = CacheModes.NoCache;

        CookieManager.Instance!.SetAcceptCookie(true);

        webView.SetWebViewClient(new PageClient());
        webView.SetWebChromeClient(new ChromeClient(InjectPageScript));
        webView.AddJavascriptInterface(new Bridge(this), "AndroidTV");
    }

    /// <summary>
    /// Injected from every "the page is ready" signal (page finished, progress 100, resume),
    /// because no single one is reliable on every Fire TV. The script guards itself, so running it
    /// twice does nothing.
    /// </summary>
    private void InjectPageScript() => _webView.EvaluateJavascript(PageScript, null);

    protected override void OnResume()
    {
        base.OnResume();
        InjectPageScript();
    }

    protected override void OnPause()
    {
        base.OnPause();
        if (_nativePlayerActive) _player?.Pause();
    }

    protected override void OnDestroy()
    {
        if (_nativePlayerActive) CloseNativePlayer();
        base.OnDestroy();
    }

    // ------------------------------------------------------------------------------ native player

    /// <summary>Called (via the bridge) when app.js starts playing something: takes over the
    /// screen with ExoPlayer. PlayerView's fit mode scales the picture to the screen while keeping
    /// its aspect ratio.</summary>
    private void ShowNativePlayer(string url, string title, string subtitle, double positionSeconds, bool live, string path,
        string? cookieOverride = null)
    {
        CloseNativePlayer();
        _nativeLive = live;
        _nativeErrorRetries = 0;
        _nativePath = path;
        _targetPath = null;

        // ExoPlayer's HTTP stack is separate from the WebView's cookies, so it is handed the in-app
        // server's cookie explicitly: a recording's playlist comes from that server. The video
        // segments come from the Tablo itself, which needs its User-Agent and ignores the cookie.
        var cookie = cookieOverride ?? CookieManager.Instance?.GetCookie(HomeUrl);
        var http = new DefaultHttpDataSource.Factory().SetUserAgent(TabloUserAgent)!;
        if (!string.IsNullOrEmpty(cookie))
            http.SetDefaultRequestProperties(new Dictionary<string, string> { ["Cookie"] = cookie });
        var sources = new DefaultMediaSourceFactory(this).SetDataSourceFactory(http);

        var player = new ExoPlayerBuilder(this).SetMediaSourceFactory(sources).Build()!;
        // Inflated from XML because surface_type (a TextureView, which composites reliably over the
        // WebView on Fire TV) has no runtime setter.
        var playerView = (PlayerView)LayoutInflater.From(this)!.Inflate(Resource.Layout.player_view, _root, false)!;
        playerView.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        playerView.Player = player;
        playerView.UseController = false;
        playerView.ResizeMode = AspectRatioFrameLayout.ResizeModeFit;

        player.AddListener(new PlayerListener(this));
        player.SetMediaItem(BuildMediaItem(url, live));
        player.Prepare();
        if (!live && positionSeconds > 0) player.SeekTo((long)(positionSeconds * 1000));

        _root.AddView(playerView);
        _playerView = playerView;
        _player = player;
        _nativePlayerActive = true;
        SetKeepScreenOn(true);

        BuildNativeOverlay(title, subtitle);
        // Live has no progress bar; its time line only says how far behind live a pause or rewind
        // has left you.
        if (live && _overlayProgress is { } pb) pb.Visibility = ViewStates.Gone;
        SetNativeTitleVisible(false);
        player.Play();
        _overlayHandler.Post(NativeOverlayTick);

        // Fetch the channel list while the picture is starting, so the first Up or Down press
        // already has one to step through.
        if (live) _ = RefreshLineupAsync();
    }

    /// <summary>
    /// Play a multi-view started through the page. The stream comes straight from the tablo-web
    /// server (with its sign-in cookie); every pane's audio is a separate audio track in it, so moving
    /// the sound is a track change here plus a note to the server to move its yellow border.
    /// </summary>
    private void ShowMosaicPlayer(string relativeUrl, string id, string channelsJson)
    {
        if (Server.MultiView.BaseUrl is not { } server)
        {
            Toast.MakeText(this, "The multi-view server is no longer available.", ToastLength.Long)!.Show();
            ClosePagePlayer();
            return;
        }

        var labels = new List<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(channelsJson);
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var number = c.TryGetProperty("number", out var n) ? n.GetString() : "";
                var call = c.TryGetProperty("callSign", out var cs) ? cs.GetString() : "";
                labels.Add($"{number} {call}".Trim());
            }
        }
        catch { /* labels are only for the on-screen note */ }

        ShowNativePlayer(new Uri(server, relativeUrl).AbsoluteUri, "Multi-view", string.Join(" · ", labels),
            0, live: true, path: "", cookieOverride: Server.MultiView.CookieHeader);
        _mosaicId = id;
        _mosaicLabels = labels;
        _mosaicPane = 0;
        ShowMosaicBanner();
    }

    /// <summary>← / →: move the sound to the previous / next pane.</summary>
    private void StepMosaicPane(int delta)
    {
        var count = Math.Max(_mosaicLabels.Count, 1);
        _mosaicPane = ((_mosaicPane + delta) % count + count) % count;
        ApplyMosaicAudio();
        ShowMosaicBanner();

        var id = _mosaicId;
        var pane = _mosaicPane;
        if (id is not null)
            _ = Task.Run(() => Server.MultiView.ForwardAsync("POST", $"/api/mosaic/{id}/audio",
                System.Text.Encoding.UTF8.GetBytes($"{{\"pane\":{pane}}}")));
    }

    /// <summary>Select the audio track for the current pane. The audio renditions are in pane order;
    /// they may arrive as one track group or one group each, so count across all of them.</summary>
    private void ApplyMosaicAudio()
    {
        if (_mosaicId is null || _player is not { } p) return;
        var index = 0;
        foreach (var item in p.CurrentTracks.Groups)
        {
            if (item is not Tracks.Group group || group.Type != C.TrackTypeAudio) continue;
            for (var t = 0; t < group.Length; t++, index++)
            {
                if (index != _mosaicPane) continue;
                if (group.IsTrackSelected(t)) return;
                p.TrackSelectionParameters = p.TrackSelectionParameters.BuildUpon()
                    .SetOverrideForType(new TrackSelectionOverride(group.MediaTrackGroup, t))!
                    .Build()!;
                return;
            }
        }
    }

    private void ShowMosaicBanner()
    {
        if (_overlayTitle is not { } t) return;
        var name = _mosaicPane < _mosaicLabels.Count ? _mosaicLabels[_mosaicPane] : $"pane {_mosaicPane + 1}";
        t.Text = $"Sound: {_mosaicPane + 1} · {name}";
        SetNativeTitleVisible(true);
        var shown = _mosaicPane;
        _overlayHandler.PostDelayed(() => { if (_mosaicId is not null && shown == _mosaicPane) SetNativeTitleVisible(false); }, 2500);
    }

    /// <summary>
    /// A live stream can drop (a weak channel, a busy tuner). Try the same playlist once, then ask
    /// the Tablo for the channel again, since a stream nobody fetched for a while is gone for good.
    /// A recording that fails just closes.
    /// </summary>
    private void OnNativePlayerError()
    {
        if (!_nativePlayerActive || _player is null) return;
        if (_nativeLive && _nativeErrorRetries < 3)
        {
            _nativeErrorRetries++;
            if (_nativeErrorRetries >= 2 && _nativePath.Length > 0)
            {
                _ = TuneAsync(_nativePath, ++_tuneGeneration, 1500);
                return;
            }
            _overlayHandler.PostDelayed(() =>
            {
                if (_nativePlayerActive && _player is { } p) { p.Prepare(); p.Play(); }
            }, 1500);
            return;
        }
        CloseNativePlayer();
    }

    /// <summary>
    /// Pause and rewind on live TV: the Tablo's live playlist keeps every segment since the channel
    /// was tuned, so all of it is seekable. Left alone, ExoPlayer treats time far behind its target
    /// offset as drift, speeding up to catch up or jumping back to the edge; a fixed 1x speed and a
    /// very large maximum offset keep the picture exactly where it was paused.
    /// </summary>
    private static MediaItem BuildMediaItem(string url, bool live)
    {
        if (!live) return MediaItem.FromUri(url)!;
        return new MediaItem.Builder()
            .SetUri(url)!
            .SetLiveConfiguration(new MediaItem.LiveConfiguration.Builder()
                .SetTargetOffsetMs(LiveOffsetMs)!
                .SetMaxOffsetMs(12 * 3600 * 1000L)!
                .SetMinPlaybackSpeed(1f)!
                .SetMaxPlaybackSpeed(1f)!
                .Build())!
            .Build()!;
    }

    /// <summary>Up/Down on live TV: move one channel along the Live TV list (antenna and free
    /// streaming channels kept apart, wrapping at the ends). Shows the channel at once and tunes
    /// only once the presses stop.</summary>
    private async void StepChannel(int delta)
    {
        var from = _targetPath ?? _nativePath;
        if (from.Length == 0) return;
        var generation = ++_tuneGeneration;

        // Never wait for the channel list here: a key press has to move the moment it is pressed.
        // A stale list is refreshed in the background, and only the very first press — before
        // there is any list at all — waits, and then only if opening the player has not already
        // fetched one.
        if (_lineup is null) await RefreshLineupAsync();
        else if (DateTime.UtcNow - _lineupFetchedUtc > TimeSpan.FromMinutes(1)) _ = RefreshLineupAsync();

        if (!_nativePlayerActive || _lineup is not { Count: > 0 } all) return;

        var current = all.FindIndex(n => n.Channel.Path == from);
        var fast = current >= 0 && all[current].Channel.IsFast;
        var list = all.Where(n => n.Channel.IsFast == fast).ToList();
        var index = list.FindIndex(n => n.Channel.Path == from);
        var next = list[index < 0 ? 0 : ((index + delta) % list.Count + list.Count) % list.Count];

        _targetPath = next.Channel.Path;
        ShowChannelBanner(next);
        await TuneAsync(next.Channel.Path, generation, ChannelSettleMs);
    }

    /// <summary>Fetch the Live TV line-up for channel up/down. Safe to call while one is running.</summary>
    private async Task RefreshLineupAsync()
    {
        if (Interlocked.Exchange(ref _lineupRefreshing, 1) == 1) return;
        try
        {
            var lineup = await Task.Run(Server.NowAsync);
            if (lineup.Count > 0) { _lineup = lineup; _lineupFetchedUtc = DateTime.UtcNow; }
        }
        catch { /* keep whatever list we had */ }
        finally { Interlocked.Exchange(ref _lineupRefreshing, 0); }
    }

    private void ShowChannelBanner(NowDto now)
    {
        if (_overlayTitle is not { } t) return;
        var channel = $"{now.Channel.Number} {now.Channel.CallSign}".Trim();
        t.Text = now.Airing is { } a ? $"{channel}\n{a.Title}" : channel;
        SetNativeTitleVisible(true);
    }

    /// <summary>Tune <paramref name="path"/> into the open player, unless a newer press has
    /// superseded it by the time the delay or the tune finishes.</summary>
    private async Task TuneAsync(string path, int generation, int delayMs)
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        if (generation != _tuneGeneration || !_nativePlayerActive) return;

        PlayDto? play = null;
        _tuningPath = path;
        try { play = await Task.Run(() => Server.PlayAsync(path, live: true, 0, 0)); }
        catch { /* reported below */ }
        finally { if (generation == _tuneGeneration) _tuningPath = null; }

        if (generation != _tuneGeneration || !_nativePlayerActive || _player is not { } p) return;
        if (play is null || string.IsNullOrEmpty(play.Url))
        {
            _targetPath = null;
            SetNativeTitleVisible(false);
            Toast.MakeText(this, "Could not tune that channel - no signal, or every tuner is busy.",
                ToastLength.Long)!.Show();
            return;
        }

        _nativePath = path;
        _targetPath = null;
        p.SetMediaItem(BuildMediaItem(play.Url.StartsWith("http") ? play.Url : HomeUrl + play.Url, live: true));
        p.Prepare();
        p.Play();

        _overlayHandler.PostDelayed(() =>
        {
            if (generation == _tuneGeneration) SetNativeTitleVisible(false);
        }, 2500);
    }

    /// <summary>The title (shown while paused or changing channel) and a bottom time line.</summary>
    private void BuildNativeOverlay(string title, string subtitle)
    {
        var overlay = new FrameLayout(this)
        {
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
        };

        _overlayTitle = new TextView(this)
        {
            Text = string.IsNullOrEmpty(subtitle) ? title : $"{title}\n{subtitle}",
            TextSize = 26,
            Gravity = GravityFlags.Center,
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center)
        };
        _overlayTitle.SetTextColor(Android.Graphics.Color.White);
        _overlayTitle.SetShadowLayer(8, 0, 0, Android.Graphics.Color.Black);
        overlay.AddView(_overlayTitle);

        var bottom = new LinearLayout(this) { Orientation = Orientation.Vertical };
        bottom.SetPadding(Dp(24), Dp(8), Dp(24), Dp(24));
        bottom.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom);

        _overlayProgress = new AProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Max = 1000,
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(6))
        };
        _overlayTime = new TextView(this) { Gravity = GravityFlags.CenterHorizontal, TextSize = 14 };
        _overlayTime.SetTextColor(Android.Graphics.Color.White);
        _overlayTime.SetShadowLayer(6, 0, 0, Android.Graphics.Color.Black);
        bottom.AddView(_overlayProgress);
        bottom.AddView(_overlayTime);
        overlay.AddView(bottom);

        _root.AddView(overlay);
        _nativeOverlay = overlay;
    }

    private void SetNativeTitleVisible(bool visible)
    {
        if (_overlayTitle is { } t) t.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
    }

    private void NativeOverlayTick()
    {
        if (!_nativePlayerActive || _player is not { } p) return;
        var duration = p.Duration;
        if (_mosaicId is not null)
        {
            _overlayTime!.Text = "";
        }
        else if (_nativeLive)
        {
            var behind = LiveBehindMs(p);
            _overlayTime!.Text = behind > 0 ? $"LIVE  -{FormatTime(behind)}" : "";
        }
        else if (duration > 0)
        {
            _overlayProgress!.Progress = (int)(1000L * p.CurrentPosition / duration);
            _overlayTime!.Text = $"{FormatTime(p.CurrentPosition)} / {FormatTime(duration)}";
        }
        _overlayHandler.PostDelayed(NativeOverlayTick, 500);
    }

    /// <summary>How far behind live playback is, beyond the normal live offset; 0 at live.</summary>
    private static long LiveBehindMs(IExoPlayer p)
    {
        var duration = p.Duration;
        if (duration <= 0) return 0;
        var behind = duration - p.CurrentPosition - LiveOffsetMs;
        return behind > 5000 ? behind : 0;
    }

    /// <summary>Left/Right on live TV. Rewinds as far back as the channel was tuned; fast-forward
    /// stops at live.</summary>
    private static void SeekLive(IExoPlayer p, long deltaMs)
    {
        var duration = p.Duration;
        if (duration <= 0) return;
        var target = p.CurrentPosition + deltaMs;
        if (target >= duration - LiveOffsetMs) p.SeekToDefaultPosition();
        else p.SeekTo(Math.Max(0, target));
    }

    private static string FormatTime(long ms)
    {
        var totalSeconds = ms / 1000;
        var h = totalSeconds / 3600;
        var m = totalSeconds % 3600 / 60;
        var s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }

    /// <summary>Tear the native player down and close the page's player panel with it.</summary>
    private void CloseNativePlayer()
    {
        _overlayHandler.RemoveCallbacksAndMessages(null);
        if (_nativeOverlay is { } ov)
        {
            _root.RemoveView(ov);
            _nativeOverlay = null;
            _overlayTitle = null;
            _overlayTime = null;
            _overlayProgress = null;
        }
        if (_playerView is { } pv) { _root.RemoveView(pv); _playerView = null; }
        if (_player is { } p) { p.Release(); _player = null; }

        if (!_nativePlayerActive) return;
        if (_mosaicId is { } mosaic)
            _ = Task.Run(() => Server.MultiView.ForwardAsync("POST", $"/api/mosaic/{mosaic}/stop", null));
        _mosaicId = null;
        _mosaicLabels = [];
        _nativePlayerActive = false;
        _nativeLive = false;
        _nativeErrorRetries = 0;
        _tuneGeneration++;
        _nativePath = "";
        _targetPath = null;
        _tuningPath = null;
        SetKeepScreenOn(false);
        ClosePagePlayer();
        _webView.EvaluateJavascript("window.__tvNativeTakeover = false;", null);
    }

    /// <summary>app.js closes its player panel on Escape.</summary>
    private void ClosePagePlayer() => _webView.EvaluateJavascript(
        "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));", null);

    /// <summary>Closes the player at the end of a recording, and clears the retry count once a
    /// live stream is playing again.</summary>
    private sealed class PlayerListener(MainActivity activity) : Java.Lang.Object, IPlayerListener
    {
        public void OnPlaybackStateChanged(int playbackState)
        {
            if (playbackState == BasePlayer.InterfaceConsts.StateEnded) activity.CloseNativePlayer();
            else if (playbackState == BasePlayer.InterfaceConsts.StateReady) activity._nativeErrorRetries = 0;
        }

        public void OnPlayerError(PlaybackException? error) => activity.OnNativePlayerError();

        // Multi-view: the audio tracks appear only once the stream has loaded.
        public void OnTracksChanged(Tracks? tracks) => activity.ApplyMosaicAudio();
    }

    // ------------------------------------------------------------------------------ remote

    // The Fire TV remote does not reliably send repeat events while a button is held, so arrows
    // run their own repeat: a long first delay (a normal press takes 150-300ms to release, which
    // must not count as a hold) and then a steady cadence.
    private const int InitialRepeatDelayMs = 400;
    private const int RepeatIntervalMs = 100;

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is null) return base.DispatchKeyEvent(e);

        if (e.Action == KeyEventActions.Down)
        {
            if (IsDirectional(e.KeyCode) || (IsCenterKey(e.KeyCode) && !_nativePlayerActive))
            {
                // Center is pumped too while the page has focus: nav.js recognises a held Center
                // from the repeats and treats it as a long press.
                if (_heldKey != e.KeyCode)
                {
                    _heldKey = e.KeyCode;
                    _holdTicks = 0;
                    _repeatHandler.RemoveCallbacksAndMessages(null);
                    RepeatTick();
                }
                return true;
            }
            if (e.RepeatCount == 0 && HandleOneShot(e.KeyCode)) return true;
            if (_nativePlayerActive) return true;
        }
        else if (e.Action == KeyEventActions.Up && e.KeyCode == _heldKey)
        {
            _heldKey = null;
            _repeatHandler.RemoveCallbacksAndMessages(null);
            // nav.js clicks on release, so the page needs the matching key-up.
            if (!_nativePlayerActive) SendKeyToPage(e.KeyCode, KeyEventActions.Up, 0);
            return true;
        }

        // While the native player is up every key-down is handled here, so its key-up must not reach
        // the page either: nav.js would "click" whatever card is still focused behind the player.
        if (_nativePlayerActive && e.Action == KeyEventActions.Up) return true;

        return base.DispatchKeyEvent(e);
    }

    /// <summary>Hand a key to the page. Repeat counts become KeyboardEvent.repeat, which nav.js
    /// uses to tell a tap from a hold.</summary>
    private void SendKeyToPage(Keycode key, KeyEventActions action, int repeatCount)
    {
        var now = SystemClock.UptimeMillis();
        using var ev = new KeyEvent(now, now, action, key, repeatCount);
        _webView.DispatchKeyEvent(ev);
    }

    private void RepeatTick()
    {
        if (_heldKey is not { } key) return;
        PerformDirectional(key, _holdTicks);
        var delay = _holdTicks == 0 ? InitialRepeatDelayMs : RepeatIntervalMs;
        _holdTicks++;
        _repeatHandler.PostDelayed(RepeatTick, delay);
    }

    private void PerformDirectional(Keycode key, int holdTicks)
    {
        if (!_nativePlayerActive)
        {
            SendKeyToPage(key, KeyEventActions.Down, holdTicks);
            return;
        }

        if (_mosaicId is not null)
        {
            if (holdTicks == 0 && key == Keycode.DpadLeft) StepMosaicPane(-1);
            else if (holdTicks == 0 && key == Keycode.DpadRight) StepMosaicPane(+1);
            return;
        }

        if (_nativeLive)
        {
            // Up/Down change channel; Left/Right rewind and fast-forward - but not while a channel
            // change is pending, which would seek the channel being left.
            if (key == Keycode.DpadUp) StepChannel(+1);
            else if (key == Keycode.DpadDown) StepChannel(-1);
            else if (_targetPath is null && _player is { } lp)
                SeekLive(lp, (key == Keycode.DpadLeft ? -1 : 1) * SeekSeconds(holdTicks) * 1000L);
            return;
        }

        if (_player is not { } p) return;
        if (key == Keycode.DpadLeft) p.SeekTo(Math.Max(0, p.CurrentPosition - SeekSeconds(holdTicks) * 1000L));
        else if (key == Keycode.DpadRight) p.SeekTo(p.CurrentPosition + SeekSeconds(holdTicks) * 1000L);
    }

    // 10s on the first press, 5s more per repeat while held, capped at a minute.
    private static int SeekSeconds(int repeats) => Math.Min(10 + repeats * 5, 60);

    private bool HandleOneShot(Keycode key)
    {
        if (_nativePlayerActive)
        {
            if (IsCenterKey(key) && _mosaicId is not null)
            {
                ShowMosaicBanner();   // a live composite has nothing to pause back into
                return true;
            }
            if (IsCenterKey(key))
            {
                // Mid channel-change: Center means "go there now" rather than waiting.
                if (_nativeLive && _targetPath is { } pending)
                {
                    if (_tuningPath != pending) _ = TuneAsync(pending, ++_tuneGeneration, 0);
                    return true;
                }
                if (_player is { } p)
                {
                    if (p.PlayWhenReady) { p.Pause(); SetNativeTitleVisible(true); }
                    else { p.Play(); SetNativeTitleVisible(false); }
                }
                return true;
            }
            if (key == Keycode.Back) CloseNativePlayer();
            return true;
        }

        if (key != Keycode.Back) return false;

        // Back must never leave the app while the player panel (still tuning, or showing an error)
        // or a details sheet is open: close it instead. app.js closes either on Escape.
        if (_playerModalOpen)
        {
            ClosePagePlayer();
            return true;
        }
        if (_webView.CanGoBack()) { _webView.GoBack(); return true; }
        return false;
    }

    // ------------------------------------------------------------------------------ plumbing

    private int Dp(int value) => (int)(value * _density + 0.5f);

    private void HideSystemBars()
    {
#pragma warning disable CA1422, CS0618
        Window!.DecorView.SystemUiVisibility = (StatusBarVisibility)(
            SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation |
            SystemUiFlags.LayoutFullscreen | SystemUiFlags.HideNavigation |
            SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky);
#pragma warning restore CA1422, CS0618
    }

    // The Fire TV screensaver only knows about remote presses, not video; keep the screen on
    // while something plays.
    private void SetKeepScreenOn(bool on)
    {
        if (on) Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
        else Window!.ClearFlags(WindowManagerFlags.KeepScreenOn);
    }

    /// <summary>What the page script can call.</summary>
    private sealed class Bridge(MainActivity activity) : Java.Lang.Object
    {
        [JavascriptInterface]
        [Export("playNative")]
        public void PlayNative(string url, string title, string subtitle, double position, bool live, string path) =>
            activity.RunOnUiThread(() =>
            {
                try { activity.ShowNativePlayer(url, title, subtitle, position, live, path); }
                catch (Exception ex)
                {
                    Toast.MakeText(activity, $"Playback error: {ex.Message}", ToastLength.Long)!.Show();
                    activity.CloseNativePlayer();
                }
            });

        [JavascriptInterface]
        [Export("playMosaic")]
        public void PlayMosaic(string url, string id, string channelsJson) =>
            activity.RunOnUiThread(() =>
            {
                try { activity.ShowMosaicPlayer(url, id, channelsJson); }
                catch (Exception ex)
                {
                    Toast.MakeText(activity, $"Multi-view error: {ex.Message}", ToastLength.Long)!.Show();
                    activity.CloseNativePlayer();
                }
            });

        [JavascriptInterface]
        [Export("playerModalState")]
        public void PlayerModalState(bool open) => activity.RunOnUiThread(() => activity._playerModalOpen = open);
    }

    private sealed class PageClient : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(AWebView? view, IWebResourceRequest? request) => false;

        public override void OnPageFinished(AWebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            view?.EvaluateJavascript(PageScript, null);
        }
    }

    private sealed class ChromeClient(Action onPageReady) : WebChromeClient
    {
        public override void OnProgressChanged(AWebView? view, int newProgress)
        {
            base.OnProgressChanged(view, newProgress);
            if (newProgress >= 100) onPageReady();
        }
    }
}
