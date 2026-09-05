# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

A Unity 6 (6000.0.64f1) mobile app that scans QR codes on paper/digital tickets and checks them in
against a Firestore `tickets` collection in real time. There is no custom build/CI tooling — this is
edited and built entirely through the Unity Editor.

- Company/Product: LumiShow / SonThanThuyQuai_TicketManager
- Firebase project id: `sonthanthuyquai-ticket` (see `Assets/google-services.json` and
  `Assets/StreamingAssets/google-services-desktop.json` for the Editor/desktop config)
- Render pipeline: Universal RP (2D)
- Comments and Inspector tooltips in the codebase are written in Vietnamese; match that convention
  when editing existing files.

## Working in this repo

- There is no CLI build/lint/test workflow — this is a Unity project, opened and built via the Unity
  Editor (Unity 6000.0.64f1, see `ProjectSettings/ProjectVersion.txt`). Open the project in the Editor
  to compile, run in Play Mode, or produce a build.
- The `com.unity.test-framework` package is installed but there are no test assemblies in `Assets/` —
  no test suite currently exists in this project.
- All gameplay/app code lives in `Assets/Scripts/` — currently 6 scripts, each with a single
  clear responsibility (see Architecture below). Don't assume there are more subsystems than this.
  `Assets/Editor/AdminPanelUIBuilder.cs` is a 7th, Editor-only script (excluded from player builds by
  virtue of living under a folder literally named `Editor`) — see the `AdminPanelController` entry below.
- `Assets/Scenes/SampleScene.unity` is the (only) app scene; it wires up the `CameraScanner`,
  `TicketManager`, and `FirebaseManager` components via the Inspector, so behavior depends on Inspector
  field assignments in the scene, not just on the C# code. The main `Canvas` (holding the camera-preview
  `RawImage`) is Screen Space-Overlay with a Scale-With-Screen-Size `CanvasScaler` (1080x1920, match
  0.5) — matches `AdminCanvas`'s scaler config, keeps UI sizing consistent across devices, and avoids
  any dependency on Main Camera's projection/clipping settings. Keep any new Canvas consistent with
  this rather than Screen Space-Camera.

## Architecture

The app is a straight-line pipeline with one event hop, spread across four scripts, plus a fifth
standalone script for staff admin operations and a sixth small local-cache helper:

```
CameraScanner (WebCamTexture + ZXing decode)
   --OnQRCodeScanned event-->  TicketManager (format validation)
        --checks-->  LocalCheckInCache (skip Firestore if already known)
        --calls-->  FirebaseManager (Firestore query + check-in)
        --on success/already-checked-in-->  LocalCheckInCache.Add
   <--UpdateScanStatus()--  TicketManager  <--callback--  FirebaseManager
CameraScanner --draws--> QRBoxUI (runtime-built overlay)

AdminPanelController (independent of the scan pipeline above)
   --HTTPS (UnityWebRequest)--> backend /api/admin/* (PIN-gated, Admin SDK)
   --clears (local only, no backend call)--> LocalCheckInCache
```

- **`CameraScanner.cs`** — Owns the `WebCamTexture`, orientation/aspect-cover math for the camera
  preview, and QR decoding via ZXing.Net. Decoding runs on a background thread via `Task.Run` (never
  block the main thread with `barcodeReader.Decode`), throttled by `decodeInterval`, reusing a pixel
  buffer to avoid per-frame GC allocations. Exposes `OnQRCodeScanned` (fired once per *new* QR value
  seen) and `UpdateScanStatus(qrValue, ScanState, message)` (called back by consumers once a scan
  result is known, to recolor/relabel the on-screen box). Also owns `ShowDetectionPreviewBox` — an
  optional "QR detected but not yet decoded" preview box fed by a ZXing `ResultPointCallback` invoked
  on the background decode thread (must not touch Unity APIs there; see `OnPossibleResultPointFound`).
  Coordinate math between camera pixel space, ZXing's internal auto-rotated space, and UI local space
  is nontrivial (`UnrotateResultPoint`, `RawPixelToLocal` vs `PixelToLocalRaw`) — read the existing
  comments before touching orientation/rotation logic, they encode hard-won fixes for
  `AspectRatioFitter` limitations and ZXing's `AutoRotate` coordinate space. `UpdateBoxCorners`
  computes the QR's true outer corners from the 3 finder-pattern centers ZXing returns, extended
  outward by 3.5 modules using `FinderPattern.EstimatedModuleSize` (`ZXing.QrCode.Internal`) — this
  is exact for any QR version/module count, unlike a fixed scale factor (`QRBoxUI.boxScale` is now
  just a small cosmetic padding on top of that, not a correctness hack). Explicitly requests the
  WebCam permission (`Application.RequestUserAuthorization`) before creating the `WebCamTexture`, and
  surfaces a status label (built at runtime, sibling of `RawImage` so it doesn't inherit its rotation)
  for "starting up" / "permission denied" / "no camera found" — previously any of those left the
  screen silently black forever with no feedback for gate staff. Has a **Test Mode**
  (`testModeUseMainCamera`, Editor-only in practice) that swaps the frame source from
  `WebCamTexture` to whatever is actually on **Display 1** — lets you test the full real pipeline
  (decode → `OnQRCodeScanned` → `TicketManager` → `FirebaseManager.CheckInTicket` → box recolor) in
  the Editor without a working webcam. `StartTestMode()` points a Scene `Camera` (`testCamera`,
  defaults to `Camera.main`) at `targetDisplay = 0`/`targetTexture = null` (i.e. renders normally,
  not into a `RenderTexture`) and disables `rawImage` itself (no camera-feed preview needed — the
  `QRBoxUI` child stays active/visible); each decode tick runs `CaptureScreenAndDecode()`, a
  coroutine that `yield return new WaitForEndOfFrame()` then `ScreenCapture.CaptureScreenshotAsTexture()`
  to grab a single frame — this was a deliberate redesign away from an earlier `RenderTexture`
  approach, which could set up a camera-renders-its-own-output feedback loop ("hai gương đối diện
  nhau") if the Canvas showing that texture was ever visible to the same camera; a one-shot
  post-render screenshot has no such loop by construction. `FrameWidth`/`FrameHeight` properties
  abstract over both frame sources (device camera vs. Test Mode's last screenshot) so the
  coordinate math (`UnrotateResultPoint`, `RawPixelToLocal`) doesn't need to know which is active —
  `ApplyCameraOrientation` (rotation/mirror/cover-fit for the on-screen preview) only matters for
  the real `WebCamTexture` path, since Test Mode's camera draws straight to the screen. **Camera preview maintenance note:** `SampleScene`'s `RawImage` uses stretch anchors (`anchorMin = 0,0`, `anchorMax = 1,1`), so `rawImageUsesStretchedAnchors` must stay enabled. When it was disabled, `ApplyCameraOrientation()` treated `sizeDelta` as an absolute size instead of the overflow beyond the parent rect, which stretched the phone preview vertically and made it look flattened. The cover calculation must use the actual `WebCamTexture.width/height` after the device has initialized, account for the 90/270-degree rotation by swapping the parent's effective display dimensions, and preserve the camera aspect ratio while cropping only the excess (cover, not non-uniform stretch). The scene currently requests 1920x1080 at 30 FPS; the device may negotiate another supported mode, so all runtime coordinate math must continue reading the resolved texture dimensions rather than the requested values. `QRBoxUI` keeps the status label as a sibling overlay under the QR root, but its position must be derived from the outward normal of the QR's top edge (not a fixed global `+Y` offset), otherwise a rotated QR can place the label beside the box instead of above it.
- **Permission lifecycle note:** `RequestCameraPermissionThenStart()` is guarded against duplicate
  runs, and `OnApplicationFocus(true)` retries the permission check when the app returns from Android
  Settings. This lets a user grant camera access without force-closing and reopening the app; do not
  reduce permission handling to a one-time `Start()` check.
- **`TicketManager.cs`** — Subscribes to `CameraScanner.OnQRCodeScanned`, does a cheap regex format
  check (`ticketCodePattern`, purely to filter obvious junk QR content — *not* a ticket-code schema,
  since ticket codes are random), checks `LocalCheckInCache.Contains` (short-circuits straight to
  `ScanState.Invalid` with no Firestore read if this exact code was already resolved by this device),
  then delegates to `FirebaseManager.CheckInTicket`. On a result where `Success` or `AlreadyCheckedIn`
  is true (both are permanent, won't-change-on-retry outcomes), adds the code to `LocalCheckInCache`
  so a repeat scan (QR held in frame too long and re-detected, or re-presented later) never re-hits
  Firestore for an answer that's already known. Reports the result back to
  `CameraScanner.UpdateScanStatus`. Also has an Editor-only `[ContextMenu]` action
  (`ResetDebugTicket`) to un-checkin a ticket by code for repeated manual testing.
- **`FirebaseManager.cs`** — Singleton (`FirebaseManager.Instance`) wrapping Firebase/Firestore.
  Initializes async in `Start()` via `FirebaseApp.CheckAndFixDependenciesAsync`; `IsReady` gates all
  Firestore calls. `CheckInTicket` queries the `tickets` collection by the `ticketCode` field (not the
  document ID), rejects if already `checkedIn` (setting `TicketCheckInResult.AlreadyCheckedIn = true`
  — distinct from `Success = false` on a transient error like a dropped connection or a nonexistent
  code, so `TicketManager` knows which failures are safe to cache permanently), otherwise sets
  `checkedIn = true` + `checkedInAt = Timestamp.GetCurrentTimestamp()`. All Firestore task
  continuations use `ContinueWithOnMainThread`, so callback bodies can call Unity APIs directly.
  `ResetTicketCheckIn` is a test-only helper (used by `TicketManager`'s debug context menu) — never
  call it from production flows.
- **`LocalCheckInCache.cs`** — Static helper (no MonoBehaviour, no Scene presence) backing a small
  on-device cache of ticket codes this app has already resolved a check-in for — the *only* reason
  it exists is to avoid a wasted Firestore read on a repeat scan of the same code (a QR sitting in
  frame past `maxMissFrames`, or a ticket re-presented later), directly in service of the
  avoid-mass-scan-quota-waste principle this project follows throughout. Backed by 1 flat JSON file
  under `Application.persistentDataPath` (Unity's standard per-platform writable-data path), loaded
  lazily into an in-memory `HashSet<string>` on first use, written synchronously on every `Add` (scan
  cadence is human-speed, so this is never a hot path). **Not a source of truth** — Firestore's
  `checkedIn` field always is; clearing this cache (`Clear()`, exposed via Admin Panel — see below)
  is always safe and can never let an already-used ticket back in, it only costs a few redundant
  Firestore reads until the cache repopulates.
- **`QRBoxUI.cs`** — UI overlay built as a child of the `RawImage` showing the camera feed, so it
  automatically inherits whatever rotation/mirroring `CameraScanner.ApplyCameraOrientation()` applies
  to that `RawImage` — no separate rotation compensation needed here. The visual "4 corners"
  viewfinder itself (like typical scanning apps) comes from `Assets/Prefabs/BoxQR.prefab`
  (`CameraScanner.cornerBoxPrefab`, Instantiated once in `QRBoxUI.Create`) — a designer-authored
  RectTransform with 4 `Image` children (one per corner, pre-made art/orientation), NOT built by code.
  `QRBoxUI` only moves/rotates/scales that prefab instance AS ONE RIGID BLOCK to match the real QR
  quad each frame (position = centroid, rotation = top-edge angle, non-uniform scale relative to the
  prefab root's own designed `rect` size — read once at Instantiate time as the size reference, so the
  prefab can be authored at any size) and recolors every `Image` found under it via
  `GetComponentsInChildren<Image>` (name/count-agnostic) — plus a status `Text` label above the box,
  still built at runtime. Has no dependency on Firebase or ZXing types.
- **`AdminPanelController.cs`** — Logic only, no UI construction (unlike `QRBoxUI`): PIN-gated staff
  panel for manual ticket operations at the gate (cancel a ticket by seat; create a walk-up/cash ticket
  with a freshly generated QR). Its `[SerializeField]` panel/InputField/Text/RawImage references and its
  Button `onClick` events are wired up by the Editor tool below, not by this script — it only shows/hides
  the panels it's given and drives the network calls. Deliberately does **not** talk to Firestore
  directly: every write goes through the web backend's `/api/admin/*` routes (PIN checked server-side,
  wrapped in Admin SDK transactions), keeping this app's only direct Firestore access limited to the
  narrow, rule-scoped read/check-in flow in `FirebaseManager.cs`. QR generation uses the encode side of
  the same `zxing.unity.dll` already bundled for decoding (`ZXing.BarcodeWriter`) — no extra plugin.
  The one exception to "every write goes through the backend" is `OnClearLocalCache`, which just
  calls `LocalCheckInCache.Clear()` directly — deliberately **not** routed through the backend/PIN
  check, since it mutates nothing on Firestore (see `LocalCheckInCache.cs`) and reaching this button
  already requires having passed `/admin/verify-pin` to open the menu in the first place.
- **`Assets/Editor/AdminPanelUIBuilder.cs`** — Editor-only tool (menu `LumiShow/Build Admin Panel UI`)
  that builds the actual `AdminCanvas` GameObject hierarchy for `AdminPanelController` — a dedicated
  Screen Space-Overlay `Canvas` (separate from the Scene's main camera-preview `Canvas`, on top via
  `sortingOrder = 100`, so it never has to touch `RawImage`/`CameraScanner`'s per-frame orientation
  logic), plus every panel,
  `Button`, and `InputField` as real Scene objects, editable afterward like any other UI (drag positions,
  tweak colors/fonts in the Inspector). Assigns `AdminPanelController`'s private serialized fields via
  `SerializedObject`/`SerializedProperty` (not direct field access — they're `private`) and wires each
  `Button.onClick` as a real persistent listener via `UnityEditor.Events.UnityEventTools`, so the
  connections show up and are editable in the Inspector exactly like hand-wired events. Safe to re-run —
  detects an existing `AdminCanvas` and asks before destroying/rebuilding it. Remember to save the Scene
  (Ctrl+S) after running it; the tool only marks the Scene dirty, it doesn't save automatically.

### Scan state machine

`ScanState` (defined in `CameraScanner.cs`) drives the box color/label:
`Processing` (yellow, set immediately when a new QR value is first seen) →
`Valid` (green) or `Invalid` (red), set once `TicketManager`/`FirebaseManager` resolve the check-in.
`UpdateScanStatus` ignores stale results for a QR value that's no longer the one currently tracked
(`qrValue != currentQRValue`), which happens if the code leaves the frame before the async Firestore
round-trip completes.

## Firestore data model

Collection `tickets`, one document per ticket, queried by field (not doc ID):
- `ticketCode` (string) — random, no fixed prefix/format assumed by app code
- `checkedIn` (bool)
- `checkedInAt` (Timestamp, set on check-in)
- `customerName` (string, optional — shown in the success message)
