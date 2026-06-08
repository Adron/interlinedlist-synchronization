# Next Steps

## Activate the workflows

Copy the workflow files into the location GitHub Actions requires:

```sh
mkdir -p .github/workflows
cp continuous-integration/*.yml .github/workflows/
```

## Per-platform follow-up

### macOS
- Verify Xcode version selected by `maxim-lobanov/setup-xcode@v1` matches what the Swift package needs (currently targets Swift 5.9 / Xcode 15+).
- Add code-signing steps when a Developer ID certificate and notarization credentials are available.

### Windows
- Confirm the output path in the upload-artifact step matches the actual build output once the project is further along.
- Add MSIX packaging step when the installer is ready.
- Wire coverage XML into a reporting tool (Codecov, SonarCloud, or GitHub's own summary) if desired.

### Linux/Ubuntu
- The Linux workflow assumes `linux-ubuntu/Cargo.toml` exists. Create it when the Rust implementation begins.
- Add a `[package.metadata.deb]` section to `Cargo.toml` with the binary path, systemd unit file, AppArmor profile, and `.desktop` autostart entry so `cargo-deb` packages them correctly.
- Consider adding an `ubuntu-24.04` matrix entry alongside `ubuntu-22.04` once the LTS targets are confirmed.

## Shared / repo-level

- Initialize the git repository (`git init`) so the workflows can be pushed to GitHub.
- Set up a GitHub repository and push; the workflows will trigger on the first push to `main` or `develop`.
- Add a top-level `ci.yml` or status-badge links to the root `README.md` once the workflows are live.
- Decide on branch naming convention (`main`/`develop` is assumed in all three workflows — adjust if different).
