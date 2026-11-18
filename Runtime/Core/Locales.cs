using System;
using System.Text.RegularExpressions;

namespace FlightmodeSdk
{
    public static class Locales
    {
        static readonly Regex Language = new Regex("^[A-Za-z]{2,3}$");
        static readonly Regex Region = new Regex("^[A-Za-z]{2}$");

        // Reduces a BCP 47 tag from the OS ("es-419", "zh-Hans-CN", "en_US") to the protocol's
        // language[-REGION] form; script and numeric region subtags are dropped.
        public static string Normalize(string tag)
        {
            var parts = (tag ?? "").Split('-', '_');
            if (!Language.IsMatch(parts[0])) throw new FormatException("not a locale: " + tag);
            var locale = parts[0].ToLowerInvariant();
            for (var i = 1; i < parts.Length; i++)
            {
                if (Region.IsMatch(parts[i])) return locale + "-" + parts[i].ToUpperInvariant();
            }
            return locale;
        }
    }
}
