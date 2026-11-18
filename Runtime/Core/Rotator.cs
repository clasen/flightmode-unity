using System;
using System.Collections.Generic;

namespace FlightmodeSdk
{
    // Smooth weighted round-robin per placement: over N picks each creative appears
    // in proportion to its booked shares, evenly interleaved.
    public sealed class Rotator
    {
        readonly Dictionary<string, Dictionary<string, long>> _current = new Dictionary<string, Dictionary<string, long>>();

        public string Next(FlightPlacement placement, Func<string, bool> playable)
        {
            if (!_current.TryGetValue(placement.PlacementId, out var current))
            {
                current = new Dictionary<string, long>();
                _current[placement.PlacementId] = current;
            }
            long total = 0;
            string best = null;
            foreach (var entry in placement.Creatives)
            {
                if (!playable(entry.CreativeId)) continue;
                current.TryGetValue(entry.CreativeId, out var value);
                value += entry.Weight;
                current[entry.CreativeId] = value;
                total += entry.Weight;
                if (best == null || value > current[best]) best = entry.CreativeId;
            }
            if (best != null) current[best] -= total;
            return best;
        }
    }
}
