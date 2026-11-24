using System;
using System.Globalization;
using System.IO;
using System.Net;
using UnityEngine;

namespace FlightmodeSdk
{
    // Drives the engine from the Unity player loop: play-time clock, connectivity-triggered sync,
    // the audience (offline by choice, ad blocker), pause handling, banner rotation and native WebView callbacks.
    [AddComponentMenu("")]
    sealed class FlightmodeRunner : MonoBehaviour
    {
        internal const string ObjectName = "__Flightmode";
        const string InterstitialSlot = "interstitial";
        const string BannerSlot = "banner";

        internal Engine Engine;
        ICreativeSurface _interstitial;
        ICreativeSurface _banner;
        ConnectPrompt _prompt;
        // Links tapped offline that need the internet; each opens once the device is online, and only
        // while its ad still shows. The banner's waits at most BannerConnectSeconds.
        string _interstitialLink;
        string _bannerLink;
        float _bannerLinkSeconds;
        bool _bannerShowsConnect;
        // The game asked to replace the banner while it showed the connect layer. The banner stays
        // until its link opens or the wait ends, so the player sees what they tapped open.
        bool _bannerHideDeferred;
        AudienceTracker _audience;
        string _endpointHost;
        float _sincePoll;
        float _sinceSync;
        bool _syncing;
        bool _paused;
        double _tickResidualMs;
        BannerRequest _bannerWanted;
        AdSession _bannerSession;
        float _bannerSeconds;

        sealed class BannerRequest
        {
            public string PlacementId;
            public BannerPosition Position;
            public FallbackReason Reason;
        }

        internal void Setup(FlightmodeOptions options)
        {
            if (options.Endpoint != FlightmodePolicy.Endpoint && options.FlightKeysJson == FlightmodePolicy.FlightKeysJson)
                throw new ArgumentException("a custom Endpoint needs the FlightKeysJson of that server");
            var settings = new EngineSettings
            {
                AppKey = options.AppKey,
                Endpoint = options.Endpoint,
                FlightKeys = FlightKey.ParseList(options.FlightKeysJson),
                Platform = Platform(),
                Locale = Locales.Normalize(string.IsNullOrEmpty(options.Locale) ? DeviceLocale() : options.Locale),
                SdkVersion = Engine.Version,
                DataDirectory = Path.Combine(Application.persistentDataPath, "flightmode"),
                AccountId = options.AccountId
            };
            Engine = new Engine(settings, new UnityHttpTransport(FlightmodePolicy.HttpTimeoutSeconds), () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (NativeWebViewSurface.Supported)
            {
                _interstitial = new NativeWebViewSurface(InterstitialSlot, ObjectName);
                _banner = new NativeWebViewSurface(BannerSlot, ObjectName);
            }
            else
            {
                _interstitial = new PosterSurface(transform);
                _banner = new PosterSurface(transform);
            }
            _endpointHost = new Uri(options.Endpoint).Host;
            _audience = new AudienceTracker((long)(FlightmodePolicy.OfflineDwellSeconds * 1000f));
            PollConnectivity();
            if (Online) Sync();
        }

        static string Platform()
        {
#if UNITY_IOS
            return "ios";
#elif UNITY_ANDROID
            return "android";
#elif UNITY_WEBGL
            return "web";
#else
            return "desktop";
#endif
        }

        static string DeviceLocale()
        {
            if (NativeWebViewSurface.Supported) return NativeWebViewSurface.DeviceLocale();
            var culture = CultureInfo.CurrentUICulture.Name;
            if (!string.IsNullOrEmpty(culture)) return culture;
            var language = Application.systemLanguage.ToString();
            foreach (var neutral in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                if (neutral.EnglishName == language) return neutral.TwoLetterISOLanguageName;
            }
            throw new InvalidOperationException("cannot resolve the device locale; set FlightmodeOptions.Locale");
        }

        internal bool Online => _audience.Online;

        internal Audience Audience => _audience.Audience;

        // Observes the network now; returns true when it just came back.
        bool PollConnectivity()
        {
            _sincePoll = 0f;
            var wasOnline = _audience.Online;
            _audience.Observe(DeviceConnectivity.Read(), (long)(Time.realtimeSinceStartupAsDouble * 1000.0));
            return _audience.Online && !wasOnline;
        }

        internal async void Sync()
        {
            if (_syncing) return;
            _syncing = true;
            _sinceSync = 0f;
            try
            {
                var outcome = await Engine.SyncAsync();
                if (outcome.Error != null) Debug.LogWarning("Flightmode sync: " + outcome.Error);
                var blocked = await AdBlockProbe.RunAsync(Dns.GetHostAddressesAsync, _endpointHost, FlightmodePolicy.AdBlockProbeHosts);
                if (blocked.HasValue) _audience.AdBlocked = blocked.Value;
                if (_prompt != null && Engine.State == FlightState.Fresh) _prompt.Close();
                Flightmode.RaiseSynced(outcome);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
            finally
            {
                _syncing = false;
            }
        }

        void Update()
        {
            var delta = Time.unscaledDeltaTime;
            _tickResidualMs += delta * 1000.0;
            var wholeMs = (long)_tickResidualMs;
            _tickResidualMs -= wholeMs;
            Engine.Tick(wholeMs);
            (_interstitial as PosterSurface)?.Tick(delta);
            (_banner as PosterSurface)?.Tick(delta);

            if (_bannerLink != null && !_paused)
            {
                _bannerLinkSeconds += delta;
                if (_bannerLinkSeconds >= FlightmodePolicy.BannerConnectSeconds) DropBannerLink();
            }
            if (_bannerSession != null && !_paused && !_bannerShowsConnect)
            {
                _bannerSeconds += delta;
                if (_bannerSeconds * 1000f >= _bannerSession.Placement.RotationMs) _banner.Hide();
            }

            _sincePoll += delta;
            if (_sincePoll >= FlightmodePolicy.ConnectivityPollSeconds && PollConnectivity()) Sync();
            _sinceSync += delta;
            if (!Online) return;
            OpenWaitingLinks();
            var interval = _prompt != null ? FlightmodePolicy.PromptRetrySeconds
                : Engine.State == FlightState.Fresh ? FlightmodePolicy.SyncIntervalSeconds : FlightmodePolicy.StaleRetrySeconds;
            if (_sinceSync >= interval) Sync();
        }

        void OnApplicationPause(bool paused)
        {
            _paused = paused;
            _interstitial.SetPaused(paused);
            _banner.SetPaused(paused);
            if (paused) Engine.Save();
            else if (PollConnectivity() || Online) Sync();
        }

        void OnApplicationQuit()
        {
            Engine.Save();
        }

        void OpenLink(AdSession session, ICreativeSurface surface, string linkId, Action attempted)
        {
            if (!session.Variant.Links.TryGetValue(linkId, out var url)) return;
            attempted();
            if (Online || !session.Variant.OnlineLinks.Contains(linkId))
            {
                OpenUrl(surface, url);
                return;
            }
            if (surface == _banner)
            {
                _bannerLink = url;
                _bannerLinkSeconds = 0f;
                _bannerShowsConnect = true;
            }
            else _interstitialLink = url;
            surface.SetConnect(session.Variant.Strings[CreativeVariant.ConnectString]);
        }

        void OpenWaitingLinks()
        {
            if (_interstitialLink != null)
            {
                var url = _interstitialLink;
                _interstitialLink = null;
                _interstitial.SetConnect(null);
                OpenUrl(_interstitial, url);
            }
            if (_bannerLink != null)
            {
                OpenUrl(_banner, _bannerLink);
                DropBannerLink();
            }
        }

        // The banner plays on as before: its layer goes and its rotation resumes.
        void DropBannerLink()
        {
            if (_bannerShowsConnect) _banner.SetConnect(null);
            _bannerShowsConnect = false;
            _bannerLink = null;
            if (!_bannerHideDeferred) return;
            _bannerHideDeferred = false;
            _banner.Hide();
        }

        // The interstitial closes behind the opened link, so the player does not come back to it.
        void OpenUrl(ICreativeSurface surface, string url)
        {
            Application.OpenURL(url);
            if (surface == _interstitial) _interstitial.Hide();
        }

        internal bool InterstitialShowing => _interstitial.IsShowing;

        // The reason the impression is counted under, or null while a short loss of signal lasts:
        // the mediation will have the player back in a moment.
        FallbackReason? Reason(bool direct) => direct ? FallbackReason.Direct : _audience.Fallback;

        internal bool ShowInterstitial(string placementId, bool direct, Action onClosed)
        {
            var reason = Reason(direct);
            if (_interstitial.IsShowing || reason == null) return false;
            var session = Engine.Begin(placementId, reason.Value);
            if (session == null) return false;
            var attempts = 0;
            _interstitial.Show(session, InterstitialSlot, new SurfaceLayout { Fullscreen = true },
                linkId => OpenLink(session, _interstitial, linkId, () => attempts++),
                result =>
                {
                    _interstitialLink = null;
                    session.Finish(result.ViewMs, result.ClosedByUser, result.Completed, attempts);
                    onClosed?.Invoke();
                });
            return true;
        }

        internal bool BannerShowing => _bannerSession != null;

        internal bool ShowBanner(string placementId, BannerPosition position, bool direct)
        {
            var reason = Reason(direct);
            if (reason == null || !Engine.IsReady(placementId)) return false;
            _bannerWanted = new BannerRequest { PlacementId = placementId, Position = position, Reason = reason.Value };
            if (_banner.IsShowing) HideBannerSurface();
            else StartBanner();
            return true;
        }

        // A hide from the game takes the banner away at once, as when the player leaves the screen it
        // sits on, and with it the link it was waiting for.
        internal void HideBanner()
        {
            _bannerWanted = null;
            _banner.Hide();
        }

        void HideBannerSurface()
        {
            if (_bannerShowsConnect) _bannerHideDeferred = true;
            else _banner.Hide();
        }

        // Plays the next banner creative; when it closes (rotation, Hide or a new Show) the next one starts.
        void StartBanner()
        {
            var request = _bannerWanted;
            var session = request == null ? null : Engine.Begin(request.PlacementId, request.Reason);
            if (session == null)
            {
                _bannerWanted = null;
                return;
            }
            _bannerSession = session;
            _bannerSeconds = 0f;
            var attempts = 0;
            var layout = new SurfaceLayout { Position = request.Position, WidthDp = FlightmodePolicy.BannerWidthDp, HeightDp = FlightmodePolicy.BannerHeightDp };
            _banner.Show(session, BannerSlot, layout,
                linkId => OpenLink(session, _banner, linkId, () => attempts++),
                result =>
                {
                    _bannerLink = null;
                    _bannerShowsConnect = false;
                    _bannerHideDeferred = false;
                    _bannerSession = null;
                    session.Finish(result.ViewMs, result.ClosedByUser, result.Completed, attempts);
                    StartBanner();
                });
        }

        internal bool ShowConnectPrompt(string message, bool dismissible, Action onClosed)
        {
            if (_prompt != null) return false;
            _prompt = new ConnectPrompt(transform, message, dismissible, () =>
            {
                _prompt = null;
                onClosed?.Invoke();
            });
            if (Online) Sync();
            return true;
        }

        // Called by the native WebView plugins through UnitySendMessage.
        void OnNativeEvent(string json)
        {
            var node = Json.Parse(json);
            var surface = node["slot"].StringValue == InterstitialSlot ? _interstitial : _banner;
            ((NativeWebViewSurface)surface).OnNativeEvent(node);
        }
    }
}
