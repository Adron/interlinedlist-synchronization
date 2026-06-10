# Continuous Integration

GitHub Actions workflow files for all three platform builds.

## Workflows

| File | Platform | Runner |
|---|---|---|
| `macos-ci.yml` | macOS (Swift/SPM) | `macos-14` |
| `windows-ci.yml` | Windows (.NET 9 / WPF) | `windows-latest` |
| `linux-ubuntu-ci.yml` | Linux/Ubuntu (Rust/GTK4) | `ubuntu-22.04` |

## Activating the workflows

GitHub Actions only picks up workflows from `.github/workflows/`. Copy or move these files there:

```sh
mkdir -p .github/workflows
cp continuous-integration/*.yml .github/workflows/
```

The path filters in each workflow (`paths:` key) ensure a workflow only runs when its own platform directory changes, so pushes that only touch `windows/` won't trigger the macOS or Linux builds.

## What each workflow does

### macOS
- Resolves SPM dependencies
- `swift build -c release`
- `swift test --parallel`
- Uploads the release binary as an artifact on `main`

### Windows
- Restores NuGet packages (central package versioning via `Directory.Packages.props`)
- `dotnet build --configuration Release`
- Runs unit tests (`InterlinedSync.Tests`) with code coverage via coverlet
- Runs integration tests (`InterlinedSync.IntegrationTests`)
- Uploads `.trx` test results and coverage XML as artifacts

### Linux/Ubuntu
- Installs GTK4, libadwaita, libayatana-appindicator3, libsecret, and other native deps
- Installs the stable Rust toolchain with clippy and rustfmt
- Checks formatting (`cargo fmt --check`)
- Lints with `cargo clippy -- -D warnings`
- `cargo build --release` + `cargo test --all`
- On `main`: runs a second job that builds a `.deb` package with `cargo-deb`

## Notes

- The Linux workflow assumes a `Cargo.toml` exists at `linux-ubuntu/Cargo.toml`. Add it when the Rust implementation begins.
- The `.deb` job requires a `[package.metadata.deb]` section in `Cargo.toml` for `cargo-deb` to know what to include (binary path, systemd unit, desktop file, etc.).
- Code signing for macOS and Windows release builds is not included here; add that as a separate workflow or extend these when certificates are available.
