# Contributing to InterlinedList Sync

## Project structure

This repository contains three independent native applications. There is no shared code between
them.

```
macos/           — Swift 5.9 + AppKit/SwiftUI menu bar daemon (Phases 1–7)
windows/         — C# 13 / .NET 9 WPF tray application (Phases 1–8)
linux-ubuntu/    — Rust + GTK4/libadwaita daemon + systemd user service (M1–M7)
```

Each directory is self-contained: its own source tree, build system, test suite, and CI workflow.
Changes to one platform directory do not affect the others.

## Per-platform toolchain prerequisites

### macOS

- macOS 13 or later (Ventura)
- Xcode 16 (installs Swift 5.9 and the Swift Package Manager automatically)
- No third-party dependencies beyond SPM packages — `swift package resolve` fetches everything

### Windows

- Windows 10 build 19041 or later
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Visual Studio 2022 (Community or higher) **or** JetBrains Rider
  - Required VS workload: **.NET desktop development**
  - Optional: **Windows App SDK** workload for MSIX tooling
- `dotnet restore` fetches all NuGet packages

### Linux

- Ubuntu 22.04 LTS or 24.04 LTS (amd64 or arm64)
- Rust stable toolchain — install via [rustup](https://rustup.rs/): `rustup update stable`
- GTK4 and libadwaita development libraries:

```bash
sudo apt-get install \
  libgtk-4-dev \
  libadwaita-1-dev \
  libayatana-appindicator3-dev \
  libdbus-1-dev \
  libsecret-1-dev \
  pkg-config
```

- For `.deb` packaging: `cargo install cargo-deb`
- For Snap packaging: `sudo snap install snapcraft --classic`

> **Note:** Ubuntu 22.04 ships libadwaita 1.0.x, which lacks `PreferencesWindow`,
> `SpinRow`, and `SwitchRow`. Install a newer version via the GNOME PPA before building
> the settings dialog:
> ```bash
> sudo add-apt-repository ppa:gnome-team/gnome-next
> sudo apt-get update && sudo apt-get install libadwaita-1-0
> ```

## Running tests

### macOS

```bash
cd macos
swift test
```

### Windows

```bash
cd windows
dotnet test InterlinedSync.sln
```

### Linux

```bash
cd linux-ubuntu
cargo test --workspace
```

## Live-API integration tests

Each platform has a set of integration tests that run against the live `interlinedlist.com` API.
These tests are skipped automatically when the required environment variables are absent, so they
never block CI on fork pull requests or local builds without credentials.

### Setting up credentials

Create a `.env` file at the repository root (it is already in `.gitignore`):

```bash
INTERLINEDLIST_EMAIL=you@example.com
INTERLINEDLIST_PASSWORD=your-password
INTERLINEDLIST_API_BASE_URL=https://interlinedlist.com
```

Each platform's test harness reads these variables at test startup:

- **macOS** — uses `ProcessInfo.processInfo.environment`; skips with `XCTSkipIf` when absent
- **Windows** — reads env vars in `IntegrationTestFixture`; skips with the `[IntegrationFact]`
  attribute when credentials are missing
- **Linux** — reads with `std::env::var`; tests return early with an `eprintln!` skip message
  when absent; run integration tests with `cargo test --workspace -- --ignored` (or without
  `--ignored` — the tests skip rather than fail)

### Running integration tests explicitly

```bash
# macOS
swift test --filter InterlinedListClientIntegrationTests

# Windows
cd windows
dotnet test InterlinedSync.IntegrationTests

# Linux
cd linux-ubuntu
cargo test --workspace 2>&1 | grep -E "(test |FAILED|ok)"
```

Integration tests clean up every document and folder they create. Do not interrupt a test run
mid-flight — if cleanup is skipped, orphaned documents prefixed `__macos-integ-`, `__windows-integ-`,
or `__linux-integ-` may remain in your account and should be deleted manually.

## Branch and pull-request conventions

- **Branch naming:** `feature/<short-description>`, `fix/<short-description>`, `chore/<topic>`,
  `docs/<topic>`
- **Target branch:** all pull requests merge to `main`
- **Documentation:** documentation changes targeting the GitHub Pages site must go on the
  `documentation` branch (or a `documentation/<topic>` sub-branch), not `main`
- **CI must pass** before merging: all three platform CI workflows must be green on the PR head
- **Review:** at least one approval required before merge

## Commit message style

This project uses [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <short summary>

<optional body>
```

| Type | When to use |
|------|-------------|
| `feat` | New feature |
| `fix` | Bug fix |
| `chore` | Maintenance (dependency update, config change) |
| `test` | Adding or updating tests |
| `docs` | Documentation only |
| `refactor` | Code restructuring with no behavior change |
| `ci` | Changes to GitHub Actions workflows |

**Scope** is the platform directory when applicable: `macos`, `windows`, `linux`, or omitted for
cross-cutting changes.

Examples:

```
feat(macos): add conflict copy creation to ConflictResolver
fix(windows): honor Retry-After header on HTTP 429
docs: rewrite root README with feature matrix
ci: add .env secret wiring to integration test jobs
```

## Code style

Each platform follows its own idiomatic style. There is no enforced cross-language style guide.

| Platform | Style reference |
|----------|----------------|
| macOS (Swift) | [Swift API Design Guidelines](https://swift.org/documentation/api-design-guidelines/); `swift-format` with default settings |
| Windows (C#) | [.NET runtime coding style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md); Roslyn analyzers in the solution |
| Linux (Rust) | `rustfmt` defaults; `cargo clippy --workspace --deny warnings` must pass |

## Filing issues and asking questions

- **Bug reports and feature requests:** [GitHub Issues](https://github.com/Adron/interlinedlist-synchronization/issues)
- **Questions:** open a Discussion or comment on a relevant issue
- **Security issues:** do not open a public issue; email the maintainer directly
