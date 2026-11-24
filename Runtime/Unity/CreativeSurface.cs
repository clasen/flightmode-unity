using System;

namespace FlightmodeSdk
{
    public sealed class SurfaceResult
    {
        public long ViewMs;
        public bool ClosedByUser;
        public bool Completed;
    }

    struct SurfaceLayout
    {
        public bool Fullscreen;
        public BannerPosition Position;
        public int WidthDp;
        public int HeightDp;
    }

    // Plays one ad session in one slot (interstitial or banner). onOpen fires for each tap on a
    // link; onClosed fires exactly once per Show, also after Hide. SetConnect covers the creative
    // with a message asking for the internet, or removes it when message is null.
    interface ICreativeSurface
    {
        bool IsShowing { get; }
        void Show(AdSession session, string format, SurfaceLayout layout, Action<string> onOpen, Action<SurfaceResult> onClosed);
        void Hide();
        void SetPaused(bool paused);
        void SetConnect(string message);
    }
}
