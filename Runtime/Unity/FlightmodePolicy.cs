namespace FlightmodeSdk
{
    // Client-side operational policy, kept in one place.
    static class FlightmodePolicy
    {
        public const float SyncIntervalSeconds = 900f;
        public const float StaleRetrySeconds = 60f;
        public const float PromptRetrySeconds = 5f;
        public const int HttpTimeoutSeconds = 20;
        public const float ConnectivityPollSeconds = 1f;
        // A loss of signal makes the player offline audience once it lasts this long; airplane mode does at once.
        public const float OfflineDwellSeconds = 180f;
        // Ad hosts every common DNS ad blocker sinkholes. All of them must be blocked to count.
        public static readonly string[] AdBlockProbeHosts = { "googleads.g.doubleclick.net", "pagead2.googlesyndication.com" };
        public const int ReadyTimeoutMs = 5000;
        public const int BannerWidthDp = 320;
        public const int BannerHeightDp = 50;
        // The interstitial close button appears once this share of the creative's duration has played.
        public const float CloseAfterShare = 0.85f;
    }
}
