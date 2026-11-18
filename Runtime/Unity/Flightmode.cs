using System;
using UnityEngine;

namespace FlightmodeSdk
{
    // Offline ad fallback. Initialize once; show ads where the mediation could not fill.
    public static class Flightmode
    {
        static FlightmodeRunner _runner;

        public static event Action<SyncOutcome> Synced;

        public static bool IsInitialized => _runner != null;

        public static void Initialize(FlightmodeOptions options)
        {
            if (_runner != null) throw new InvalidOperationException("Flightmode is already initialized");
            var go = new GameObject(FlightmodeRunner.ObjectName);
            UnityEngine.Object.DontDestroyOnLoad(go);
            var runner = go.AddComponent<FlightmodeRunner>();
            runner.Setup(options);
            _runner = runner;
        }

        static FlightmodeRunner Runner => _runner != null ? _runner : throw new InvalidOperationException("call Flightmode.Initialize first");

        internal static void RaiseSynced(SyncOutcome outcome) => Synced?.Invoke(outcome);

        public static FlightState FlightState => Runner.Engine.State;

        // Offline: the player cut the network (airplane mode, data denied to the app) or has been
        // without signal past the dwell time. AdBlocked: online behind a DNS ad blocker.
        // The mediation cannot fill either, so the game can come straight to Flightmode.
        public static Audience Audience => Runner.Audience;

        public static ExpiredAction ExpiredAction
        {
            get
            {
                var engine = Runner.Engine;
                if (engine.State != FlightState.Expired) return ExpiredAction.None;
                switch (engine.Flight.ExpiredPolicy)
                {
                    case "rotate": return ExpiredAction.None;
                    case "soft": return ExpiredAction.Prompt;
                    case "hard": return ExpiredAction.Block;
                    default: throw new FormatException("unknown expired policy " + engine.Flight.ExpiredPolicy);
                }
            }
        }

        // Uploads pending metrics and refreshes the flight now, if the device is online.
        public static void SyncNow() => Runner.Sync();

        // Shows the game's localized "connect to continue" message when ExpiredAction asks for it.
        // Prompt can be dismissed; Block closes only once a sync brings a fresh flight.
        // Returns false and does nothing when no prompt is due.
        public static bool ShowConnectPrompt(string message, Action onClosed)
        {
            var action = ExpiredAction;
            if (action == ExpiredAction.None) return false;
            return Runner.ShowConnectPrompt(message, action == ExpiredAction.Prompt, onClosed);
        }

        public static class Interstitial
        {
            public static bool IsShowing => Runner.InterstitialShowing;

            public static bool IsReady(string placementId) => !IsShowing && Runner.Engine.IsReady(placementId);

            // Fallback where the mediation could not fill; the SDK records why. Returns false when nothing
            // can play (no flight, unknown placement, frequency cap, cooldown) or the device just lost signal.
            public static bool Show(string placementId, Action onClosed) => Runner.ShowInterstitial(placementId, false, onClosed);

            // Shown by the game on its own, without asking the mediation first.
            public static bool ShowDirect(string placementId, Action onClosed) => Runner.ShowInterstitial(placementId, true, onClosed);
        }

        public static class Banner
        {
            public static bool IsShowing => Runner.BannerShowing;

            // Shows and rotates banners of the placement until Hide, as a fallback where the mediation
            // could not fill. Returns false when nothing can play or the device just lost signal.
            public static bool Show(string placementId, BannerPosition position) => Runner.ShowBanner(placementId, position, false);

            public static bool ShowDirect(string placementId, BannerPosition position) => Runner.ShowBanner(placementId, position, true);

            public static void Hide() => Runner.HideBanner();
        }
    }
}
