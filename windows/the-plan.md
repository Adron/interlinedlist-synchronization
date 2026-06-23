# InterlinedList Sync — Windows: Implementation Plan

## Summary

Build a native Windows system-tray synchronization client that bidirectionally syncs a user's InterlinedList documents (from `https://interlinedlist.com`) to a local folder on their machine. The app runs silently in the background, surfaces through a notification-area icon, and provides toast notifications for sync events.

---

## Recommended Technology Stack

### Language & Runtime
| Component | Choice | Rationale |
|-----------|--------|-----------|
| Language | **C# 13** | Native Windows citizen, first-class WinRT/Win32 interop, strong async story |
| Runtime | **.NET 9** (net9.0-windows10.0.19041.0) | Current release, 3-year support, full Windows API surface |

### UI & System Tray
| Component | Choice | Rationale |
|-----------|--------|-----------|
| Settings / onboarding UI | **WPF** (Windows Presentation Foundation) | Most battle-tested choice for tray-first apps; mature MVVM ecosystem; `Hardcodet.Wpf.TaskbarNotification` is the de facto standard for `NotifyIcon` hosting in managed code |
| Tray icon | **`Hardcodet.Wpf.TaskbarNotification`** | Wraps `System.Windows.Forms.NotifyIcon` with WPF data-binding; context menus, balloon tips, and tooltips all first-class |
| Toast notifications | **`Microsoft.Windows.AppNotifications`** (Windows App SDK) | Modern, Action Center–integrated toasts; avoids deprecated `BalloonTipText` |

> **Why WPF over WinUI 3?** WinUI 3 is Microsoft's forward direction, but it has no first-class `NotifyIcon` API as of Windows App SDK 1.6 — tray support requires WinForms interop that adds complexity without benefit. For a tray-primary app, WPF + `Hardcodet.Wpf.TaskbarNotification` is the pragmatic, production-proven choice. The entire UI surface for this app is one settings window and a context menu; WPF handles that with zero friction.

### Background Services & DI
| Component | Choice |
|-----------|--------|
| Application host | `Microsoft.Extensions.Hosting` (`IHostedService` for sync loop) |
| Dependency injection | `Microsoft.Extensions.DependencyInjection` |
| Configuration | `Microsoft.Extensions.Configuration` + `appsettings.json` in `%APPDATA%\interlinedlist-sync\` |
| Logging | `Microsoft.Extensions.Logging` + **Serilog** (rolling file sink to `%LOCALAPPDATA%\interlinedlist-sync\logs\`) |

### Networking & API
| Component | Choice |
|-----------|--------|
| HTTP client | `System.Net.Http.HttpClient` via `IHttpClientFactory` |
| JSON serialisation | `System.Text.Json` (source-generated, zero-reflection) |
| Retry / resilience | **Polly** (`AddHttpClient` + `AddTransientHttpErrorPolicy`) |

### Storage
| Component | Choice |
|-----------|--------|
| Sync state database | **SQLite** via `Microsoft.Data.Sqlite` (in-process, no server) |
| Credential / token storage | **Windows Credential Manager** (`Windows.Security.Credentials.PasswordVault`) |
| User preferences | `appsettings.json` via `Microsoft.Extensions.Configuration` |

### File System
| Component | Choice |
|-----------|--------|
| Directory watching | `System.IO.FileSystemWatcher` (recursive, `InternalBufferSize` = 65536) |
| Debounce | `System.Threading.Timer` (500 ms coalesce window) |

### Testing
| Component | Choice |
|-----------|--------|
| Unit test framework | **xUnit** |
| Assertions | **FluentAssertions** |
| Mocking | **Moq** |
| HTTP mocking | **RichardSzalay.MockHttp** (`MockHttpMessageHandler`) |
| File system abstraction | **System.IO.Abstractions** (`IFileSystem` / `MockFileSystem`) |
| Integration API stubs | **WireMock.Net** |
| UI automation (smoke) | **WinAppDriver** (Appium for Windows) |

### Packaging & Distribution
| Component | Choice |
|-----------|--------|
| Primary installer | **MSIX** via Windows App SDK packaging project (sideload or Store) |
| Fallback installer | **WiX Toolset v4** MSI (for enterprise / Group Policy scenarios) |
| Code signing | EV certificate (Azure Key Vault signing in CI) |
| Auto-start | MSIX `windows.startup` extension (Task Scheduler bridge) — no registry `Run` key |

### CI/CD
- **GitHub Actions** on `windows-latest` runners
- Steps: build → unit test → coverage gate (80 %+) → MSIX package → sign → upload artefact

---

## Solution Structure

```
windows/
  InterlinedSync.sln
  InterlinedSync/                        # Main WPF application
    App.xaml / App.xaml.cs               # WPF application entry; hosts IHost
    Program.cs                           # Host builder, DI registration
    SystemTray/
      TrayIconController.cs              # Hardcodet TaskbarIcon host + context menu
      TrayMenuBuilder.cs                 # Builds WPF ContextMenu from sync state
    Sync/
      SyncEngine.cs                      # IHostedService; orchestrates push + pull loops
      SyncStateRepository.cs             # SQLite-backed document/folder state
      ConflictResolver.cs                # IConflictStrategy implementations
      UploadQueue.cs                     # Channel<T>-based upload producer/consumer
    API/
      InterlinedListClient.cs            # HttpClient wrapper; all API calls
      IInterlinedListClient.cs           # Interface (testability / LSP)
      Models/
        Document.cs                      # API response DTOs (record types)
        Folder.cs
        DeltaResponse.cs
    Auth/
      AuthManager.cs                     # OAuth / token acquisition + refresh
      IAuthProvider.cs
    Storage/
      CredentialManager.cs               # PasswordVault P/Invoke wrapper
      PreferencesManager.cs              # Typed IOptions<SyncPreferences>
    FileSystem/
      FileWatcher.cs                     # FileSystemWatcher + debounce timer
      FileMapper.cs                      # local path <-> server document ID
    Notifications/
      NotificationService.cs             # AppNotifications toast dispatch
    UI/
      ViewModels/
        OnboardingViewModel.cs
        SettingsViewModel.cs
        SyncStatusViewModel.cs
      Views/
        OnboardingWindow.xaml/.cs        # First-run sign-in + folder selection
        SettingsWindow.xaml/.cs          # Ongoing configuration
      Converters/                        # IValueConverter implementations
      Resources/
        Styles.xaml
        Icons.xaml
    Assets/
      tray-idle.ico
      tray-syncing.ico
      tray-error.ico
      tray-paused.ico
  InterlinedSync.Tests/                  # xUnit unit tests
  InterlinedSync.IntegrationTests/       # WireMock.Net + real SQLite tests
  InterlinedSync.UITests/               # WinAppDriver smoke tests
  InterlinedSync.Package/               # MSIX packaging project
    Package.appxmanifest
```

---

## Feature Breakdown & Implementation Phases

### Phase 1 — Core Infrastructure
- [ ] `Program.cs`: `IHostBuilder` wiring; WPF application starts as a hosted background app (no main window, Dock/taskbar hidden)
- [ ] `TrayIconController`: loads `TaskbarIcon` from XAML resource; wires up context menu actions to `ICommand`s; updates icon based on `SyncState` events
- [ ] `CredentialManager`: thin wrapper around `PasswordVault`; `SaveToken` / `LoadToken` / `DeleteToken`
- [ ] `PreferencesManager`: typed `IOptions<SyncPreferences>` backed by `appsettings.json`; exposes `SyncFolder`, `PollIntervalSeconds`, `AutoStart`
- [ ] Serilog setup: rolling file log in `%LOCALAPPDATA%\interlinedlist-sync\logs\`

### Phase 2 — Authentication
- [ ] `AuthManager`: OAuth sign-in via `WebAuthenticationBroker` (or system browser redirect with custom URI scheme `interlinedsync://auth`); stores access token in `PasswordVault`
- [ ] Token validation on startup: if token missing → show `OnboardingWindow`; if token present → proceed to sync
- [ ] `OnboardingWindow`: sign-in prompt + local sync folder picker (`FolderBrowserDialog`)
- [ ] Sign-out action in tray menu: revokes token, clears `PasswordVault` entry, resets sync state DB

### Phase 3 — Sync Engine (Pull)
- [ ] `SyncStateRepository`: SQLite schema (`documents`, `folders`, `sync_log`); migrations with `IF NOT EXISTS`
- [ ] `InterlinedListClient.GetDeltaAsync()`: calls `GET /api/documents` (or delta endpoint) with Bearer token; deserialises to `DeltaResponse`
- [ ] `SyncEngine` pull loop: `IHostedService`; polls every `PollIntervalSeconds`; compares server state with SQLite state; downloads new/updated documents; writes to `SyncFolder`
- [ ] `FileMapper`: bidirectional map of `local path ↔ server document ID`; persisted in SQLite
- [ ] Tray icon state: idle → syncing → idle; error icon on API failure

### Phase 4 — Sync Engine (Push) — **Complete**
- [x] `IFileWatcher` + `FileSystemWatcherService`: `FileSystemWatcher` filtered on `*.md`, 500 ms debounce backed by an injectable `TimeProvider`, surfaces `LocalChange` records via `Channel<LocalChange>`
- [x] `SyncEngine.PushOnceAsync`: consumes the watcher channel inside `ExecuteAsync`; routes Created/Modified to `POST` (new) or `PUT` (known); skips when the SHA-256 hash is unchanged
- [x] `SyncEngine.PushDeleteAsync`: maps local path to document id via the repository, calls `DELETE /api/documents/{id}`, removes the local record
- [x] Renames: treated as delete-old + create-new; `LocalChangeKind.Renamed` events carry both paths
- [x] Push/pull serialization: `SemaphoreSlim(1,1)` shared between `RunOnceAsync` and `PushOnceAsync` so the two pipelines never collide on the same record
- [x] `IInterlinedListClient` extended with `CreateDocumentAsync`, `UpdateDocumentAsync`, `DeleteDocumentAsync` (404 on delete treated as success)
- [x] DI wiring in `Program.cs` registers `IFileWatcher -> FileSystemWatcherService` as a singleton; the sync engine starts the watcher on the configured sync folder
- [x] Tests: `FileSystemWatcherServiceTests` (6) cover debounce, filter, and event kinds against a real temp directory; `SyncEngineTests` push scenarios (7) cover create / update / hash-unchanged / delete / rename / error using `MockHttpMessageHandler` + `StubFileWatcher`

### Phase 5 — Conflict Resolution
- [ ] Detect conflict: document modified locally since last sync AND modified on server since last sync
- [ ] Default strategy: **backup + last-writer-wins** — renames local copy to `<name>.conflict-<timestamp>.md`, writes server version to canonical path
- [ ] `IConflictStrategy` interface: allows future alternate strategies (manual merge, local-wins, server-wins)

### Phase 6 — Notifications & Error Handling
- [ ] `NotificationService`: wraps `AppNotificationBuilder`; sends toasts for sync complete, conflict detected, error, sign-in required
- [ ] Retry policy: Polly `WaitAndRetry` (3 attempts, exponential backoff) for transient HTTP errors
- [ ] Offline detection: `NetworkInformation.GetInternetConnectionProfile()` — pause sync loop, resume on reconnect
- [ ] Error tray state: red icon + "Last sync failed" tooltip; click opens settings with error detail

### Phase 7 — Settings UI
- [ ] `SettingsWindow`: sync folder path (change via folder picker), poll interval slider, auto-start toggle, sign-out button, "Open sync folder" shortcut, version/log path display
- [ ] Auto-start toggle: registers/deregisters MSIX `windows.startup` task via `Windows.ApplicationModel.StartupTask`
- [ ] `SyncStatusViewModel`: exposes `LastSyncTime`, `DocumentCount`, `SyncStatus` as `[ObservableProperty]` (CommunityToolkit.Mvvm)

### Phase 8 — Packaging & Distribution
- [ ] `Package.appxmanifest`: `runFullTrust`, `windows.startup`, `interlinedsync://` protocol handler
- [ ] MSIX build in CI: `msbuild InterlinedSync.Package.wapproj /p:Configuration=Release /p:Platform=x64`
- [ ] WiX MSI as secondary installer for enterprise deployment without Store access
- [ ] Code signing pipeline: Azure Key Vault + `AzureSignTool`

---

## Key Architectural Decisions

| Decision | Choice | Why |
|----------|--------|-----|
| App host model | `IHostedService` inside WPF app | Clean shutdown, DI, `ILogger` — no `Application.Current` singletons |
| Tray icon | `Hardcodet.Wpf.TaskbarNotification` | Zero friction; WPF data-binding on context menus |
| Sync state | SQLite file | Same approach as macOS app; survives crashes; enables delta sync |
| Token storage | `PasswordVault` | Windows-native, OS-encrypted, survives user session; never cleartext |
| Push mechanism | `Channel<T>` + `IHostedService` | Decouples file watcher (producer) from HTTP upload (consumer); backpressure built in |
| Config store | `appsettings.json` in `%APPDATA%` | Simple, human-editable, `IConfiguration`-compatible; avoids registry coupling |
| Auto-start | MSIX startup extension | Declared, auditable, revocable — not a stealth registry key |

---

## Open Questions — Clarification Needed

The following questions need answers before implementation begins. Each is marked with the phase it blocks.

### API & Authentication
1. **[Phase 2] What authentication mechanism does the InterlinedList API use?**
   The agent definition assumes OAuth 2.0 with bearer tokens (`il_tok_...`). Is this a standard OAuth 2.0 authorization code flow, or a long-lived API key the user pastes in, or something else? This determines whether a browser-based OAuth redirect is needed or just a "paste your token" input field.

2. **[Phase 2] Is there a token refresh mechanism, or are tokens long-lived / non-expiring?**
   Long-lived tokens (no refresh needed) are simpler; short-lived tokens with refresh require a refresh loop running alongside the sync engine.

3. **[Phase 3] Does the API have a delta/incremental sync endpoint, or must the client fetch the full document list every poll cycle?**
   A delta endpoint (changes since cursor/timestamp) is critical for scalability — fetching the full list every 30 s is expensive at scale. If no delta endpoint exists, what is the expected document count per user (rough order of magnitude)?

4. **[Phase 4] What are the API endpoints and HTTP methods for document create, update, and delete?**
   The README links to `https://interlinedlist.com/help/api` — have the exact endpoint paths, request shapes, and response schemas been confirmed? (e.g., `POST /api/documents`, `PUT /api/documents/{id}`, `DELETE /api/documents/{id}`?)

5. **[Phase 4] Does the API support uploading document content as plain text / Markdown, or is there a multipart/binary upload format?**

### Document Format & Folder Structure
6. **[Phase 3] What file format are InterlinedList documents stored as locally?**
   Markdown (`.md`)? Plain text (`.txt`)? A proprietary format? This affects file naming, the watcher filter, and how conflict copies are named.

7. **[Phase 3] Does the server folder hierarchy map 1:1 to the local file system?**
   If the server has nested folders (Folder A → Sub-folder B → Document C), should the local sync path be `SyncFolder/A/B/C.md`? What happens when a document is moved on the server — is that a rename, delete+create, or a dedicated move event?

8. **[Phase 4] How are document names and IDs related?**
   Does each document have a stable server-side ID that persists across renames? This is essential for the `FileMapper` and for avoiding duplicate uploads when a file is renamed locally.

### Sync Behaviour
9. **[Phase 5] What is the expected conflict resolution behaviour from the user's perspective?**
   The macOS app uses backup + last-writer-wins. Should the Windows app match this exactly, or is a different strategy (e.g., "server always wins", or a user-visible conflict indicator in the tray menu) preferred?

10. **[Phase 3/4] Should sync be bidirectional from day one, or is phase 1 server→local (pull) only, with push added later?**
    Bidirectional is the end state per the README, but a phased approach (pull first, validate, then add push) reduces initial risk.

11. **[Phase 3] What is the minimum polling interval? Is there a server-side rate limit?**
    30 seconds is the macOS app's default. Is that appropriate for Windows too, or should it be user-configurable down to a lower floor (e.g., 10 s)?

### Platform & Distribution
12. **[Packaging] What is the minimum supported Windows version?**
    `net9.0-windows10.0.19041.0` (Windows 10 2004, build 19041) is the current plan. Windows 11 only would simplify some WinUI 3 paths. Is Windows 10 support required?

13. **[Packaging] What is the distribution channel: Microsoft Store, direct MSIX download, MSI/EXE installer, or some combination?**
    This affects the packaging project setup, signing requirements, and whether `runFullTrust` packaging applies.

14. **[Packaging] Is an EV code-signing certificate already in place, or does that need to be acquired?**
    Unsigned MSIX installers require enabling Developer Mode on the target machine; signed packages install silently.

### Scope
15. **Should the Windows app be a close functional port of the macOS app, or can it diverge in UX where Windows conventions differ?**
    For example: the macOS app is a menu bar icon (top of screen); Windows puts tray icons in the notification area (bottom-right). The core behaviour is the same, but context menu structure and settings window layout may reasonably differ.

16. **Is multi-account support (multiple InterlinedList accounts on one machine) in scope for v1?**

17. **Is there a target ship date or milestone that should shape which phases are in scope for the first release?**
