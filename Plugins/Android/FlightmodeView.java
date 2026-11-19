package ad.flightmode;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.provider.Settings;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;
import android.window.OnBackInvokedCallback;
import android.window.OnBackInvokedDispatcher;

import com.unity3d.player.UnityPlayer;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;

// Plays Flightmode HTML creatives in an android.webkit.WebView above the Unity view.
// The package is served from disk on https://creative.flightmode.local/ and every other request
// is answered locally with 403, so nothing reaches the network. The native layer owns the
// progress bar, the close button and the play time.
public final class FlightmodeView {
    static final String HOST = "creative.flightmode.local";
    static final String ORIGIN = "https://" + HOST + "/";
    static final String POLICY = "default-src 'self' 'unsafe-inline' 'unsafe-eval' data: blob:; connect-src 'self'";
    static final int MARGIN_DP = 12;
    static final int BAR_DP = 4;
    static final int CLOSE_DP = 32;
    static final int GAP_DP = 4;
    static final Map<String, String> TYPES = new HashMap<>();
    static final Map<String, Ad> ADS = new HashMap<>();
    static final Handler MAIN = new Handler(Looper.getMainLooper());

    static {
        String[] pairs = {
            "html", "text/html", "js", "text/javascript", "mjs", "text/javascript", "css", "text/css", "json", "application/json",
            "svg", "image/svg+xml", "png", "image/png", "jpg", "image/jpeg", "jpeg", "image/jpeg", "webp", "image/webp",
            "gif", "image/gif", "woff2", "font/woff2", "woff", "font/woff", "ttf", "font/ttf", "mp4", "video/mp4",
            "webm", "video/webm", "mp3", "audio/mpeg", "ogg", "audio/ogg"
        };
        for (int i = 0; i < pairs.length; i += 2) TYPES.put(pairs[i], pairs[i + 1]);
    }

    private FlightmodeView() {
    }

    public static void show(final String slot, final String root, final String entryPath, final String entryHtml, final int durationMs,
                            final int closeAfterMs, final int progressSteps, final int readyTimeoutMs, final boolean bannerTop,
                            final int bannerWidthDp, final int bannerHeightDp, final String callbackObject) {
        MAIN.post(() -> {
            Ad previous = ADS.get(slot);
            if (previous != null) previous.finish(false, false);
            Ad ad = new Ad(UnityPlayer.currentActivity, slot, root, entryPath, entryHtml, durationMs, closeAfterMs, callbackObject);
            ADS.put(slot, ad);
            ad.present(progressSteps, readyTimeoutMs, bannerTop, bannerWidthDp, bannerHeightDp);
        });
    }

    public static void hide(final String slot) {
        MAIN.post(() -> {
            Ad ad = ADS.get(slot);
            if (ad != null) ad.finish(false, false);
        });
    }

    public static void setPaused(final boolean paused) {
        MAIN.post(() -> {
            for (Ad ad : ADS.values().toArray(new Ad[0])) ad.setPaused(paused);
        });
    }

    public static String locale() {
        return Locale.getDefault().toLanguageTag();
    }

    // 0 online, 1 offline, 2 offline by the user (airplane mode). Online means a validated network,
    // so a Wi-Fi without internet or behind a captive portal counts as offline.
    public static int connectivity() {
        Context context = UnityPlayer.currentActivity.getApplicationContext();
        ConnectivityManager manager = (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
        Network network = manager.getActiveNetwork();
        NetworkCapabilities capabilities = network == null ? null : manager.getNetworkCapabilities(network);
        if (capabilities != null && capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)) return 0;
        return Settings.Global.getInt(context.getContentResolver(), Settings.Global.AIRPLANE_MODE_ON, 0) != 0 ? 2 : 1;
    }

    static int dp(Context context, float value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }

    static String contentType(String path) {
        int dot = path.lastIndexOf('.');
        String type = dot < 0 ? null : TYPES.get(path.substring(dot + 1).toLowerCase(Locale.ROOT));
        return type != null ? type : "application/octet-stream";
    }

    static final class Ad {
        final Activity activity;
        final String slot;
        final String root;
        final String entryPath;
        final String entryHtml;
        final int durationMs;
        final int closeAfterMs;
        final String callbackObject;
        FrameLayout container;
        WebView webView;
        ProgressView progress;
        CloseView close;
        boolean started;
        boolean paused;
        boolean completed;
        boolean finished;
        Object backCallback;
        long accumulatedMs;
        long resumedAt;

        Ad(Activity activity, String slot, String root, String entryPath, String entryHtml, int durationMs, int closeAfterMs, String callbackObject) {
            this.activity = activity;
            this.slot = slot;
            this.root = root;
            this.entryPath = entryPath;
            this.entryHtml = entryHtml;
            this.durationMs = durationMs;
            this.closeAfterMs = closeAfterMs;
            this.callbackObject = callbackObject;
        }

        boolean interstitial() {
            return durationMs > 0;
        }

        @SuppressLint({"SetJavaScriptEnabled", "AddJavascriptInterface"})
        void present(int steps, int readyTimeoutMs, boolean bannerTop, int bannerWidthDp, int bannerHeightDp) {
            ViewGroup host = activity.findViewById(android.R.id.content);
            container = new FrameLayout(activity);
            container.setClickable(true);
            FrameLayout.LayoutParams params;
            if (interstitial()) {
                container.setBackgroundColor(Color.BLACK);
                params = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
                container.setOnApplyWindowInsetsListener((view, insets) -> {
                    applySafeArea(view, insets);
                    return insets;
                });
            } else {
                params = new FrameLayout.LayoutParams(dp(activity, bannerWidthDp), dp(activity, bannerHeightDp),
                    Gravity.CENTER_HORIZONTAL | (bannerTop ? Gravity.TOP : Gravity.BOTTOM));
            }

            webView = new WebView(activity);
            webView.setBackgroundColor(Color.TRANSPARENT);
            webView.setVerticalScrollBarEnabled(false);
            webView.setHorizontalScrollBarEnabled(false);
            webView.setOverScrollMode(View.OVER_SCROLL_NEVER);
            WebSettings settings = webView.getSettings();
            settings.setJavaScriptEnabled(true);
            settings.setAllowFileAccess(false);
            settings.setAllowContentAccess(false);
            settings.setMediaPlaybackRequiresUserGesture(false);
            settings.setSupportMultipleWindows(false);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) settings.setSafeBrowsingEnabled(false);
            webView.addJavascriptInterface(new NativeBridge(this), "__fmNative");
            webView.setWebViewClient(new LocalClient(this));
            container.addView(webView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

            if (interstitial()) {
                progress = new ProgressView(activity, Math.max(1, steps));
                FrameLayout.LayoutParams bar = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(activity, BAR_DP), Gravity.TOP);
                int margin = dp(activity, MARGIN_DP);
                bar.setMargins(margin, margin, margin, 0);
                container.addView(progress, bar);
                close = new CloseView(activity);
                FrameLayout.LayoutParams button = new FrameLayout.LayoutParams(dp(activity, CLOSE_DP), dp(activity, CLOSE_DP), Gravity.TOP | Gravity.END);
                button.setMargins(0, margin * 2 + dp(activity, BAR_DP), margin, 0);
                close.setVisibility(closeAfterMs > 0 ? View.GONE : View.VISIBLE);
                close.setOnClickListener(view -> closeByUser());
                container.addView(close, button);
            }

            host.addView(container, params);
            if (interstitial()) captureBack();
            webView.loadUrl(ORIGIN + entryPath);
            MAIN.postDelayed(() -> {
                if (!started) finish(false, false);
            }, readyTimeoutMs);
        }

        void applySafeArea(View view, WindowInsets insets) {
            int left = insets.getSystemWindowInsetLeft();
            int top = insets.getSystemWindowInsetTop();
            int right = insets.getSystemWindowInsetRight();
            int bottom = insets.getSystemWindowInsetBottom();
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P && insets.getDisplayCutout() != null) {
                left = Math.max(left, insets.getDisplayCutout().getSafeInsetLeft());
                top = Math.max(top, insets.getDisplayCutout().getSafeInsetTop());
                right = Math.max(right, insets.getDisplayCutout().getSafeInsetRight());
                bottom = Math.max(bottom, insets.getDisplayCutout().getSafeInsetBottom());
            }
            view.setPadding(left, top, right, bottom);
        }

        // Back belongs to the interstitial while it is up: it closes once the close button shows and
        // does nothing before, so it never reaches the game underneath. The WebView holds the focus,
        // and API 33+ delivers back through the dispatcher when the app opts into predictive back.
        void captureBack() {
            webView.setOnKeyListener((view, keyCode, event) -> {
                if (keyCode != KeyEvent.KEYCODE_BACK) return false;
                if (event.getAction() == KeyEvent.ACTION_UP) onBack();
                return true;
            });
            webView.requestFocus();
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                OnBackInvokedCallback callback = this::onBack;
                activity.getOnBackInvokedDispatcher().registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_OVERLAY, callback);
                backCallback = callback;
            }
        }

        void onBack() {
            if (close.getVisibility() == View.VISIBLE) closeByUser();
        }

        // A close after the creative played to its end is a completion, not a skip.
        void closeByUser() {
            finish(!completed, completed);
        }

        // The creative reached its end: the clock stops and the ad stays up until the user closes it.
        void complete() {
            accumulatedMs = elapsedMs();
            completed = true;
            progress.setProgress(1f);
            close.setVisibility(View.VISIBLE);
        }

        long elapsedMs() {
            long running = started && !paused && !completed ? SystemClock.uptimeMillis() - resumedAt : 0;
            return accumulatedMs + running;
        }

        void start() {
            if (started || finished) return;
            started = true;
            resumedAt = SystemClock.uptimeMillis();
            emit("onStart", null);
            if (interstitial()) container.postOnAnimation(this::tick);
        }

        void tick() {
            if (finished) return;
            if (!paused) {
                long elapsed = elapsedMs();
                progress.setProgress(Math.min(1f, (float) elapsed / durationMs));
                if (elapsed >= closeAfterMs) close.setVisibility(View.VISIBLE);
                if (elapsed >= durationMs) {
                    complete();
                    return;
                }
            }
            container.postOnAnimation(this::tick);
        }

        void setPaused(boolean value) {
            if (!started || finished || value == paused) return;
            if (value) accumulatedMs = elapsedMs();
            else resumedAt = SystemClock.uptimeMillis();
            paused = value;
            emit("onPause", value ? "true" : "false");
        }

        void emit(String name, String argument) {
            webView.evaluateJavascript("window.__flightmodeEmit&&window.__flightmodeEmit('" + name + "'" + (argument != null ? "," + argument : "") + ")", null);
        }

        void send(JSONObject event) {
            try {
                event.put("slot", slot);
            } catch (JSONException error) {
                throw new IllegalStateException(error);
            }
            UnityPlayer.UnitySendMessage(callbackObject, "OnNativeEvent", event.toString());
        }

        void finish(boolean closedByUser, boolean completed) {
            if (finished) return;
            long viewMs = elapsedMs();
            finished = true;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU && backCallback != null) {
                activity.getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback((OnBackInvokedCallback) backCallback);
            }
            if (container != null) {
                ((ViewGroup) container.getParent()).removeView(container);
                webView.removeJavascriptInterface("__fmNative");
                webView.destroy();
            }
            if (ADS.get(slot) == this) ADS.remove(slot);
            try {
                send(new JSONObject().put("t", "closed").put("viewMs", viewMs).put("closedByUser", closedByUser).put("completed", completed || this.completed));
            } catch (JSONException error) {
                throw new IllegalStateException(error);
            }
        }

        void onMessage(String json) {
            if (finished) return;
            try {
                JSONObject message = new JSONObject(json);
                String type = message.getString("t");
                if (type.equals("ready")) {
                    start();
                } else if (type.equals("open")) {
                    if (started) send(new JSONObject().put("t", "open").put("id", message.getString("id")));
                } else if (type.equals("done")) {
                    if (started && interstitial() && !completed) complete();
                }
            } catch (JSONException ignored) {
                // Malformed messages from the creative are dropped.
            }
        }

        WebResourceResponse serve(String url) {
            Map<String, String> headers = new HashMap<>();
            headers.put("Content-Security-Policy", POLICY);
            headers.put("Cache-Control", "no-store");
            if (!url.startsWith(ORIGIN)) return response(403, "text/plain", headers, new byte[0]);
            String path = url.substring(ORIGIN.length());
            int query = path.indexOf('?');
            if (query >= 0) path = path.substring(0, query);
            int fragment = path.indexOf('#');
            if (fragment >= 0) path = path.substring(0, fragment);
            path = android.net.Uri.decode(path);
            String type = contentType(path);
            if (path.equals(entryPath)) return response(200, type, headers, entryHtml.getBytes(StandardCharsets.UTF_8));
            try {
                File base = new File(root).getCanonicalFile();
                File file = new File(base, path).getCanonicalFile();
                if (!file.getPath().startsWith(base.getPath() + File.separator)) return response(403, "text/plain", headers, new byte[0]);
                if (!file.isFile()) return response(404, "text/plain", headers, new byte[0]);
                InputStream stream = new FileInputStream(file);
                return new WebResourceResponse(type, null, 200, "OK", headers, stream);
            } catch (IOException error) {
                return response(404, "text/plain", headers, new byte[0]);
            }
        }

        static WebResourceResponse response(int status, String type, Map<String, String> headers, byte[] body) {
            String reason = status == 200 ? "OK" : status == 403 ? "Forbidden" : "Not Found";
            return new WebResourceResponse(type, null, status, reason, headers, new ByteArrayInputStream(body));
        }
    }

    static final class NativeBridge {
        final Ad ad;

        NativeBridge(Ad ad) {
            this.ad = ad;
        }

        @JavascriptInterface
        public void post(final String json) {
            MAIN.post(() -> ad.onMessage(json));
        }
    }

    static final class LocalClient extends WebViewClient {
        final Ad ad;

        LocalClient(Ad ad) {
            this.ad = ad;
        }

        @Override
        public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
            return ad.serve(request.getUrl().toString());
        }

        @Override
        public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
            return !request.getUrl().toString().startsWith(ORIGIN);
        }

        @Override
        public boolean onRenderProcessGone(WebView view, android.webkit.RenderProcessGoneDetail detail) {
            ad.finish(false, false);
            return true;
        }
    }

    static final class ProgressView extends View {
        final int steps;
        final Paint track = new Paint(Paint.ANTI_ALIAS_FLAG);
        final Paint fill = new Paint(Paint.ANTI_ALIAS_FLAG);
        final RectF rect = new RectF();
        float progress;

        ProgressView(Context context, int steps) {
            super(context);
            this.steps = steps;
            track.setColor(Color.argb(90, 255, 255, 255));
            fill.setColor(Color.WHITE);
        }

        void setProgress(float value) {
            progress = value;
            invalidate();
        }

        @Override
        protected void onDraw(Canvas canvas) {
            float gap = dp(getContext(), GAP_DP);
            float width = (getWidth() - gap * (steps - 1)) / steps;
            float radius = getHeight() / 2f;
            float filled = progress * steps;
            for (int i = 0; i < steps; i++) {
                float left = i * (width + gap);
                rect.set(left, 0, left + width, getHeight());
                canvas.drawRoundRect(rect, radius, radius, track);
                float amount = Math.max(0f, Math.min(1f, filled - i));
                if (amount <= 0f) continue;
                rect.set(left, 0, left + width * amount, getHeight());
                canvas.drawRoundRect(rect, radius, radius, fill);
            }
        }
    }

    static final class CloseView extends View {
        final Paint background = new Paint(Paint.ANTI_ALIAS_FLAG);
        final Paint cross = new Paint(Paint.ANTI_ALIAS_FLAG);
        final RectF rect = new RectF();

        CloseView(Context context) {
            super(context);
            background.setColor(Color.argb(140, 0, 0, 0));
            cross.setColor(Color.WHITE);
            cross.setStrokeWidth(dp(context, 2.5f));
            cross.setStrokeCap(Paint.Cap.ROUND);
        }

        @Override
        protected void onDraw(Canvas canvas) {
            rect.set(0, 0, getWidth(), getHeight());
            float corner = dp(getContext(), 6);
            canvas.drawRoundRect(rect, corner, corner, background);
            float inset = getWidth() * 0.3f;
            canvas.drawLine(inset, inset, getWidth() - inset, getHeight() - inset, cross);
            canvas.drawLine(getWidth() - inset, inset, inset, getHeight() - inset, cross);
        }
    }
}
