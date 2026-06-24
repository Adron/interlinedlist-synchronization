# Gap Work — InterlinedList Sync

A cross-platform plan covering everything still needed to ship a v1.0 release across macOS, Windows, and Linux. Synthesized from the three per-platform `the-plan.md` / `next-steps.md` files and the live API review against `interlinedlist.com/help/api`.

**Last updated:** 2026-06-23 (after Phase 4 landed)
**Status:** Phase 4 complete on all three platforms; CI green on `main`.

---

## 1. Current state snapshot

| Platform | Phase model | Done | Next |
|---|---|---|---|
| **macOS** | 7 Phases | 1–4 ✓ | Phase 5 (Preferences + Login Item) |
| **Windows** | 8 Phases | 1–4 ✓ | Phase 5 (Conflict Resolution) |
| **Linux** | 7 Milestones | M1–M3 ✓, M4 partial | Finish M4, then M5 (Offline Queue) |

CI status on `CompositeCode/interlinedlist-synchronization`: all three platform workflows green; `.deb` artifact builds cleanly. No `v*` release tag has been cut yet; `release.yml` has never run.

---

## 2. Cross-platform API alignment (do FIRST — blocks Phase 5+)

The three client implementations diverge from the live API in identical ways. Fix once across all three before extending features.

| Concern | API reality | Action |
|---|---|---|
| Update verb | `PATCH /api/documents/[id]` | Change `PUT` → `PATCH` on all three clients |
| Body field name | `content` | macOS only: rename `body` → `content` in `DocumentDTO` |
| Folder model | `Folder { id, name, parentId, createdAt }` + `Document.folderId` + `relativePath` | Add to macOS + Linux (Windows already has model, needs CRUD client methods) |
| Delta sync | `GET /api/documents/sync?lastSyncAt=...` returns `{ syncedAt, folders[], documents[] }` with `deleted: true` tombstones | Adopt on all three to fix tombstone handling natively |
| Auth | Most endpoints session-only; `/sync` accepts Bearer | Verify Bearer works on POST/PATCH/DELETE — needs integration test against live API |

### Open questions still requiring user input
1. **Auth contract** — does the `POST /api/auth/login` Bearer token work on non-`/sync` endpoints? This is the single highest-impact unknown.
2. **`relativePath` semantics** — server-suggested local filename, historical artifact, or path-within-folder?
3. **Folder scope for Phase 5** — bundle with conflict resolution, or ship flat-first and add folders as Phase 5.5?

---

## 3. Remaining per-platform work

### 3.1 macOS — Phase 5: Preferences + Login Item

- `PreferencesView` (SwiftUI) wired to `PreferencesManager`:
  - Sync folder picker (`NSOpenPanel`, store security-scoped bookmark)
  - Poll interval slider (5–300 s)
  - Conflict strategy display (remote-wins + conflict-copy, read-only for v1)
  - Notifications enable/disable toggle (already in `PreferencesManager`, just needs UI)
  - Sign-out button
- `SMAppService` for launch-at-login toggle
- Hot-reload of sync interval when preference changes (currently set once at engine construction)
- Tests: `PreferencesViewModelTests`, `LaunchAgentManagerTests`

### 3.2 macOS — Phase 6: Conflict Resolution + Error Handling

- `ConflictResolver` policy: remote-wins → write `filename.conflict-YYYYMMDD-HHMMSS.md` for local divergence
- Per-document `SemaphoreSlim`-equivalent (Swift actor isolation) so push and pull serialize per file, not globally
- Error surfacing:
  - Auth expired → notification + tray badge → reopen preferences
  - Network offline → pause loop, show "Offline" in menu
  - Rate-limited (HTTP 429) → exponential backoff with jitter
- Tests: `ConflictResolverTests` (covers all five conflict permutations), `RetryPolicyTests`

### 3.3 macOS — Phase 7: Packaging + Distribution

- `make-app.sh` already builds `.app`; add:
  - Code signing (Developer ID Application cert from Apple Developer)
  - Notarization via `notarytool` (Apple ID + app-specific password as repo secrets: `APPLE_ID`, `APPLE_APP_SPECIFIC_PASSWORD`, `APPLE_TEAM_ID`)
  - Stapling
- Optionally `.pkg` installer with `productbuild`
- Update `release.yml` macOS job to sign + notarize before uploading

### 3.4 Windows — Phase 5: Conflict Resolution

- Mirror macOS Phase 6 policy: remote-wins + `.conflict-YYYYMMDD-HHMMSS.md` copy
- Replace the single `SemaphoreSlim` in `SyncEngine` with per-document `SemaphoreSlim` so push/pull don't fully serialize
- Conflict detection during push: hash check + `updatedAt` comparison before PATCH
- Tests: `ConflictResolverTests`, extend `SyncEngineTests` with conflict permutations

### 3.5 Windows — Phase 6: Notifications & Error Handling

- Windows App SDK toasts via `AppNotificationManager` for: sync completion (changes only), sync failure, conflict copy
- Toast actions: "Open Folder", "Retry"
- Auth-expired badge on tray icon
- Network change detection via `NetworkInformation.NetworkStatusChanged` → pause/resume
- Tests: `NotificationManagerTests` (mock toast manager), `NetworkMonitorTests`

### 3.6 Windows — Phase 7: Settings UI

- WPF `SettingsWindow.xaml`:
  - Sync folder path + browse (`FolderBrowserDialog`)
  - Poll interval slider
  - Auto-start toggle (write to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`)
  - Notifications toggles
  - Sign-out button
  - "Open sync folder" / "Open log folder" shortcuts
  - Version + log path display
- `OnboardingWindow.xaml` for first-run sign-in + folder selection
- Tests: WPF view-model tests (no UI thread dependencies)

### 3.7 Windows — Phase 8: Packaging & Distribution

- Wire `InterlinedSync.Package` MSIX project into CI:
  - `dotnet build -c Release` on `.Package` project produces `.msix`/`.msixbundle`
  - Code signing certificate (configure as `WINDOWS_CERT_PFX_BASE64` + `WINDOWS_CERT_PASSWORD` repo secrets)
  - Add Windows job to `release.yml` for tagged builds
- Optionally: framework-dependent vs self-contained publish trade-off (self-contained = bigger zip, no .NET runtime install required)

### 3.8 Linux — Finish M4 polish

From the Linux engineer's report:
- Folder picker result needs to be persisted on settings window close (currently shows in UI only)
- Add "Local wins" option to `ConflictResolution` enum (currently only `RemoteWins` and `ConflictCopy`)
- Single window-level Apply/Revert instead of per-panel Save rows
- Live `KeyringSecretStore` integration test (currently `#[ignore]`'d; needs a Linux runner with D-Bus + GNOME Keyring)

### 3.9 Linux — M5: Offline queue

- New `queue-store` crate (or extend `state-store`) with `pending_ops` table: `(id, op_kind, doc_id, payload_json, queued_at, retry_count)`
- `SyncEngine::run()` drains the queue on startup and on reconnect
- NetworkManager D-Bus subscription via `zbus` → trigger drain when state becomes `CONNECTED_GLOBAL`
- Tests: queue insert/drain/retry; D-Bus listener mock

### 3.10 Linux — M6: `.deb` polish

- `.deb` builds (verified in CI) but maintainer scripts are incomplete:
  - `postinst`: `sysctl --system` (for the sysctl drop-in) + `apparmor_parser -r /etc/apparmor.d/usr.bin.interlinedlist-sync`
  - `prerm`: reverse both
- Add to `Cargo.toml`'s `[package.metadata.deb]`
- PPA setup (separate from this work) — document the upload process

### 3.11 Linux — M7: Snap

- `snapcraft.yaml` with `core22` base, `confinement: classic` (file access required), interfaces: `home`, `network`, `desktop`, `password-manager-service`
- CI job to build snap on tagged releases
- Snap Store account + review process (out of scope for v1 but document)

---

## 4. Integration testing (currently zero coverage)

All three platforms have **only unit tests** today. Live-API integration tests are needed before v1.

### 4.1 Setup work (do once, all platforms benefit)

- **GitHub secrets** on `CompositeCode/interlinedlist-synchronization`:
  - `INTERLINEDLIST_EMAIL`
  - `INTERLINEDLIST_PASSWORD`
  - `INTERLINEDLIST_API_BASE_URL`
- **Workflow updates** — each CI workflow's test step gains:
  ```yaml
  env:
    INTERLINEDLIST_EMAIL: ${{ secrets.INTERLINEDLIST_EMAIL }}
    INTERLINEDLIST_PASSWORD: ${{ secrets.INTERLINEDLIST_PASSWORD }}
    INTERLINEDLIST_API_BASE_URL: ${{ secrets.INTERLINEDLIST_API_BASE_URL }}
  ```
- **Local convention** — `.env` already in place + gitignored; each platform's test harness reads from env vars at startup
- **Gating** — integration tests skip gracefully when env vars are absent (fork PRs, etc.)
- **Branch policy** — recommend: run on PR-to-`main` + manual `workflow_dispatch`, not every push (avoids hammering live API)

### 4.2 Per-platform test scaffolding

| Platform | Test target | Convention |
|---|---|---|
| macOS | `InterlinedSyncIntegrationTests` SPM test target | `XCTSkipIf(ProcessInfo.processInfo.environment["INTERLINEDLIST_EMAIL"] == nil)` |
| Windows | `InterlinedSync.IntegrationTests` (already exists; currently empty scaffolding) | xUnit `Skip="..."` attribute or `IConditionalTheory` |
| Linux | `tests/integration/` directory; `#[ignore]` by default unless env vars present | `cargo test --ignored` runs them |

### 4.3 What integration tests should cover

- Login → bearer token returned
- Token works on `GET /api/documents`, `POST`, `PATCH`, `DELETE`
- Token works on `GET /api/documents/folders` + CRUD
- `/api/documents/sync` delta returns expected shape
- Round-trip: create local file → verify upload → fetch from server → matches
- Round-trip: create on server → verify download → matches
- Conflict scenario: modify both sides → verify conflict copy created
- Cleanup: integration tests must tear down any docs/folders they create

---

## 5. UX / UI gaps

The application is a **menu-bar / tray sync client**. UX details below apply uniformly across all three OSes (with platform-idiomatic adjustments).

### 5.1 Status icon states (tray/menu bar)

| State | Icon | Trigger |
|---|---|---|
| **Idle** | static cloud or circular-arrow icon | no activity, last sync succeeded |
| **Syncing** | animated rotation or spinner overlay | active push/pull |
| **Paused** | pause overlay | user paused via menu |
| **Error** | warning badge | last sync failed; details in tooltip |
| **Conflict** | conflict badge | one or more conflict copies created since last user view |
| **Offline** | offline indicator | network lost; queue building locally |
| **Auth needed** | exclamation badge | token expired/invalid; click → reopen sign-in |

Implementation status:
- **macOS** — Phase 4 landed idle / syncing / paused / error via SF Symbols. Conflict / offline / auth-needed states still need wiring.
- **Windows** — partial (icon placeholder); state-driven badging is Phase 6 work.
- **Linux** — `ksni` tray supports state changes; needs wiring through `SyncStatus` enum updates.

### 5.2 Menu structure (consistent across OSes)

```
┌────────────────────────────────┐
│ Signed in as user@example.com  │  ← header, not clickable
│ Last synced 5 minutes ago      │  ← live status
│ ── separator ──                │
│ Sync Now                       │  ← triggers immediate poll
│ Pause / Resume                 │  ← toggles
│ ── separator ──                │
│ Open Sync Folder               │  ← Finder/Explorer/Files at root
│ Open in InterlinedList         │  ← https://interlinedlist.com
│ ── separator ──                │
│ Preferences…                   │
│ Quit                           │
└────────────────────────────────┘
```

When in **Error / Conflict / Auth-needed** state, prepend a contextual item:
- Error: "Last sync failed — click for details" → opens log viewer
- Conflict: "N conflicts since last sync" → opens sync folder filtered to `.conflict-*.md`
- Auth-needed: "Sign in required" → opens onboarding sheet

### 5.3 Preferences window

| Panel | Controls |
|---|---|
| **General** | Sync folder path + browse · Launch at startup · Sync interval (5–300 s slider) |
| **Account** | "Signed in as …" · Sign out button · Open InterlinedList in browser |
| **Notifications** | Enable notifications · Sync completion · Sync errors · Conflict copies |
| **Conflict Resolution** | Strategy: Remote wins + conflict copy *(read-only for v1)* · Show conflict log |
| **Advanced** | Log file path (read-only, with "Open Log Folder" button) · Reset state (with confirmation) · About / version |

### 5.4 First-run onboarding

1. **Welcome screen** — brand mark + one-line value prop + "Get Started" button
2. **Sign-in** — email + password → `POST /api/auth/login` → store token in platform keychain
3. **Folder selection** — default to `~/InterlinedList Sync` (or platform equivalent); browse option
4. **Initial sync progress** — full document list pulled; progress bar showing N of M files
5. **Done** — "Your documents are syncing. Find them at /path/to/folder. The app lives in your menu bar." → "Open Folder" + "Got It" buttons

Status:
- **macOS** has `OnboardingView` but only steps 1 + 2 + 3 are present. Steps 4 + 5 are TODO.
- **Windows** — `OnboardingWindow` is planned for Phase 7, not yet built.
- **Linux** — currently CLI-only (`--login` flag). Needs a GTK onboarding window or accepted as power-user-CLI convention.

### 5.5 Notifications

| Event | Type | Body | Actions |
|---|---|---|---|
| Sync completed (changes only) | Info | "Synced 3 documents" | Open Folder |
| Sync failed | Error | "Couldn't reach InterlinedList. Will retry in 30 s." | Retry, Open Log |
| Conflict copy created | Warning | "Conflict in 'Document Name.md' — copy saved." | Open File |
| Auth expired | Error | "Sign in to continue syncing." | Sign In |
| Initial sync complete | Info | "Initial sync done — N documents downloaded." | Open Folder |

Toggleable per category in Preferences → Notifications.

### 5.6 Folder tree UX (when folder support lands — see §2)

- Server folder hierarchy mirrors to local subdirectories under the sync root
- Folder rename on server → local rename (atomic where possible)
- Document moved between folders on server → local file moved
- User creates local folder → creates server folder via `POST /api/documents/folders`
- Conflict: local folder name collision with server folder created elsewhere → rename with `-conflict-YYYYMMDD-HHMMSS` suffix
- Empty folders on server: keep represented locally (don't delete just because empty)

### 5.7 Error / edge states (define behavior before implementation)

| Condition | Behavior |
|---|---|
| Network offline | Pause sync, show offline icon, queue local changes, resume on reconnect |
| Auth expired | Tray badge + notification, open preferences sign-in |
| Server rate-limited (HTTP 429) | Exponential backoff with `Retry-After` header honored |
| Disk full | Pause sync, surface error notification, halt pull until resolved |
| Sync folder missing/moved | Pause sync, open preferences to re-pick folder |
| Two clients editing same doc | Per remote-wins + conflict-copy policy |
| Document deleted on server | Move local file to trash (not permanent delete) — needs design confirmation |

---

## 6. Documentation gaps

- `README.md` at repo root — currently minimal; should describe the three native apps, link to per-platform READMEs, link to releases
- Per-platform `README.md` files exist but need: install instructions for end users, troubleshooting section, where logs live
- `CONTRIBUTING.md` — does not exist
- GitHub Pages site (the `docs-writer` agent's "documentation branch" target) — never set up
- API contract reference (snapshot of `/help/api` findings to avoid live-doc drift)

---

## 7. Sub-agent breakdown for the remaining work

Use this matrix when delegating. Each agent gets a self-contained brief and operates on disjoint directories so they can run in parallel.

### 7.1 Standing assignments (per platform — sequential within platform, parallel across platforms)

| Workstream | Agent | Scope |
|---|---|---|
| macOS Phase 5–7 | `swift-engineer` (implementation) + `engineering-macos-agent` (signing/notarization in Phase 7) | `macos/` only |
| Windows Phase 5–8 | `engineering-windows-agent` | `windows/` only |
| Linux M4 polish + M5–M7 | `engineering-linux-ubuntu-agent` | `linux-ubuntu/` only |

Rule of thumb: Phase 5 on all three can run in parallel (different platforms, no cross-cutting dependencies). API alignment work (§2) should be done first as one batched commit per platform — also parallel.

### 7.2 Cross-cutting agents

| Workstream | Agent | When to invoke |
|---|---|---|
| API alignment audit (§2) | `general-purpose` to first inventory the divergences in detail, then per-platform engineers to apply fixes | Before Phase 5 begins |
| Integration test scaffolding (§4) | per-platform engineers, **after** §2 alignment lands | After API alignment |
| GitHub secrets setup + workflow wiring (§4.1) | direct execution (not delegated — small focused diff) | Before integration tests |
| UX design pass for Preferences / Onboarding mocks (§5.3, §5.4) | `general-purpose` first if you want low-fidelity wireframe descriptions; then per-platform engineers for implementation | Before Phase 5 / 7 implementation |
| Documentation (§6) | `docs-writer` | Parallel with implementation; runs against `documentation` branch |
| Folder-support cross-platform design (§2 follow-up) | `general-purpose` to draft a shared design doc, then per-platform engineers | Before folder implementation begins |

### 7.3 Parallel batching strategy

To keep iteration tight, batch like this:

**Batch A (immediate, before Phase 5) — three parallel agents:**
- `swift-engineer`: macOS API alignment (PUT→PATCH, body→content, prep `Folder` model)
- `engineering-windows-agent`: Windows API alignment (PUT→PATCH, wire `Folder` CRUD, swap to `/sync` endpoint)
- `engineering-linux-ubuntu-agent`: Linux API alignment (PUT→PATCH, add `Folder` model + CRUD, swap to `/sync` endpoint)

**Batch B (immediate, in parallel with Batch A) — direct:**
- GitHub secrets + workflow env-var wiring for all three workflows (small, no need to delegate)

**Batch C (Phase 5 proper) — three parallel agents after Batch A lands:**
- `swift-engineer`: macOS Phase 5 (Preferences + Login Item)
- `engineering-windows-agent`: Windows Phase 5 (Conflict Resolution)
- `engineering-linux-ubuntu-agent`: Linux finish-M4 + M5 (Offline Queue)

**Batch D (Phase 6, after Batch C) — three parallel agents:**
- macOS Phase 6, Windows Phase 6, Linux M6 (.deb maintainer scripts)

**Batch E (Phase 7+) — three parallel agents:**
- macOS Phase 7 (signing/notarization), Windows Phase 7+8 (Settings UI + MSIX), Linux M7 (Snap)

**Batch F (release):**
- Tag `v0.1.0-rc1`, run `release.yml`, smoke-test installers, iterate

### 7.4 Agent briefing rule

Every sub-agent brief should explicitly:
- State the scope and which directory(ies) they may modify
- Cite the relevant section of this gap-work plan
- Require tests alongside every change
- Require `swift test` / `dotnet test` / `cargo test --workspace` green before reporting done
- Cap the report at ~300 words

---

## 8. Suggested execution order

A pragmatic critical path from "Phase 4 done" to "v1.0 release":

1. **Confirm answers to §2 open questions** (auth contract, relativePath, folder scope) — user input required
2. **Batch B** — GitHub secrets + workflow wiring (~15 min, direct)
3. **Batch A** — Cross-platform API alignment (3 parallel agents, ~1 session)
4. **Integration test scaffolding** — per-platform, parallel (~1 session)
5. **Cut `v0.1.0-alpha` tag** — first installer-grade release, internally testable
6. **Batch C** — Phase 5 across the board (~1 session)
7. **Folder support implementation** (if §2 scope decision is "include in Phase 5") — likely ~1–2 sessions
8. **Batch D** — Phase 6 across the board (~1 session)
9. **Batch E** — Phase 7+ packaging/signing (~1–2 sessions; signing cert procurement may bottleneck)
10. **Documentation pass** (parallel with implementation throughout)
11. **Cut `v1.0.0` tag** → publish public release

---

## 9. Things explicitly NOT in scope for v1

Documented here to keep scope contained:

- Multi-account support
- Selective sync (sync only certain folders)
- Per-folder pause/resume
- iOS / iPadOS / Android clients
- Browser extension
- Local search across synced documents
- Conflict-resolution strategy beyond remote-wins + conflict-copy (e.g., three-way merge, user-chosen winner)
- End-to-end encryption of document content
- Offline editing of documents (the app syncs, it doesn't edit)
