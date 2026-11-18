using System;
using UnityEngine;
using UnityEngine.UI;

namespace FlightmodeSdk
{
    // Full-screen message asking the player to go online. The game supplies the localized text.
    sealed class ConnectPrompt
    {
        const int SortingOrder = 32001;
        const float CloseSize = 32f;
        const float Margin = 12f;

        readonly GameObject _canvas;
        Action _onClosed;

        public ConnectPrompt(Transform parent, string message, bool dismissible, Action onClosed)
        {
            _onClosed = onClosed;
            var canvas = Overlay.Canvas(parent, "FlightmodeConnectPrompt", SortingOrder);
            _canvas = canvas.gameObject;
            Overlay.Fill(canvas, "Backdrop", new Color(0f, 0f, 0f, 0.85f), Vector2.zero, Vector2.one);
            var safe = Overlay.SafeArea(canvas);
            var text = Overlay.Rect(safe, "Message", new Vector2(0.1f, 0.3f), new Vector2(0.9f, 0.7f)).gameObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 20;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            text.text = message;
            if (!dismissible) return;
            var close = Overlay.CloseButton(safe, CloseSize, Close);
            close.anchoredPosition = new Vector2(-Margin, -Margin);
        }

        public void Close()
        {
            if (_onClosed == null) return;
            UnityEngine.Object.Destroy(_canvas);
            var callback = _onClosed;
            _onClosed = null;
            callback();
        }
    }
}
