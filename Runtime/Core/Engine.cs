using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FlightmodeSdk
{
    public sealed class HttpResult
    {
        public int Status;
        public byte[] Body;
    }

    // Network access is injected so the engine runs on UnityWebRequest in games and HttpClient in tests.
    public interface IHttpTransport
    {
        Task<HttpResult> PostJsonAsync(string url, string json);
        Task<HttpResult> GetAsync(string url);
    }

    public sealed class EngineSettings
    {
        public string AppKey;
        public string Endpoint;
        public IReadOnlyList<FlightKey> FlightKeys;
        public string Platform;
        public string Locale;
        public string SdkVersion;
        public string DataDirectory;
        // Only set with the user's consent; uploaded as a salted SHA-256 to count unique accounts.
        public string AccountId;
    }

    public sealed class SyncOutcome
    {
        public bool Reached;
        public int Acked;
        public int Rejected;
        public bool NewFlight;
        public string Error;
    }

    // One play of a creative. Close it exactly once with the observed outcome.
    public sealed class AdSession
    {
        internal Engine Engine;
        internal bool Finished;
        public FlightPlacement Placement;
        public CreativeVariant Variant;
        public string FallbackReason;
        public string Directory;
        public string EntryPath => Path.Combine(Directory, Variant.Entry);
        public int DurationMs => Math.Min(Variant.DurationMs, Placement.MaxDurationMs);

        public string PosterPath(string slot)
        {
            return Variant.Posters.TryGetValue(slot, out var sha) ? Engine.FilePath(sha) : null;
        }

        public void Finish(long viewMs, bool closedByUser, bool completed, int ctaAttempts)
        {
            if (Finished) throw new InvalidOperationException("ad session already finished");
            Finished = true;
            Engine.Record(this, viewMs, closedByUser, completed, ctaAttempts);
        }
    }

    public sealed class Engine
    {
        public const string Version = "0.1.10";
        const long DayMs = 86_400_000;

        readonly EngineSettings _settings;
        readonly IHttpTransport _http;
        readonly Func<long> _wallClock;
        readonly Rotator _rotator = new Rotator();
        readonly Dictionary<string, int> _sessionCounts = new Dictionary<string, int>();
        // Play time of this session when each placement's last interstitial closed.
        readonly Dictionary<string, long> _interstitialClosedAt = new Dictionary<string, long>();
        long _playMs;
        readonly HashSet<string> _readyCreatives = new HashSet<string>();
        string _installId;
        string _installKey;
        string _flightEnvelope;
        Flight _flight;
        FlightClock _clock = new FlightClock();
        Ledger _ledger = new Ledger();
        bool _syncing;

        public Engine(EngineSettings settings, IHttpTransport http, Func<long> wallClock)
        {
            if (string.IsNullOrEmpty(settings.AppKey)) throw new ArgumentException("AppKey is required");
            if (string.IsNullOrEmpty(settings.Endpoint)) throw new ArgumentException("Endpoint is required");
            if (settings.FlightKeys == null || settings.FlightKeys.Count == 0) throw new ArgumentException("at least one flight key is required");
            if (string.IsNullOrEmpty(settings.DataDirectory)) throw new ArgumentException("DataDirectory is required");
            _settings = settings;
            _http = http;
            _wallClock = wallClock;
            System.IO.Directory.CreateDirectory(FilesDirectory);
            Load();
        }

        public string InstallId => _installId;
        public Flight Flight => _flight;
        public Ledger Ledger => _ledger;
        public FlightClock Clock => _clock;
        string StatePath => Path.Combine(_settings.DataDirectory, "state.json");
        string FilesDirectory => Path.Combine(_settings.DataDirectory, "files");
        string CreativesDirectory => Path.Combine(_settings.DataDirectory, "creatives");

        internal string FilePath(string sha256) => Path.Combine(FilesDirectory, sha256);

        public FlightState State
        {
            get
            {
                if (_flight == null) return FlightState.None;
                return _clock.IsExpired(_flight.ValidityMs, _wallClock()) ? FlightState.Expired : FlightState.Fresh;
            }
        }

        // Called by the host every frame or on a timer with the monotonic time since the last call.
        public void Tick(long monotonicDeltaMs)
        {
            _clock.Advance(_wallClock(), monotonicDeltaMs);
            if (monotonicDeltaMs > 0) _playMs += monotonicDeltaMs;
        }

        void Load()
        {
            if (!File.Exists(StatePath))
            {
                _installId = Crypto.NewInstallId();
                Save();
                return;
            }
            var state = Json.Parse(File.ReadAllText(StatePath, Encoding.UTF8));
            _installId = Read.Str(state, "installId");
            _installKey = state["installKey"]?.StringValue;
            _ledger = Ledger.FromJson(Read.Node(state, "ledger"));
            var envelope = state["flightEnvelope"]?.StringValue;
            if (envelope == null) return;
            try
            {
                var flight = ParseFlight(envelope);
                if (!FilesPresent(flight)) return;
                _flightEnvelope = envelope;
                _flight = flight;
                _clock = FlightClock.FromJson(Read.Node(state, "clock"));
                Materialize(flight);
            }
            catch (Exception error) when (error is SignatureException || error is FormatException)
            {
                _flightEnvelope = null;
                _flight = null;
            }
        }

        public void Save()
        {
            var state = new Dictionary<string, object>
            {
                ["installId"] = _installId,
                ["installKey"] = _installKey,
                ["flightEnvelope"] = _flightEnvelope,
                ["clock"] = _clock.ToJson(),
                ["ledger"] = _ledger.ToJson()
            };
            WriteAtomic(StatePath, Encoding.UTF8.GetBytes(Json.Stringify(state)));
        }

        static void WriteAtomic(string path, byte[] bytes)
        {
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        Flight ParseFlight(string envelopeJson)
        {
            var payload = Crypto.VerifyFlightEnvelope(Json.Parse(envelopeJson), _settings.FlightKeys);
            var flight = Flight.Parse(Json.Parse(Encoding.UTF8.GetString(payload)));
            if (flight.InstallId != _installId) throw new FormatException("flight was issued to another install");
            return flight;
        }

        bool FilesPresent(Flight flight)
        {
            foreach (var creative in flight.Creatives)
            {
                foreach (var file in creative.Files) if (!File.Exists(FilePath(file.Sha256))) return false;
            }
            return true;
        }

        string AccountHash()
        {
            if (string.IsNullOrEmpty(_settings.AccountId)) return null;
            return Crypto.Sha256Hex(Encoding.UTF8.GetBytes(_settings.AppKey + ":" + _settings.AccountId));
        }

        public async Task<SyncOutcome> SyncAsync()
        {
            if (_syncing) return new SyncOutcome { Error = "sync already running" };
            _syncing = true;
            try
            {
                if (_installKey == null)
                {
                    var registration = await PostSync(new List<object>());
                    if (registration.Error != null) return registration.Outcome;
                    var key = registration.Body["installKey"]?.StringValue;
                    if (key == null) return new SyncOutcome { Reached = true, Error = "server did not issue an install key" };
                    _installKey = key;
                    Save();
                    return await Apply(registration.Body, 0, 0);
                }
                if (_flight != null) _ledger.Seal(_installId, _installKey, _flight.FlightId, _flight.ExpiredPolicy, _wallClock(), AccountHash());
                Save();
                var batches = new List<object>();
                foreach (var pending in _ledger.Pending) batches.Add(Json.Parse(pending.Envelope));
                var response = await PostSync(batches);
                if (response.Error != null) return response.Outcome;
                var settled = new HashSet<long>();
                var acked = 0;
                var rejected = 0;
                foreach (var seq in Read.Arr(response.Body, "acks"))
                {
                    settled.Add((long)seq.NumberValue);
                    acked++;
                }
                foreach (var entry in Read.Arr(response.Body, "rejected"))
                {
                    settled.Add(Read.Long(entry, "seq"));
                    rejected++;
                }
                _ledger.Settle(settled);
                _ledger.PruneDays(Ledger.DayOf(_wallClock() - 2 * DayMs));
                Save();
                return await Apply(response.Body, acked, rejected);
            }
            finally
            {
                _syncing = false;
            }
        }

        sealed class SyncResponse
        {
            public JsonNode Body;
            public string Error;
            public SyncOutcome Outcome;
        }

        async Task<SyncResponse> PostSync(List<object> batches)
        {
            var request = new Dictionary<string, object>
            {
                ["protocol"] = 1,
                ["appKey"] = _settings.AppKey,
                ["installId"] = _installId,
                ["platform"] = _settings.Platform,
                ["locale"] = _settings.Locale,
                ["sdkVersion"] = _settings.SdkVersion,
                ["batches"] = batches
            };
            if (_flight != null && _installKey != null)
            {
                request["currentFlightId"] = _flight.FlightId;
                request["currentFlightExpiresAt"] = _clock.ReceivedWallMs + _flight.ValidityMs;
            }
            HttpResult result;
            try
            {
                result = await _http.PostJsonAsync(_settings.Endpoint.TrimEnd('/') + "/v1/sync", Json.Stringify(request));
            }
            catch (Exception error)
            {
                return new SyncResponse { Error = error.Message, Outcome = new SyncOutcome { Error = error.Message } };
            }
            if (result.Status != 200)
            {
                var message = "sync returned HTTP " + result.Status;
                return new SyncResponse { Error = message, Outcome = new SyncOutcome { Reached = result.Status > 0, Error = message } };
            }
            return new SyncResponse { Body = Json.Parse(Encoding.UTF8.GetString(result.Body)) };
        }

        async Task<SyncOutcome> Apply(JsonNode body, int acked, int rejected)
        {
            var outcome = new SyncOutcome { Reached = true, Acked = acked, Rejected = rejected };
            var envelopeNode = body["flight"];
            if (envelopeNode == null) return outcome;
            var envelope = Json.Stringify(envelopeNode);
            Flight flight;
            try
            {
                flight = ParseFlight(envelope);
            }
            catch (Exception error) when (error is SignatureException || error is FormatException)
            {
                outcome.Error = "rejected flight: " + error.Message;
                return outcome;
            }
            var missing = await DownloadFiles(flight);
            if (missing != null)
            {
                outcome.Error = missing;
                return outcome;
            }
            var replaced = _flight == null || _flight.FlightId != flight.FlightId;
            _flight = flight;
            _flightEnvelope = envelope;
            _clock.Start(_wallClock());
            Materialize(flight);
            Save();
            if (replaced) CollectGarbage(flight);
            outcome.NewFlight = replaced;
            return outcome;
        }

        // Downloads every missing file, verifying its SHA-256, before the flight replaces the current one.
        async Task<string> DownloadFiles(Flight flight)
        {
            var wanted = new Dictionary<string, long>();
            foreach (var creative in flight.Creatives)
            {
                foreach (var file in creative.Files) wanted[file.Sha256] = file.Size;
                foreach (var sha in creative.Posters.Values) wanted[sha] = -1;
            }
            foreach (var pair in wanted)
            {
                if (File.Exists(FilePath(pair.Key))) continue;
                HttpResult result;
                try
                {
                    result = await _http.GetAsync(_settings.Endpoint.TrimEnd('/') + "/v1/files/" + pair.Key);
                }
                catch (Exception error)
                {
                    return "download failed: " + error.Message;
                }
                if (result.Status != 200) return "download returned HTTP " + result.Status;
                if (Crypto.Sha256Hex(result.Body) != pair.Key || (pair.Value >= 0 && result.Body.LongLength != pair.Value))
                {
                    return "downloaded file does not match its hash";
                }
                WriteAtomic(FilePath(pair.Key), result.Body);
            }
            return null;
        }

        // Lays out each variant as a directory tree the WebView can load with relative paths.
        void Materialize(Flight flight)
        {
            _readyCreatives.Clear();
            foreach (var creative in flight.Creatives)
            {
                var root = Path.Combine(CreativesDirectory, creative.DirectoryName);
                var marker = Path.Combine(root, ".complete");
                if (!File.Exists(marker))
                {
                    if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
                    foreach (var file in creative.Files)
                    {
                        var target = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
                        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(FilePath(file.Sha256), target, true);
                    }
                    File.WriteAllText(marker, "");
                }
                _readyCreatives.Add(creative.CreativeId);
            }
        }

        void CollectGarbage(Flight flight)
        {
            var keepFiles = new HashSet<string>();
            var keepDirectories = new HashSet<string>();
            foreach (var creative in flight.Creatives)
            {
                foreach (var file in creative.Files) keepFiles.Add(file.Sha256);
                foreach (var sha in creative.Posters.Values) keepFiles.Add(sha);
                keepDirectories.Add(creative.DirectoryName);
            }
            foreach (var path in System.IO.Directory.GetFiles(FilesDirectory))
            {
                if (!keepFiles.Contains(Path.GetFileName(path))) File.Delete(path);
            }
            if (!System.IO.Directory.Exists(CreativesDirectory)) return;
            foreach (var path in System.IO.Directory.GetDirectories(CreativesDirectory))
            {
                if (!keepDirectories.Contains(Path.GetFileName(path))) System.IO.Directory.Delete(path, true);
            }
        }

        bool UnderCaps(FlightPlacement placement)
        {
            _sessionCounts.TryGetValue(placement.PlacementId, out var session);
            if (placement.CapPerSession > 0 && session >= placement.CapPerSession) return false;
            if (placement.Format == "interstitial" && placement.CooldownMs > 0
                && _interstitialClosedAt.TryGetValue(placement.PlacementId, out var closedAt)
                && _playMs - closedAt < placement.CooldownMs) return false;
            return placement.CapPerDay == 0 || _ledger.ImpressionsOn(Ledger.DayOf(_wallClock()), placement.PlacementId) < placement.CapPerDay;
        }

        public bool IsReady(string placementId)
        {
            var placement = _flight?.Placement(placementId);
            if (placement == null || !UnderCaps(placement)) return false;
            foreach (var entry in placement.Creatives) if (_readyCreatives.Contains(entry.CreativeId)) return true;
            return false;
        }

        // Picks the next creative for a placement, or null when nothing can play
        // (no flight, unknown placement, frequency cap or cooldown).
        public AdSession Begin(string placementId, FallbackReason reason)
        {
            var placement = _flight?.Placement(placementId);
            if (placement == null || !UnderCaps(placement)) return null;
            var creativeId = _rotator.Next(placement, id => _readyCreatives.Contains(id));
            if (creativeId == null) return null;
            var variant = _flight.Creative(creativeId);
            return new AdSession
            {
                Engine = this,
                Placement = placement,
                Variant = variant,
                FallbackReason = FallbackReasons.Wire(reason),
                Directory = Path.Combine(CreativesDirectory, variant.DirectoryName)
            };
        }

        internal void Record(AdSession session, long viewMs, bool closedByUser, bool completed, int ctaAttempts)
        {
            _sessionCounts.TryGetValue(session.Placement.PlacementId, out var count);
            _sessionCounts[session.Placement.PlacementId] = count + 1;
            if (session.Placement.Format == "interstitial") _interstitialClosedAt[session.Placement.PlacementId] = _playMs;
            _ledger.Record(new LedgerRow
            {
                Day = Ledger.DayOf(_wallClock()),
                PlacementId = session.Placement.PlacementId,
                CreativeId = session.Variant.CreativeId,
                VariantKey = session.Variant.VariantKey,
                FallbackReason = session.FallbackReason,
                Impressions = 1,
                ViewMs = Math.Max(0, viewMs),
                Closes = closedByUser ? 1 : 0,
                CtaAttempts = Math.Max(0, ctaAttempts),
                Completions = completed ? 1 : 0
            });
            Save();
        }
    }
}
