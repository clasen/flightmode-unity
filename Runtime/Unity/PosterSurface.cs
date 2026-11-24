using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace FlightmodeSdk
{
    // Shows the server-rendered poster of the variant with uGUI. Used in the editor and on
    // platforms without a native WebView; it keeps the same timing, close rules and metrics.
    // A tap on the poster opens the creative's first link.
    sealed class PosterSurface : ICreativeSurface
    {
        const int SortingOrder = 32000;
        const float BarHeight = 4f;
        const float Margin = 12f;
        const float CloseSize = 32f;

        readonly Transform _parent;
        GameObject _canvas;
        RectTransform _frame;
        GameObject _connect;
        Texture2D _texture;
        AdSession _session;
        SurfaceLayout _layout;
        Action<string> _onOpen;
        Action<SurfaceResult> _onClosed;
        readonly List<RectTransform> _fills = new List<RectTransform>();
        GameObject _close;
        float _viewSeconds;
        bool _paused;
        bool _completed;

        public PosterSurface(Transform parent)
        {
            _parent = parent;
        }

        public bool IsShowing => _onClosed != null;

        public void Show(AdSession session, string format, SurfaceLayout layout, Action<string> onOpen, Action<SurfaceResult> onClosed)
        {
            if (IsShowing) throw new InvalidOperationException("poster surface is already showing");
            var targetAspect = layout.Fullscreen ? Screen.safeArea.width / Screen.safeArea.height : (float)layout.WidthDp / layout.HeightDp;
            var posterPath = session.PosterPath(PosterSlot(session.Variant, format, targetAspect));
            _texture = new Texture2D(2, 2);
            if (!_texture.LoadImage(File.ReadAllBytes(posterPath))) throw new InvalidDataException("poster is not a PNG: " + posterPath);
            _session = session;
            _layout = layout;
            _onOpen = onOpen;
            _onClosed = onClosed;
            _viewSeconds = 0f;
            _completed = false;
            _fills.Clear();

            var canvas = Overlay.Canvas(_parent, "FlightmodePoster", SortingOrder);
            _canvas = canvas.gameObject;
            var safe = Overlay.SafeArea(canvas);
            RectTransform frame;
            if (layout.Fullscreen)
            {
                var backdrop = Overlay.Fill(canvas, "Backdrop", Color.black, Vector2.zero, Vector2.one);
                backdrop.transform.SetAsFirstSibling();
                frame = (RectTransform)backdrop.transform;
            }
            else
            {
                var y = layout.Position == BannerPosition.Top ? 1f : 0f;
                frame = Overlay.Rect(safe, "Banner", new Vector2(0.5f, y), new Vector2(0.5f, y));
                frame.pivot = new Vector2(0.5f, y);
                frame.sizeDelta = new Vector2(layout.WidthDp, layout.HeightDp);
            }
            _frame = frame;
            var poster = Overlay.Rect(frame, "Poster", Vector2.zero, Vector2.one).gameObject.AddComponent<RawImage>();
            poster.texture = _texture;
            var fitter = poster.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = (float)_texture.width / _texture.height;
            var firstLink = FirstLink(session.Variant);
            if (firstLink != null) Overlay.OnTap(poster, () => _onOpen?.Invoke(firstLink));

            if (!layout.Fullscreen) return;
            var bar = Overlay.Rect(safe, "Progress", new Vector2(0f, 1f), new Vector2(1f, 1f));
            bar.offsetMin = new Vector2(Margin, -Margin - BarHeight);
            bar.offsetMax = new Vector2(-Margin, -Margin);
            var steps = Math.Max(1, session.Variant.ProgressSteps);
            const float gap = 0.01f;
            var width = (1f - gap * (steps - 1)) / steps;
            for (var i = 0; i < steps; i++)
            {
                var x = i * (width + gap);
                var track = Overlay.Fill(bar, "Step", new Color(1f, 1f, 1f, 0.35f), new Vector2(x, 0f), new Vector2(x + width, 1f));
                var fill = Overlay.Fill(track.transform, "Fill", Color.white, Vector2.zero, new Vector2(0f, 1f));
                _fills.Add(fill.rectTransform);
            }
            var close = Overlay.CloseButton(safe, CloseSize, () => Close(!_completed, _completed));
            close.anchoredPosition = new Vector2(-Margin, -Margin * 2 - BarHeight);
            _close = close.gameObject;
            _close.SetActive(false);
        }

        // Picks the rendered slot of this format whose aspect is closest to the target.
        static string PosterSlot(CreativeVariant variant, string format, float targetAspect)
        {
            string best = null;
            var bestDistance = float.MaxValue;
            foreach (var slot in variant.Posters.Keys)
            {
                if (!slot.StartsWith(format + ":", StringComparison.Ordinal)) continue;
                var size = slot.Substring(format.Length + 1).Split(':', 'x');
                var distance = Mathf.Abs(Mathf.Log(float.Parse(size[0]) / float.Parse(size[1]) / targetAspect));
                if (distance >= bestDistance) continue;
                best = slot;
                bestDistance = distance;
            }
            if (best == null) throw new InvalidOperationException("creative " + variant.CreativeId + " has no " + format + " poster");
            return best;
        }

        static string FirstLink(CreativeVariant variant)
        {
            foreach (var id in variant.Links.Keys) return id;
            return null;
        }

        public void Tick(float deltaSeconds)
        {
            if (!IsShowing || _paused || _completed) return;
            _viewSeconds += deltaSeconds;
            if (!_layout.Fullscreen) return;
            var elapsedMs = _viewSeconds * 1000f;
            var progress = Mathf.Clamp01(elapsedMs / _session.DurationMs) * _fills.Count;
            for (var i = 0; i < _fills.Count; i++) _fills[i].anchorMax = new Vector2(Mathf.Clamp01(progress - i), 1f);
            if (elapsedMs >= _session.DurationMs * FlightmodePolicy.CloseAfterShare) _close.SetActive(true);
            if (elapsedMs >= _session.DurationMs) _completed = true;
        }

        void Close(bool closedByUser, bool completed)
        {
            UnityEngine.Object.Destroy(_canvas);
            UnityEngine.Object.Destroy(_texture);
            var callback = _onClosed;
            var viewMs = (long)(_viewSeconds * 1000f);
            _onClosed = null;
            _onOpen = null;
            _session = null;
            _frame = null;
            _connect = null;
            callback(new SurfaceResult { ViewMs = viewMs, ClosedByUser = closedByUser, Completed = completed || _completed });
        }

        public void Hide()
        {
            if (IsShowing) Close(false, false);
        }

        public void SetPaused(bool paused)
        {
            _paused = paused;
        }

        public void SetConnect(string message)
        {
            if (!IsShowing) return;
            if (_connect != null) UnityEngine.Object.Destroy(_connect);
            _connect = null;
            if (message == null) return;
            var layer = Overlay.Fill(_frame, "Connect", new Color(0f, 0f, 0f, FlightmodePolicy.ConnectLayerAlpha), Vector2.zero, Vector2.one);
            _connect = layer.gameObject;
            var text = Overlay.Rect(layer.transform, "Message", Vector2.zero, Vector2.one).gameObject.AddComponent<Text>();
            text.rectTransform.offsetMin = new Vector2(Margin, 0f);
            text.rectTransform.offsetMax = new Vector2(-Margin, 0f);
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 8;
            text.resizeTextMaxSize = 20;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            text.text = message;
        }
    }
}
