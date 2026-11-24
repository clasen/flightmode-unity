# Flightmode protocol (version 1)

The SDK talks to the server through two HTTP endpoints. One round trip covers the
whole sync: it uploads the signed metric batches and, when needed, brings back a
new signed flight. The schemas live in
`packages/protocol/src/schemas.ts` in the server repository. They are
closed: an unknown field is a validation error.

## Lifecycle

1. **Online.** The SDK calls `POST /v1/sync`. The first call registers the install
   and receives its `installKey`. Later calls return a flight whenever the one on
   the device is stale.
2. **Download.** The SDK fetches every file of the flight with
   `GET /v1/files/:sha256`. It checks each hash and activates the new flight only
   when all files are on disk. Until then the previous flight stays active.
3. **Offline.** The SDK rotates the flight's creatives and counts metrics locally,
   per day, placement, creative, variant and fallback reason.
4. **Reconnect.** The SDK seals the counters into an HMAC-signed batch with the
   next `seq` and uploads every unacknowledged batch on the next sync.

## Signed envelope

```json
{ "payload": "<base64 of the exact JSON bytes>", "sig": "<base64>", "keyId": "<id>" }
```

The signature covers the exact payload bytes, so the receiver never
re-serializes before verifying.

| Signed object | Algorithm | `keyId` |
| --- | --- | --- |
| Flight | RSA-2048 PKCS#1 v1.5, SHA-256 | Server signing key id. Public keys ship as JWK (`kid`, `n`, `e`) from `get_sdk_config` and are embedded in the app. |
| Batch | HMAC-SHA256 with the base64-decoded `installKey` | `installId` |

## `POST /v1/sync`

### Request

```json
{
  "protocol": 1,
  "appKey": "…",
  "installId": "16–64 chars [A-Za-z0-9_-]",
  "platform": "ios | android | web | desktop",
  "locale": "es | es-AR",
  "sdkVersion": "0.1.0",
  "currentFlightId": "fl_…",
  "currentFlightExpiresAt": 1790000000000,
  "batches": [ /* batch envelopes, oldest first, at most 256 */ ]
}
```

- `locale` is `language[-REGION]`. The SDK reduces OS tags to that form, so
  `es-419` becomes `es` and `zh-Hans-CN` becomes `zh-CN`.
- `currentFlightId` and `currentFlightExpiresAt` are sent once the device holds a
  flight. `currentFlightExpiresAt` uses the device's receipt time plus the flight
  validity.

### Response

```json
{ "acks": [3, 4], "rejected": [{ "seq": 5, "reason": "batch signature mismatch" }], "installKey": "…", "flight": { /* envelope */ } }
```

- `installKey` is present only on the registering call. Batches sent before the
  install exists are rejected with `unknown install`.
- `acks` lists the accepted `seq` values. A `seq` at or below the install's
  highest accepted `seq` is acknowledged again without being counted twice, so
  retries are safe.
- `rejected` lists batches dropped for good: bad signature, schema error, unknown
  placement, a day outside the accepted window
  (`ingest.maxRowAgeDays` back, one day ahead), completions or closes above
  impressions, or totals above the plausibility caps per install and day
  (`ingest.maxImpressionsPerInstallDay`, `ingest.maxViewMsPerInstallDay`). The
  SDK deletes acknowledged and rejected batches alike.
- Rows of a creative that no longer exists, deleted after the device received
  its flight, are dropped and the rest of the batch is acknowledged.
- `flight` comes back when the device has none, holds a different flight id for
  its (app, platform, locale), or its flight expires within
  `flights.refreshBeforeMs`.

HTTP errors: `401` for an unknown `appKey`, `409` for an install that belongs to
another app, `400` for an invalid body.

## Flight payload

```json
{
  "protocol": 1,
  "flightId": "fl_…",
  "installId": "…",
  "appId": "app_demo",
  "platform": "ios",
  "locale": "es",
  "issuedAt": 1790000000000,
  "expiresAt": 1790604800000,
  "expiredPolicy": "rotate | soft | hard",
  "placements": [
    {
      "placementId": "pl_demo_interstitial",
      "format": "interstitial",
      "maxDurationMs": 18000,
      "closeAfterMs": 5000,
      "rotationMs": 15000,
      "capPerSession": 0,
      "capPerDay": 0,
      "cooldownMs": 240000,
      "creatives": [{ "creativeId": "cr_…", "weight": 1 }]
    }
  ],
  "creatives": [
    {
      "creativeId": "cr_…",
      "revision": 1,
      "variantKey": "es.ios",
      "locale": "es",
      "platform": "ios",
      "entry": "index.html",
      "durationMs": 18000,
      "progressSteps": 3,
      "strings": { "headline": "…", "connect": "…" },
      "links": { "store": "https://…", "whatsapp": "https://wa.me/…" },
      "onlineLinks": ["whatsapp"],
      "files": [{ "path": "index.html", "sha256": "…", "size": 1234 }],
      "posters": { "interstitial:9:16": "<sha256>", "banner:320x50": "<sha256>" }
    }
  ]
}
```

- A flight is issued to one install. The SDK rejects a flight whose `installId`
  is not its own.
- Validity is measured from the moment the device receives the flight, not from
  `issuedAt`. The SDK's `FlightClock` takes the later of the wall clock, the
  latest wall time it has seen and the accumulated play time since receipt, so
  turning the device clock back does not extend a flight.
- `capPerSession` and `capPerDay` cap impressions per placement; `0` means no
  cap. `cooldownMs` is the play time an interstitial placement waits after one
  of its interstitials closes before it plays another; `0` means none, and
  banners ignore it, as they ignore `closeAfterMs`.
- `expiredPolicy` is the A/B arm for this install. Each batch records it, so
  reports split delivery by arm.
- `files` holds only the resolved variant: overrides already applied, override
  sources and `manifest.json` left out. File paths are relative, never absolute,
  and never contain `..`.
- `onlineLinks`, present only when not empty, names the links the manifest
  marks `"online": true`. Tapped offline, the SDK covers the creative with a
  dark layer showing `strings.connect` and opens the link once the device is
  online. Closing the interstitial drops the wait. A banner waits for up to
  `BannerConnectSeconds` (`FlightmodePolicy`, 90 s) and then plays on. If the
  game hides the banner meanwhile, it goes at once and the link keeps waiting.
  If the game replaces it with another banner, the SDK keeps it until the link
  opens or the wait ends. The SDK rejects a variant with `onlineLinks` and no `connect` string.
- `posters` are frames rendered by the server for each format slot. The SDK
  shows them where no WebView is available, such as the Unity editor.

## Batch payload

```json
{
  "protocol": 1,
  "installId": "…",
  "seq": 7,
  "flightId": "fl_…",
  "expiredPolicy": "rotate",
  "createdAt": 1790000000000,
  "accountHash": "<sha256 hex, only with consent>",
  "rows": [
    {
      "day": "2027-01-15",
      "placementId": "pl_demo_interstitial",
      "creativeId": "cr_…",
      "variantKey": "es.ios",
      "fallbackReason": "offline | ad_blocked | no_fill | direct",
      "impressions": 3,
      "viewMs": 54035,
      "closes": 0,
      "ctaAttempts": 1,
      "completions": 3
    }
  ]
}
```

- `seq` grows by one per sealed batch and starts at 1.
- `day` is the UTC day of the impression.
- `fallbackReason` is decided by the SDK: `offline` (offline by choice, or for
  longer than a passing loss of signal), `ad_blocked` (online behind a DNS ad
  blocker), `no_fill` (online, the mediation had nothing) or `direct` (shown
  without asking the mediation).
- `viewMs` counts only time on screen after the creative called `ready()`, with
  the app in the foreground.
- `ctaAttempts` counts taps on a known link id, online or not. Offline, the SDK
  opens the URL at once, since an app that takes the link can work offline,
  except for a link in `onlineLinks`, which waits for the internet.
- `accountHash` is `sha256(appKey + ":" + accountId)`, sent only when the app
  passed an account id with the user's consent. The server salts it again and
  feeds it into a HyperLogLog per creative and day to count unique accounts.

## `GET /v1/files/:sha256`

Returns the immutable bytes of a file, with
`cache-control: public, max-age=31536000, immutable`. The SDK verifies the
SHA-256 and the size from the flight before storing the file.

## Fixtures

`packages/protocol/fixtures`, mirrored in the Unity SDK at `Tests~/fixtures`,
holds a signed flight and batch built from the example creative with a
test-only key. The JavaScript tests and the C# tests read the same files, which
keeps both sides
byte-compatible.
