using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FlightmodeSdk
{
    // uGUI building blocks for the poster surface and the connect prompt. Canvas units are dp.
    static class Overlay
    {
        public static RectTransform Canvas(Transform parent, string name, int sortingOrder)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = Screen.dpi > 0 ? Screen.dpi / 160f : 1f;
            go.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();
            return (RectTransform)go.transform;
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
#if ENABLE_LEGACY_INPUT_MANAGER
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            UnityEngine.Object.DontDestroyOnLoad(go);
#else
            Debug.LogWarning("Flightmode: add an EventSystem to the scene so ad buttons receive taps.");
#endif
        }

        public static RectTransform Rect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        public static Image Fill(Transform parent, string name, Color color, Vector2 anchorMin, Vector2 anchorMax)
        {
            var image = Rect(parent, name, anchorMin, anchorMax).gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        // The device safe area as a stretched child of the canvas.
        public static RectTransform SafeArea(Transform canvas)
        {
            var area = Screen.safeArea;
            return Rect(canvas, "SafeArea",
                new Vector2(area.xMin / Screen.width, area.yMin / Screen.height),
                new Vector2(area.xMax / Screen.width, area.yMax / Screen.height));
        }

        public static void OnTap(Graphic graphic, Action action)
        {
            graphic.raycastTarget = true;
            graphic.gameObject.AddComponent<Button>().onClick.AddListener(() => action());
        }

        // A round-cornered dark square with an X drawn from two rotated bars, so no font is needed.
        public static RectTransform CloseButton(Transform parent, float size, Action onClick)
        {
            var rect = Rect(parent, "Close", Vector2.one, Vector2.one);
            rect.pivot = Vector2.one;
            rect.sizeDelta = new Vector2(size, size);
            var background = rect.gameObject.AddComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.55f);
            OnTap(background, onClick);
            foreach (var angle in new[] { 45f, -45f })
            {
                var bar = Fill(rect, "Bar", Color.white, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                bar.raycastTarget = false;
                bar.rectTransform.sizeDelta = new Vector2(size * 0.55f, size * 0.08f);
                bar.rectTransform.localRotation = Quaternion.Euler(0f, 0f, angle);
            }
            return rect;
        }
    }
}
