using System;
using System.Collections.Generic;

namespace FlightmodeSdk
{
    public enum FlightState { None, Fresh, Expired }

    public enum FallbackReason { Offline, AdBlocked, NoFill, Direct }

    public static class FallbackReasons
    {
        public static string Wire(FallbackReason reason)
        {
            switch (reason)
            {
                case FallbackReason.Offline: return "offline";
                case FallbackReason.AdBlocked: return "ad_blocked";
                case FallbackReason.NoFill: return "no_fill";
                case FallbackReason.Direct: return "direct";
                default: throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }
    }

    public sealed class FileRef
    {
        public string Path;
        public string Sha256;
        public long Size;
    }

    public sealed class CreativeVariant
    {
        public string CreativeId;
        public int Revision;
        public string VariantKey;
        public string Locale;
        public string Platform;
        public string Entry;
        public int DurationMs;
        public int ProgressSteps;
        public Dictionary<string, string> Strings;
        public Dictionary<string, string> Links;
        // Links that wait for the internet; offline, the host shows Strings["connect"] until it returns.
        public HashSet<string> OnlineLinks;
        public List<FileRef> Files;
        public Dictionary<string, string> Posters;

        public const string ConnectString = "connect";

        public string DirectoryName => CreativeId + "-" + Revision + "-" + VariantKey;
    }

    public sealed class WeightedCreative
    {
        public string CreativeId;
        public int Weight;
    }

    public sealed class FlightPlacement
    {
        public string PlacementId;
        public string Format;
        public int MaxDurationMs;
        public int CloseAfterMs;
        public int RotationMs;
        public int CapPerSession;
        public int CapPerDay;
        public long CooldownMs;
        public List<WeightedCreative> Creatives;
    }

    public sealed class Flight
    {
        public string FlightId;
        public string InstallId;
        public string AppId;
        public string Platform;
        public string Locale;
        public long IssuedAt;
        public long ExpiresAt;
        public string ExpiredPolicy;
        public List<FlightPlacement> Placements;
        public List<CreativeVariant> Creatives;

        public long ValidityMs => ExpiresAt - IssuedAt;

        public FlightPlacement Placement(string placementId)
        {
            foreach (var placement in Placements) if (placement.PlacementId == placementId) return placement;
            return null;
        }

        public CreativeVariant Creative(string creativeId)
        {
            foreach (var creative in Creatives) if (creative.CreativeId == creativeId) return creative;
            return null;
        }

        public static Flight Parse(JsonNode node)
        {
            var flight = new Flight
            {
                FlightId = Read.Str(node, "flightId"),
                InstallId = Read.Str(node, "installId"),
                AppId = Read.Str(node, "appId"),
                Platform = Read.Str(node, "platform"),
                Locale = Read.Str(node, "locale"),
                IssuedAt = Read.Long(node, "issuedAt"),
                ExpiresAt = Read.Long(node, "expiresAt"),
                ExpiredPolicy = Read.Str(node, "expiredPolicy"),
                Placements = new List<FlightPlacement>(),
                Creatives = new List<CreativeVariant>()
            };
            if (Read.Long(node, "protocol") != 1) throw new FormatException("unsupported flight protocol");
            if (flight.ExpiresAt <= flight.IssuedAt) throw new FormatException("flight expires before it is issued");
            foreach (var item in Read.Arr(node, "placements"))
            {
                var placement = new FlightPlacement
                {
                    PlacementId = Read.Str(item, "placementId"),
                    Format = Read.Str(item, "format"),
                    MaxDurationMs = (int)Read.Long(item, "maxDurationMs"),
                    CloseAfterMs = (int)Read.Long(item, "closeAfterMs"),
                    RotationMs = (int)Read.Long(item, "rotationMs"),
                    CapPerSession = (int)Read.Long(item, "capPerSession"),
                    CapPerDay = (int)Read.Long(item, "capPerDay"),
                    CooldownMs = Read.Long(item, "cooldownMs"),
                    Creatives = new List<WeightedCreative>()
                };
                foreach (var entry in Read.Arr(item, "creatives"))
                {
                    placement.Creatives.Add(new WeightedCreative { CreativeId = Read.Str(entry, "creativeId"), Weight = (int)Read.Long(entry, "weight") });
                }
                flight.Placements.Add(placement);
            }
            foreach (var item in Read.Arr(node, "creatives"))
            {
                var variant = new CreativeVariant
                {
                    CreativeId = Read.Str(item, "creativeId"),
                    Revision = (int)Read.Long(item, "revision"),
                    VariantKey = Read.Str(item, "variantKey"),
                    Locale = Read.Str(item, "locale"),
                    Platform = Read.Str(item, "platform"),
                    Entry = Read.Str(item, "entry"),
                    DurationMs = (int)Read.Long(item, "durationMs"),
                    ProgressSteps = (int)Read.Long(item, "progressSteps"),
                    Strings = Read.StringMap(item, "strings"),
                    Links = Read.StringMap(item, "links"),
                    OnlineLinks = new HashSet<string>(),
                    Posters = Read.StringMap(item, "posters"),
                    Files = new List<FileRef>()
                };
                if (item.Has("onlineLinks"))
                {
                    foreach (var id in Read.Arr(item, "onlineLinks"))
                    {
                        if (id.Type != JsonNode.Kind.String || !variant.Links.ContainsKey(id.StringValue)) throw new FormatException("onlineLinks names an unknown link");
                        variant.OnlineLinks.Add(id.StringValue);
                    }
                    if (!variant.Strings.TryGetValue(CreativeVariant.ConnectString, out var connect) || string.IsNullOrWhiteSpace(connect)) throw new FormatException("onlineLinks requires strings." + CreativeVariant.ConnectString);
                }
                foreach (var file in Read.Arr(item, "files"))
                {
                    var path = Read.Str(file, "path");
                    if (path.StartsWith("/", StringComparison.Ordinal) || path.Contains("..")) throw new FormatException("unsafe file path " + path);
                    variant.Files.Add(new FileRef { Path = path, Sha256 = Read.Str(file, "sha256"), Size = Read.Long(file, "size") });
                }
                flight.Creatives.Add(variant);
            }
            return flight;
        }
    }

    static class Read
    {
        public static JsonNode Node(JsonNode node, string key)
        {
            var value = node?[key];
            if (value == null) throw new FormatException("missing " + key);
            return value;
        }

        public static string Str(JsonNode node, string key)
        {
            var value = Node(node, key);
            if (value.Type != JsonNode.Kind.String) throw new FormatException(key + " must be a string");
            return value.StringValue;
        }

        public static long Long(JsonNode node, string key)
        {
            var value = Node(node, key);
            if (value.Type != JsonNode.Kind.Number || value.NumberValue != Math.Truncate(value.NumberValue)) throw new FormatException(key + " must be an integer");
            return (long)value.NumberValue;
        }

        public static List<JsonNode> Arr(JsonNode node, string key)
        {
            var value = Node(node, key);
            if (value.Type != JsonNode.Kind.Array) throw new FormatException(key + " must be an array");
            return value.ArrayValue;
        }

        public static Dictionary<string, string> StringMap(JsonNode node, string key)
        {
            var value = Node(node, key);
            if (value.Type != JsonNode.Kind.Object) throw new FormatException(key + " must be an object");
            var map = new Dictionary<string, string>();
            foreach (var pair in value.ObjectValue)
            {
                if (pair.Value.Type != JsonNode.Kind.String) throw new FormatException(key + "." + pair.Key + " must be a string");
                map[pair.Key] = pair.Value.StringValue;
            }
            return map;
        }
    }
}
