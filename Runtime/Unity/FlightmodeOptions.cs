namespace FlightmodeSdk
{
    // Values from the publisher's get_sdk_config.
    public sealed class FlightmodeOptions
    {
        public string AppKey;
        public string Endpoint;
        // The flightKeys JSON array, embedded in the app so flights verify offline.
        public string FlightKeysJson;
        // Only with the user's consent: counts unique accounts through a salted hash.
        public string AccountId;
        // Overrides the device locale, for testing translations.
        public string Locale;
    }

    public enum BannerPosition { Top, Bottom }

    // What the game should do while the flight is expired, per the A/B arm the server assigned.
    public enum ExpiredAction { None, Prompt, Block }
}
