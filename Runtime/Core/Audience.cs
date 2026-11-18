using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace FlightmodeSdk
{
    // What the platform reports about the network. OfflineByUser means the player cut it:
    // airplane mode, radios off, or data denied to the app.
    public enum Connectivity { Online, Offline, OfflineByUser }

    // Players Flightmode serves on its own account: offline by choice, or online behind an ad blocker.
    public enum Audience { None, Offline, AdBlocked }

    // Separates a player who went offline on purpose from one who lost signal for a moment (an
    // elevator, a tunnel): the second gets the mediation back as soon as the signal returns, so
    // a loss of signal counts as offline only after it lasts the dwell time.
    public sealed class AudienceTracker
    {
        readonly long _offlineDwellMs;
        Connectivity _connectivity = Connectivity.Online;
        long _offlineSinceMs;
        long _nowMs = long.MinValue;

        public AudienceTracker(long offlineDwellMs)
        {
            if (offlineDwellMs < 0) throw new ArgumentOutOfRangeException(nameof(offlineDwellMs));
            _offlineDwellMs = offlineDwellMs;
        }

        // Set by the ad block probe; kept until the next conclusive probe.
        public bool AdBlocked { get; set; }

        public bool Online => _connectivity == Connectivity.Online;

        // nowMs is a monotonic clock that keeps running while the app is in the background.
        public void Observe(Connectivity connectivity, long nowMs)
        {
            if (nowMs < _nowMs) throw new ArgumentException("the audience clock went backwards", nameof(nowMs));
            if (connectivity != Connectivity.Online && _connectivity == Connectivity.Online) _offlineSinceMs = nowMs;
            _connectivity = connectivity;
            _nowMs = nowMs;
        }

        public Audience Audience
        {
            get
            {
                switch (_connectivity)
                {
                    case Connectivity.OfflineByUser: return Audience.Offline;
                    case Connectivity.Offline: return _nowMs - _offlineSinceMs >= _offlineDwellMs ? Audience.Offline : Audience.None;
                    default: return AdBlocked ? Audience.AdBlocked : Audience.None;
                }
            }
        }

        // Why a fallback impression plays, or null while a short loss of signal lasts.
        public FallbackReason? Fallback
        {
            get
            {
                switch (Audience)
                {
                    case Audience.Offline: return FallbackReason.Offline;
                    case Audience.AdBlocked: return FallbackReason.AdBlocked;
                    default: return Online ? FallbackReason.NoFill : (FallbackReason?)null;
                }
            }
        }
    }

    // Phone ad blockers work at DNS (Private DNS, DNS profiles, local VPNs, Pi-hole): ad hosts
    // resolve to a sinkhole address or to nothing while other hosts resolve normally.
    public static class AdBlockProbe
    {
        enum Answer { Routable, Blocked, Unknown }

        // True when every ad host is blocked, false when one resolves to a routable address, and
        // null when DNS gave no usable answer (the control host does not resolve, or a lookup failed).
        public static async Task<bool?> RunAsync(Func<string, Task<IPAddress[]>> resolve, string controlHost, IReadOnlyList<string> adHosts)
        {
            if (adHosts.Count == 0) throw new ArgumentException("the probe needs ad hosts", nameof(adHosts));
            if (await Lookup(resolve, controlHost) != Answer.Routable) return null;
            foreach (var host in adHosts)
            {
                var answer = await Lookup(resolve, host);
                if (answer == Answer.Routable) return false;
                if (answer == Answer.Unknown) return null;
            }
            return true;
        }

        static async Task<Answer> Lookup(Func<string, Task<IPAddress[]>> resolve, string host)
        {
            IPAddress[] addresses;
            try
            {
                addresses = await resolve(host);
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.HostNotFound || error.SocketErrorCode == SocketError.NoData)
            {
                return Answer.Blocked;
            }
            catch (SocketException)
            {
                return Answer.Unknown;
            }
            if (addresses.Length == 0) return Answer.Blocked;
            foreach (var address in addresses) if (!Sinkhole(address)) return Answer.Routable;
            return Answer.Blocked;
        }

        static bool Sinkhole(IPAddress address) =>
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(address);
    }
}
