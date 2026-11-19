namespace FlightmodeSdk
{
    // Client-side operational policy, kept in one place.
    static class FlightmodePolicy
    {
        // The hosted Flightmode server and its public flight keys. FlightmodeOptions overrides both.
        public const string Endpoint = "https://flightmodeads.com";
        public const string FlightKeysJson = "[{\"kid\":\"kmwggxqgl4728\",\"kty\":\"RSA\",\"n\":\"u0U0L5MkowuUNa3KTlP2xnM63_VDELEhiDO6LuQGULJ916Da35dTRGYaSVZT6Ib80rYyT-kGAuLnLTGJHAkSIBKvovDO9frftmquqSreW6zwvfHB3J4jflIL1vK9QWdlLB_mjFaMPgXx3Pd8FYwwauDd0ycD4jZSYUMZvBWpGt6jB5pQyK1tS4TEaV6yQCDLCQ-xIk8PFYuSscKzgNoCQltQwswtH73ByAUwVV0fs0r8sOw8UxKfBc997s6iLUIiZKI8Ip6UOO4pSfgHm6mnr4q-bmAh9zdR7n6A0BRlU1GinE8r2IS6YGsTsk6lRcuJI7YINdOzgz-nEN7Rwetc3w\",\"e\":\"AQAB\"}]";
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
