using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace FlightmodeSdk
{
    // Plays the HTML creative in the system WebView (WKWebView / android.webkit.WebView) above the
    // Unity view. Native code serves the package from disk on a local origin, blocks the network,
    // draws the progress bar and close button and enforces the duration. It reports back through
    // UnitySendMessage(callbackObject, "OnNativeEvent", json).
    sealed class NativeWebViewSurface : ICreativeSurface
    {
#if UNITY_IOS && !UNITY_EDITOR
        const string Post = "function(m){window.webkit.messageHandlers.fm.postMessage(m)}";
        [DllImport("__Internal")] static extern void FMShow(string slot, string root, string entryPath, string entryHtml, int durationMs, int closeAfterMs, int progressSteps, int readyTimeoutMs, [MarshalAs(UnmanagedType.I1)] bool bannerTop, int bannerWidthDp, int bannerHeightDp, string callbackObject);
        [DllImport("__Internal")] static extern void FMHide(string slot);
        [DllImport("__Internal")] static extern void FMSetPaused([MarshalAs(UnmanagedType.I1)] bool paused);
        [DllImport("__Internal")] static extern void FMSetConnect(string slot, string message, float alpha);
        [DllImport("__Internal")] static extern string FMLocale();
        public static string DeviceLocale() => FMLocale();
#elif UNITY_ANDROID && !UNITY_EDITOR
        const string Post = "function(m){__fmNative.post(JSON.stringify(m))}";
        static AndroidJavaClass _view;
        static AndroidJavaClass View => _view ?? (_view = new AndroidJavaClass("ad.flightmode.FlightmodeView"));
        static void FMShow(string slot, string root, string entryPath, string entryHtml, int durationMs, int closeAfterMs, int progressSteps, int readyTimeoutMs, bool bannerTop, int bannerWidthDp, int bannerHeightDp, string callbackObject)
        {
            View.CallStatic("show", slot, root, entryPath, entryHtml, durationMs, closeAfterMs, progressSteps, readyTimeoutMs, bannerTop, bannerWidthDp, bannerHeightDp, callbackObject);
        }
        static void FMHide(string slot) => View.CallStatic("hide", slot);
        static void FMSetPaused(bool paused) => View.CallStatic("setPaused", paused);
        static void FMSetConnect(string slot, string message, float alpha) => View.CallStatic("setConnect", slot, message, alpha);
        public static string DeviceLocale() => View.CallStatic<string>("locale");
#else
        const string Post = "function(m){}";
        static void FMShow(string slot, string root, string entryPath, string entryHtml, int durationMs, int closeAfterMs, int progressSteps, int readyTimeoutMs, bool bannerTop, int bannerWidthDp, int bannerHeightDp, string callbackObject) => throw new PlatformNotSupportedException();
        static void FMHide(string slot) => throw new PlatformNotSupportedException();
        static void FMSetPaused(bool paused) => throw new PlatformNotSupportedException();
        static void FMSetConnect(string slot, string message, float alpha) => throw new PlatformNotSupportedException();
        public static string DeviceLocale() => throw new PlatformNotSupportedException();
#endif

        public static bool Supported
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        readonly string _slot;
        readonly string _callbackObject;
        Action<string> _onOpen;
        Action<SurfaceResult> _onClosed;

        public NativeWebViewSurface(string slot, string callbackObject)
        {
            _slot = slot;
            _callbackObject = callbackObject;
        }

        public bool IsShowing => _onClosed != null;

        public void Show(AdSession session, string format, SurfaceLayout layout, Action<string> onOpen, Action<SurfaceResult> onClosed)
        {
            if (IsShowing) throw new InvalidOperationException(_slot + " is already showing");
            _onOpen = onOpen;
            _onClosed = onClosed;
            var html = File.ReadAllText(session.EntryPath, Encoding.UTF8);
            var entryHtml = Bridge.Inject(html, Bridge.Script(session.Variant, format, session.DurationMs, Post));
            FMShow(_slot, session.Directory, session.Variant.Entry, entryHtml,
                layout.Fullscreen ? session.DurationMs : 0, (int)(session.DurationMs * FlightmodePolicy.CloseAfterShare), session.Variant.ProgressSteps,
                FlightmodePolicy.ReadyTimeoutMs, layout.Position == BannerPosition.Top, layout.WidthDp, layout.HeightDp, _callbackObject);
        }

        public void Hide()
        {
            if (IsShowing) FMHide(_slot);
        }

        public void SetPaused(bool paused) => FMSetPaused(paused);

        public void SetConnect(string message)
        {
            // Natives take an empty message as removal: AndroidJavaClass cannot pass a null String.
            if (IsShowing) FMSetConnect(_slot, message ?? "", FlightmodePolicy.ConnectLayerAlpha);
        }

        // Events from native code: {"t":"open","id":...} or {"t":"closed","viewMs":...,"closedByUser":...,"completed":...}.
        internal void OnNativeEvent(JsonNode node)
        {
            switch (node["t"].StringValue)
            {
                case "open":
                    _onOpen?.Invoke(node["id"].StringValue);
                    break;
                case "closed":
                    var callback = _onClosed;
                    _onClosed = null;
                    _onOpen = null;
                    callback?.Invoke(new SurfaceResult
                    {
                        ViewMs = (long)node["viewMs"].NumberValue,
                        ClosedByUser = node["closedByUser"].BoolValue,
                        Completed = node["completed"].BoolValue
                    });
                    break;
                default:
                    throw new FormatException("unknown native event " + node["t"].StringValue);
            }
        }
    }
}
