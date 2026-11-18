using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FlightmodeSdk.Tests
{
    sealed class HttpClientTransport : IHttpTransport
    {
        readonly HttpClient _client = new HttpClient();

        public async Task<HttpResult> PostJsonAsync(string url, string json)
        {
            var response = await _client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            return new HttpResult { Status = (int)response.StatusCode, Body = await response.Content.ReadAsByteArrayAsync() };
        }

        public async Task<HttpResult> GetAsync(string url)
        {
            var response = await _client.GetAsync(url);
            return new HttpResult { Status = (int)response.StatusCode, Body = await response.Content.ReadAsByteArrayAsync() };
        }
    }

    sealed class OfflineTransport : IHttpTransport
    {
        public Task<HttpResult> PostJsonAsync(string url, string json) => throw new HttpRequestException("offline");
        public Task<HttpResult> GetAsync(string url) => throw new HttpRequestException("offline");
    }

    static class Program
    {
        static int _failures;

        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "interop") return Interop.Run(args[1]).GetAwaiter().GetResult();
            Run("fixture flight verifies and tampering fails", FixtureFlight);
            Run("batch HMAC matches the JavaScript signer", BatchHmac);
            Run("flight clock resists clock rollback", ClockRollback);
            Run("rotator follows weights and interleaves", RotatorWeights);
            Run("ledger aggregates and seals consecutive signed batches", LedgerSeal);
            Run("engine works offline from scratch and persists the ledger", EngineOffline);
            Run("device locales normalize to the protocol form", LocaleNormalize);
            Run("bridge goes right after the head tag", BridgeInject);
            Run("a short loss of signal is not offline audience, airplane mode is", AudienceOffline);
            Run("ad blocker audience only while online", AudienceAdBlocked);
            Run("ad block probe reads sinkholes, real answers and DNS failures", AdBlockProbeAnswers);
            Console.WriteLine(_failures == 0 ? "all tests passed" : _failures + " test(s) failed");
            return _failures == 0 ? 0 : 1;
        }

        static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("ok - " + name);
            }
            catch (Exception error)
            {
                _failures++;
                Console.WriteLine("not ok - " + name + ": " + error);
            }
        }

        static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        static void AudienceOffline()
        {
            var audience = new AudienceTracker(180_000);
            audience.Observe(Connectivity.Online, 0);
            Assert(audience.Audience == Audience.None && audience.Fallback == FallbackReason.NoFill, "online without blocker is no_fill");
            audience.Observe(Connectivity.Offline, 1_000);
            Assert(audience.Audience == Audience.None && audience.Fallback == null, "a fresh loss of signal shows nothing");
            audience.Observe(Connectivity.Offline, 180_999);
            Assert(audience.Fallback == null, "still inside the dwell time");
            audience.Observe(Connectivity.Offline, 181_000);
            Assert(audience.Audience == Audience.Offline && audience.Fallback == FallbackReason.Offline, "offline past the dwell time");
            audience.Observe(Connectivity.Online, 182_000);
            audience.Observe(Connectivity.Offline, 183_000);
            Assert(audience.Fallback == null, "reconnecting restarts the dwell time");
            audience.Observe(Connectivity.OfflineByUser, 184_000);
            Assert(audience.Fallback == FallbackReason.Offline, "airplane mode counts at once");
            var threw = false;
            try { audience.Observe(Connectivity.Online, 0); } catch (ArgumentException) { threw = true; }
            Assert(threw, "the clock cannot go backwards");
        }

        static void AudienceAdBlocked()
        {
            var audience = new AudienceTracker(0) { AdBlocked = true };
            audience.Observe(Connectivity.Online, 0);
            Assert(audience.Audience == Audience.AdBlocked && audience.Fallback == FallbackReason.AdBlocked, "online behind a blocker");
            audience.Observe(Connectivity.OfflineByUser, 1);
            Assert(audience.Fallback == FallbackReason.Offline, "offline wins over the blocker");
        }

        static void AdBlockProbeAnswers()
        {
            Func<Dictionary<string, object>, Func<string, Task<IPAddress[]>>> dns = answers => host =>
            {
                var answer = answers[host];
                if (answer is SocketError error) throw new SocketException((int)error);
                return Task.FromResult((IPAddress[])answer);
            };
            var routable = new[] { IPAddress.Parse("142.250.0.1") };
            var hosts = new[] { "ads.one", "ads.two" };
            bool? Probe(object one, object two, object control = null) => AdBlockProbe.RunAsync(
                dns(new Dictionary<string, object> { ["api.test"] = control ?? routable, ["ads.one"] = one, ["ads.two"] = two }), "api.test", hosts).GetAwaiter().GetResult();

            Assert(Probe(routable, routable) == false, "ads resolve: no blocker");
            Assert(Probe(new[] { IPAddress.Any }, new[] { IPAddress.IPv6Any, IPAddress.Loopback }) == true, "sinkhole addresses");
            Assert(Probe(SocketError.HostNotFound, new IPAddress[0]) == true, "NXDOMAIN and empty answers");
            Assert(Probe(new[] { IPAddress.Any }, routable) == false, "one ad host resolving rules out a blocker");
            Assert(Probe(new[] { IPAddress.Any }, SocketError.TryAgain) == null, "a failed lookup is inconclusive");
            Assert(Probe(new[] { IPAddress.Any }, new[] { IPAddress.Any }, SocketError.HostNotFound) == null, "DNS that resolves nothing is inconclusive");
        }

        static void LocaleNormalize()
        {
            Assert(Locales.Normalize("es-419") == "es", "numeric region dropped");
            Assert(Locales.Normalize("zh-Hans-CN") == "zh-CN", "script dropped");
            Assert(Locales.Normalize("en_us") == "en-US", "underscore and case");
            Assert(Locales.Normalize("pt") == "pt", "language only");
            var threw = false;
            try { Locales.Normalize(""); } catch (FormatException) { threw = true; }
            Assert(threw, "empty locale rejected");
        }

        static void BridgeInject()
        {
            Assert(Bridge.Inject("<html><HEAD lang=\"x\"><title>", "S") == "<html><HEAD lang=\"x\">S<title>", "after head");
            Assert(Bridge.Inject("<p>no head</p>", "S") == "S<p>no head</p>", "prepended without head");
        }

        static JsonNode Fixture(string name)
        {
            return Json.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)));
        }

        static FlightKey FixtureKey()
        {
            var jwk = Fixture("keys.json")["flightKey"]["publicJwk"];
            return new FlightKey { Kid = jwk["kid"].StringValue, N = jwk["n"].StringValue, E = jwk["e"].StringValue };
        }

        static void FixtureFlight()
        {
            var envelope = Fixture("flight.json")["envelope"];
            var payload = Crypto.VerifyFlightEnvelope(envelope, new[] { FixtureKey() });
            var flight = Flight.Parse(Json.Parse(Encoding.UTF8.GetString(payload)));
            Assert(flight.Creatives[0].VariantKey == "es.ios", "variant key");
            Assert(flight.Creatives[0].Links["store"].StartsWith("https://apps.apple.com"), "platform link");
            Assert(flight.Placement("pl_interstitial").MaxDurationMs == 18000, "placement");

            var tampered = Json.Parse(Json.Stringify(envelope));
            var bytes = Convert.FromBase64String(tampered["payload"].StringValue);
            bytes[10] ^= 1;
            tampered.ObjectValue["payload"] = JsonNode.Str(Convert.ToBase64String(bytes));
            Assert(Throws<SignatureException>(() => Crypto.VerifyFlightEnvelope(tampered, new[] { FixtureKey() })), "tampered payload must fail");
            var otherKey = new FlightKey { Kid = "other", N = FixtureKey().N, E = FixtureKey().E };
            Assert(Throws<SignatureException>(() => Crypto.VerifyFlightEnvelope(envelope, new[] { otherKey })), "unknown key must fail");
        }

        static void BatchHmac()
        {
            var envelope = Fixture("batch.json")["envelope"];
            var key = Fixture("keys.json")["installKey"].StringValue;
            var payload = Convert.FromBase64String(envelope["payload"].StringValue);
            Assert(Crypto.HmacBase64(key, payload) == envelope["sig"].StringValue, "HMAC differs from JavaScript");
        }

        static void ClockRollback()
        {
            const long day = 86_400_000;
            var clock = new FlightClock();
            clock.Start(10 * day);
            clock.Advance(12 * day, 60_000);
            Assert(!clock.IsExpired(7 * day, 12 * day), "fresh after two days");
            clock.Advance(18 * day, 60_000);
            Assert(clock.IsExpired(7 * day, 12 * day), "turning the clock back after expiry keeps it expired");
            var player = new FlightClock();
            player.Start(10 * day);
            player.Advance(10 * day, 7 * day);
            Assert(player.IsExpired(7 * day, 10 * day), "accumulated play time expires a frozen clock");
            var restored = FlightClock.FromJson(Json.Parse(Json.Stringify(clock.ToJson())));
            Assert(restored.MaxSeenWallMs == 18 * day && restored.ElapsedMs == 120_000, "clock round-trips");
        }

        static void RotatorWeights()
        {
            var placement = new FlightPlacement
            {
                PlacementId = "pl",
                Creatives = new List<WeightedCreative> { new WeightedCreative { CreativeId = "a", Weight = 2 }, new WeightedCreative { CreativeId = "b", Weight = 1 } }
            };
            var rotator = new Rotator();
            var picks = Enumerable.Range(0, 30).Select(_ => rotator.Next(placement, id => true)).ToList();
            Assert(picks.Count(id => id == "a") == 20 && picks.Count(id => id == "b") == 10, "2:1 split");
            Assert(string.Concat(picks.Take(6)) == "abaaba", "interleaved: " + string.Concat(picks.Take(6)));
            Assert(rotator.Next(placement, id => id == "b") == "b", "skips unplayable creatives");
        }

        static void LedgerSeal()
        {
            var ledger = new Ledger();
            var key = Convert.ToBase64String(new byte[32]);
            for (var i = 0; i < 3; i++)
            {
                ledger.Record(new LedgerRow { Day = "2027-01-15", PlacementId = "pl", CreativeId = "cr", VariantKey = "es.ios", FallbackReason = "offline", Impressions = 1, ViewMs = 1000, Completions = 1 });
            }
            Assert(ledger.RowCount == 1 && ledger.ImpressionsOn("2027-01-15", "pl") == 3, "aggregates into one row");
            ledger.Seal("install_test_000001", key, "fl_00112233445566778899aabb", "rotate", 1_800_000_000_000, null);
            ledger.Record(new LedgerRow { Day = "2027-01-16", PlacementId = "pl", CreativeId = "cr", VariantKey = "es.ios", FallbackReason = "no_fill", Impressions = 1 });
            ledger.Seal("install_test_000001", key, "fl_00112233445566778899aabb", "rotate", 1_800_000_000_000, null);
            Assert(ledger.Pending.Select(b => b.Seq).SequenceEqual(new long[] { 1, 2 }), "consecutive seqs");
            var first = Json.Parse(ledger.Pending[0].Envelope);
            var payload = Convert.FromBase64String(first["payload"].StringValue);
            Assert(Crypto.HmacBase64(key, payload) == first["sig"].StringValue, "signed");
            Assert(Json.Parse(Encoding.UTF8.GetString(payload))["rows"].ArrayValue[0]["impressions"].NumberValue == 3, "row counts");
            var restored = Ledger.FromJson(Json.Parse(Json.Stringify(ledger.ToJson())));
            restored.Settle(new HashSet<long> { 1 });
            Assert(restored.Pending.Count == 1 && restored.LastSeq == 2, "ledger round-trips and settles");
        }

        static void EngineOffline()
        {
            var directory = Path.Combine(Path.GetTempPath(), "flightmode-engine-" + Guid.NewGuid().ToString("N"));
            try
            {
                var settings = new EngineSettings
                {
                    AppKey = "app_key_offline_test", Endpoint = "http://127.0.0.1:9", FlightKeys = new[] { FixtureKey() },
                    Platform = "android", Locale = "es-AR", SdkVersion = Engine.Version, DataDirectory = directory
                };
                var engine = new Engine(settings, new OfflineTransport(), () => 1_800_000_000_000);
                Assert(engine.State == FlightState.None, "no flight before the first sync");
                Assert(engine.Begin("pl", FallbackReason.Offline) == null, "nothing to show");
                var outcome = engine.SyncAsync().GetAwaiter().GetResult();
                Assert(!outcome.Reached && outcome.Error == "offline", "offline sync reports the failure");
                var installId = engine.InstallId;
                var reloaded = new Engine(settings, new OfflineTransport(), () => 1_800_000_000_000);
                Assert(reloaded.InstallId == installId, "install id persists");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        static bool Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (T)
            {
                return true;
            }
        }
    }

    static class Interop
    {
        // Runs against a live server. The config JSON carries endpoint, appKey, flightKeys,
        // dataDirectory and placementId; prints a JSON summary the Node harness asserts.
        public static async Task<int> Run(string configPath)
        {
            var config = Json.Parse(File.ReadAllText(configPath));
            var keys = FlightKey.ParseList(Json.Stringify(config["flightKeys"]));
            var settings = new EngineSettings
            {
                AppKey = config["appKey"].StringValue,
                Endpoint = config["endpoint"].StringValue,
                FlightKeys = keys,
                Platform = "android",
                Locale = "es-AR",
                SdkVersion = Engine.Version,
                DataDirectory = config["dataDirectory"].StringValue,
                AccountId = "interop-account"
            };
            var engine = new Engine(settings, new HttpClientTransport(), () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var first = await engine.SyncAsync();
            var placementId = config["placementId"].StringValue;
            var shown = 0;
            for (var i = 0; i < 2; i++)
            {
                if (i > 0)
                {
                    if (engine.Begin(placementId, FallbackReason.Offline) != null) throw new Exception("the cooldown let a second interstitial through");
                    engine.Tick(engine.Flight.Placement(placementId).CooldownMs);
                }
                var session = engine.Begin(placementId, FallbackReason.Offline);
                if (session == null) break;
                if (!File.Exists(session.EntryPath)) throw new Exception("entry file missing at " + session.EntryPath);
                session.Finish(session.DurationMs, false, true, i);
                shown++;
            }
            var second = await engine.SyncAsync();
            var summary = new Dictionary<string, object>
            {
                ["state"] = engine.State.ToString(),
                ["firstNewFlight"] = first.NewFlight,
                ["firstError"] = first.Error,
                ["shown"] = shown,
                ["acked"] = second.Acked,
                ["rejected"] = second.Rejected,
                ["pending"] = engine.Ledger.Pending.Count,
                ["variantKey"] = engine.Flight?.Creatives.FirstOrDefault()?.VariantKey
            };
            Console.WriteLine(Json.Stringify(summary));
            return 0;
        }
    }
}
