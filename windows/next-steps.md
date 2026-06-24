# Next Steps — Phase 7 (Settings UI)

Phase 6 (Notifications + Error Handling) shipped: a typed `INotificationManager`
fronted by a one-method `INotificationDispatcher` seam, an `INetworkMonitor`
that pauses the engine on offline / resumes on reconnect, typed
`AuthExpiredException` / `RateLimitedException` thrown from
`InterlinedListClient`, and a `Retry-After`-aware in-engine backoff loop.

## Phase 6 — done

- `Notifications/INotificationManager.cs` + `WindowsAppNotificationManager.cs`
  with action labels for "Open Folder" / "Retry" / "Open Log" / "Open File" /
  "Sign In". `LoggingNotificationDispatcher` is the cross-platform fallback;
  the Windows App SDK dispatcher will replace it once the
  `Microsoft.WindowsAppSDK` package is wired in (sparse package or MSIX).
- `Notifications/Mocks/MockNotificationManager.cs` — records every call for
  test assertions.
- `Network/INetworkMonitor.cs` + `NetworkInformationMonitor.cs` (wraps
  `System.Net.NetworkInformation.NetworkChange`; on Windows the production
  monitor will additionally subscribe to
  `Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged`
  once the WinRT projection is enabled).
- `Errors/AuthExpiredException.cs` + `RateLimitedException.cs` (both extend
  `ApiException`; `ApiException` is no longer `sealed`).
- `Sync/SyncState.cs` — added `Offline` and `AuthExpired`.
- `Sync/SyncEngine.cs` — pause/resume gates, notification fan-out, in-engine
  retry-with-jitter for 429, `ResumeAfterReauth()` for the tray to call after
  the user re-signs in.
- `Configuration/SyncPreferences.cs` — `NotifyOnSyncCompletion`,
  `NotifyOnErrors`, `NotifyOnConflicts` (all default `true`).
- `Program.cs` — registered `INetworkMonitor`, `INotificationDispatcher`, and
  `INotificationManager` as singletons.
- Tests: 24 new unit tests (122 -> 146 green) covering notification gating,
  network-state transitions, auth-expired pause + resume, offline pause +
  reconnect, 429 retry, completion notification, and conflict notification.

## Phase 7 goals

1. **Surface the new preferences in the Settings window.** Add toggles for
   `NotifyOnSyncCompletion`, `NotifyOnErrors`, `NotifyOnConflicts`. Bind via
   `SettingsViewModel` -> `IPreferencesStore`.
2. **Hook the tray's "Sign In" item** to `SyncEngine.ResumeAfterReauth()` so
   the engine actually un-pauses after a successful re-auth round-trip.
3. **Swap the dispatcher.** Add `Microsoft.WindowsAppSDK` (sparse package /
   MSIX) and replace `LoggingNotificationDispatcher` with a real
   `AppNotificationManager.Default.Show(builder.BuildNotification())` call.
   Wire `AppNotificationManager.Default.NotificationInvoked` to a router that
   resolves the `arguments` query string back to a `ITrayCommandHandler` call.
4. **Run the engine through a smoke test on Windows** to confirm
   `NetworkChange.NetworkAvailabilityChanged` fires for the scenarios that
   matter (Wi-Fi off, VPN drop, captive portal).

## Open questions for Phase 7

- Windows App SDK toasts require either MSIX packaging or a sparse package
  + `AppNotificationManager.Default.Register()` at startup. The packaging
  project (`InterlinedSync.Package`) is already an MSIX `.wapproj` so the
  MSIX path is straightforward — confirm before sparse-package work begins.
- Where does the "Open Log" action open: the current `interlinedsync-.log`
  file directly, or the containing folder? (Folder is safer if the file is
  rotating.)
- Should we add an offline-aware delay to push attempts so the channel
  doesn't back up while paused, or is the existing pause check sufficient?
