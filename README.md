# Flightmode Unity SDK

Offline ad fallback for Unity games. When the device is offline or behind a DNS
ad blocker, your mediation network cannot fill. Flightmode plays signed HTML
creatives downloaded ahead of time, counts impressions locally, and uploads
signed metrics on the next reconnect.

- Package name: `ad.flightmode.sdk`
- Unity 2021.3 or newer
- iOS (WKWebView) and Android (WebView) play the HTML creatives. The Editor and
  other platforms show the creative's poster image.

## Installation

Package Manager: **Add package from git URL...**

```
https://github.com/clasen/flightmode-unity.git#v0.1.1
```

### Sample

The package ships a **Mediation fallback** sample. Import it from the Package
Manager: select **Flightmode**, open **Samples**, and click **Import**.

## Setup

Get your app key from your publisher's `get_sdk_config`:

| Option | Description |
| --- | --- |
| `AppKey` | Your app key. |
| `Endpoint` | Optional. Base URL of the Flightmode server. Defaults to `https://flightmodeads.com`. |
| `FlightKeysJson` | Optional. The `flightKeys` JSON array that verifies flights offline. Defaults to the keys of `https://flightmodeads.com`. |
| `AccountId` | Optional. Only with the user's consent: counts unique accounts through a salted hash. |
| `Locale` | Optional. Overrides the device locale, for testing translations. |

Initialize once, early in the game:

```csharp
using FlightmodeSdk;

Flightmode.Initialize(new FlightmodeOptions
{
    AppKey = "your-app-key",
});
```

A self-hosted server needs both `Endpoint` and `FlightKeysJson`, from its own
`get_sdk_config`.

The SDK creates a persistent GameObject, syncs whenever the device is online,
and keeps the latest flight on disk for offline play.

## Usage

### Interstitial

Ask your mediation first. When it cannot fill, show Flightmode:

```csharp
public void ShowInterstitial(Action onDone)
{
    if (mediationReady)
    {
        ShowMediationInterstitial(onDone);
        return;
    }
    if (!Flightmode.Interstitial.Show("interstitial-placement-id", onDone)) onDone();
}
```

`Show` returns `false` when nothing can play (no flight, unknown placement,
frequency cap, cooldown). Call `onDone` yourself in that case.

Use `Flightmode.Interstitial.ShowDirect` to show a creative without asking the
mediation first.

### Banner

```csharp
Flightmode.Banner.Show("banner-placement-id", BannerPosition.Bottom);
Flightmode.Banner.Hide();
```

The banner rotates creatives of the placement until you call `Hide`.

### Skip the mediation when it cannot fill

`Flightmode.Audience` tells you when the mediation has no chance:

- `Audience.Offline`: airplane mode, data denied to the app, or no signal for
  longer than the dwell time.
- `Audience.AdBlocked`: online behind a DNS ad blocker.
- `Audience.None`: ask the mediation as usual.

### Expired flights

A flight that stays offline too long expires. The server assigns what the game
should do through `Flightmode.ExpiredAction`. Check it at a level start:

```csharp
if (!Flightmode.ShowConnectPrompt("Connect to the internet to keep playing.", onContinue))
    onContinue();
```

`ShowConnectPrompt` does nothing and returns `false` when no prompt is due. A
`Prompt` can be dismissed; a `Block` closes only once a sync brings a fresh
flight.

### Sync

The SDK syncs on its own. To force it, call `Flightmode.SyncNow()`. Subscribe to
`Flightmode.Synced` to observe each sync result (`Reached`, `Acked`,
`Rejected`, `NewFlight`).

## Tests

The core logic runs without Unity:

```bash
dotnet run --project Tests~/Flightmode.Tests.csproj
```

## Protocol

See [Documentation~/PROTOCOL.md](Documentation~/PROTOCOL.md) for the HTTP
protocol and signing details.

## License

MIT. See [LICENSE.md](LICENSE.md).
