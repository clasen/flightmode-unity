using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FlightmodeSdk
{
    public sealed class LedgerRow
    {
        public string Day;
        public string PlacementId;
        public string CreativeId;
        public string VariantKey;
        public string FallbackReason;
        public long Impressions;
        public long ViewMs;
        public long Closes;
        public long CtaAttempts;
        public long Completions;

        public string Key => Day + "|" + PlacementId + "|" + CreativeId + "|" + VariantKey + "|" + FallbackReason;
    }

    public sealed class PendingBatch
    {
        public long Seq;
        public string Envelope;
    }

    // Offline counters plus sealed, signed batches waiting for an ack. Rows aggregate
    // per day x placement x creative variant x fallback reason, so storage stays small
    // however long the device stays offline.
    public sealed class Ledger
    {
        public const int MaxRowsPerBatch = 2000;

        readonly Dictionary<string, LedgerRow> _rows = new Dictionary<string, LedgerRow>();
        readonly Dictionary<string, long> _dayImpressions = new Dictionary<string, long>();
        public readonly List<PendingBatch> Pending = new List<PendingBatch>();
        public long LastSeq;

        public int RowCount => _rows.Count;

        public static string DayOf(long wallMs)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(wallMs).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public long ImpressionsOn(string day, string placementId)
        {
            return _dayImpressions.TryGetValue(day + "|" + placementId, out var count) ? count : 0;
        }

        public void Record(LedgerRow delta)
        {
            if (!_rows.TryGetValue(delta.Key, out var row))
            {
                row = new LedgerRow
                {
                    Day = delta.Day,
                    PlacementId = delta.PlacementId,
                    CreativeId = delta.CreativeId,
                    VariantKey = delta.VariantKey,
                    FallbackReason = delta.FallbackReason
                };
                _rows[delta.Key] = row;
            }
            row.Impressions += delta.Impressions;
            row.ViewMs += delta.ViewMs;
            row.Closes += delta.Closes;
            row.CtaAttempts += delta.CtaAttempts;
            row.Completions += delta.Completions;
            var dayKey = delta.Day + "|" + delta.PlacementId;
            _dayImpressions[dayKey] = ImpressionsOn(delta.Day, delta.PlacementId) + delta.Impressions;
        }

        // Moves every open row into signed batches with consecutive sequence numbers.
        public void Seal(string installId, string installKey, string flightId, string expiredPolicy, long nowWallMs, string accountHash)
        {
            if (_rows.Count == 0) return;
            var rows = new List<LedgerRow>(_rows.Values);
            rows.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            for (var start = 0; start < rows.Count; start += MaxRowsPerBatch)
            {
                var chunk = rows.GetRange(start, Math.Min(MaxRowsPerBatch, rows.Count - start));
                var wireRows = new List<object>(chunk.Count);
                foreach (var row in chunk)
                {
                    wireRows.Add(new Dictionary<string, object>
                    {
                        ["day"] = row.Day,
                        ["placementId"] = row.PlacementId,
                        ["creativeId"] = row.CreativeId,
                        ["variantKey"] = row.VariantKey,
                        ["fallbackReason"] = row.FallbackReason,
                        ["impressions"] = row.Impressions,
                        ["viewMs"] = row.ViewMs,
                        ["closes"] = row.Closes,
                        ["ctaAttempts"] = row.CtaAttempts,
                        ["completions"] = row.Completions
                    });
                }
                var seq = LastSeq + 1;
                var batch = new Dictionary<string, object>
                {
                    ["protocol"] = 1,
                    ["installId"] = installId,
                    ["seq"] = seq,
                    ["flightId"] = flightId,
                    ["expiredPolicy"] = expiredPolicy,
                    ["createdAt"] = nowWallMs,
                    ["rows"] = wireRows
                };
                if (accountHash != null) batch["accountHash"] = accountHash;
                var payload = Encoding.UTF8.GetBytes(Json.Stringify(batch));
                var envelope = new Dictionary<string, object>
                {
                    ["payload"] = Convert.ToBase64String(payload),
                    ["sig"] = Crypto.HmacBase64(installKey, payload),
                    ["keyId"] = installId
                };
                Pending.Add(new PendingBatch { Seq = seq, Envelope = Json.Stringify(envelope) });
                LastSeq = seq;
            }
            _rows.Clear();
        }

        // Acked batches are stored server side; rejected ones can never be accepted, so both leave.
        public void Settle(ICollection<long> seqs)
        {
            Pending.RemoveAll(batch => seqs.Contains(batch.Seq));
        }

        public void PruneDays(string oldestDay)
        {
            var stale = new List<string>();
            foreach (var key in _dayImpressions.Keys) if (string.CompareOrdinal(key, oldestDay) < 0) stale.Add(key);
            foreach (var key in stale) _dayImpressions.Remove(key);
        }

        public Dictionary<string, object> ToJson()
        {
            var rows = new List<object>();
            foreach (var row in _rows.Values)
            {
                rows.Add(new Dictionary<string, object>
                {
                    ["day"] = row.Day, ["placementId"] = row.PlacementId, ["creativeId"] = row.CreativeId,
                    ["variantKey"] = row.VariantKey, ["fallbackReason"] = row.FallbackReason,
                    ["impressions"] = row.Impressions, ["viewMs"] = row.ViewMs, ["closes"] = row.Closes,
                    ["ctaAttempts"] = row.CtaAttempts, ["completions"] = row.Completions
                });
            }
            var pending = new List<object>();
            foreach (var batch in Pending) pending.Add(new Dictionary<string, object> { ["seq"] = batch.Seq, ["envelope"] = batch.Envelope });
            var days = new Dictionary<string, object>();
            foreach (var pair in _dayImpressions) days[pair.Key] = pair.Value;
            return new Dictionary<string, object> { ["lastSeq"] = LastSeq, ["rows"] = rows, ["pending"] = pending, ["days"] = days };
        }

        public static Ledger FromJson(JsonNode node)
        {
            var ledger = new Ledger { LastSeq = Read.Long(node, "lastSeq") };
            foreach (var item in Read.Arr(node, "rows"))
            {
                var row = new LedgerRow
                {
                    Day = Read.Str(item, "day"), PlacementId = Read.Str(item, "placementId"), CreativeId = Read.Str(item, "creativeId"),
                    VariantKey = Read.Str(item, "variantKey"), FallbackReason = Read.Str(item, "fallbackReason"),
                    Impressions = Read.Long(item, "impressions"), ViewMs = Read.Long(item, "viewMs"), Closes = Read.Long(item, "closes"),
                    CtaAttempts = Read.Long(item, "ctaAttempts"), Completions = Read.Long(item, "completions")
                };
                ledger._rows[row.Key] = row;
            }
            foreach (var item in Read.Arr(node, "pending"))
            {
                ledger.Pending.Add(new PendingBatch { Seq = Read.Long(item, "seq"), Envelope = Read.Str(item, "envelope") });
            }
            foreach (var pair in Read.Node(node, "days").ObjectValue) ledger._dayImpressions[pair.Key] = (long)pair.Value.NumberValue;
            return ledger;
        }
    }
}
