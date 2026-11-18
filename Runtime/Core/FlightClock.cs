using System;
using System.Collections.Generic;

namespace FlightmodeSdk
{
    // Decides whether the stored flight expired without trusting the device clock alone.
    // Validity is measured from the device's own receipt time, and the flight also expires
    // after its validity in accumulated play time, so turning the clock back does not extend it.
    public sealed class FlightClock
    {
        public long ReceivedWallMs;
        public long MaxSeenWallMs;
        public long ElapsedMs;

        public void Start(long nowWallMs)
        {
            ReceivedWallMs = nowWallMs;
            MaxSeenWallMs = nowWallMs;
            ElapsedMs = 0;
        }

        public void Advance(long nowWallMs, long monotonicDeltaMs)
        {
            if (monotonicDeltaMs > 0) ElapsedMs += monotonicDeltaMs;
            if (nowWallMs > MaxSeenWallMs) MaxSeenWallMs = nowWallMs;
        }

        public bool IsExpired(long validityMs, long nowWallMs)
        {
            var latest = Math.Max(nowWallMs, MaxSeenWallMs);
            return latest - ReceivedWallMs >= validityMs || ElapsedMs >= validityMs;
        }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                ["receivedWallMs"] = ReceivedWallMs,
                ["maxSeenWallMs"] = MaxSeenWallMs,
                ["elapsedMs"] = ElapsedMs
            };
        }

        public static FlightClock FromJson(JsonNode node)
        {
            return new FlightClock
            {
                ReceivedWallMs = Read.Long(node, "receivedWallMs"),
                MaxSeenWallMs = Read.Long(node, "maxSeenWallMs"),
                ElapsedMs = Read.Long(node, "elapsedMs")
            };
        }
    }
}
