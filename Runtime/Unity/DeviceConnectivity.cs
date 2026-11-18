using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FlightmodeSdk
{
    // Reads the network state from the native plugins, which can tell airplane mode or data denied to
    // the app from a plain loss of signal. Elsewhere only reachability is known, so every loss is plain.
    static class DeviceConnectivity
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int FMConnectivity();
        static int Native() => FMConnectivity();
#elif UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaClass _view;
        static int Native() => (_view ?? (_view = new AndroidJavaClass("ad.flightmode.FlightmodeView"))).CallStatic<int>("connectivity");
#else
        static int Native() => -1;
#endif

        public static Connectivity Read()
        {
            var state = Native();
            switch (state)
            {
                case -1: return Application.internetReachability == NetworkReachability.NotReachable ? Connectivity.Offline : Connectivity.Online;
                case 0: return Connectivity.Online;
                case 1: return Connectivity.Offline;
                case 2: return Connectivity.OfflineByUser;
                default: throw new InvalidOperationException("unknown native connectivity state " + state);
            }
        }
    }
}
