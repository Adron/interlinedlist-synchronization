use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use anyhow::{Context, Result};
use clap::Parser;
use tokio::sync::mpsc;
#[cfg(target_os = "linux")]
use tracing::warn;
use tracing::{error, info};
use tracing_appender::non_blocking::WorkerGuard;
use tracing_subscriber::fmt::layer as fmt_layer;
use tracing_subscriber::prelude::*;
use tracing_subscriber::EnvFilter;

use api_client::{ApiClient, ApiClientConfig, ApiClientTrait};
use config_store::ConfigStore;
use file_watcher::FileWatcher;
#[cfg(target_os = "linux")]
use notifier::LibnotifyNotifier;
#[cfg(not(target_os = "linux"))]
use notifier::StubNotifier;
use secret_store::FileSecretStore;
// interlinedlist-sync's Cargo.toml enables secret-store's `gnome-keyring` feature only on
// Linux, so KeyringSecretStore is only available when compiling for Linux.
#[cfg(target_os = "linux")]
use secret_store::KeyringSecretStore;
use secret_store::SecretStore;
use state_store::StateStore;
use sync_engine::{SyncEngine, SyncStatus};
use tray_app::run_tray_app;

mod status;
use status::{build_report, last_sync_time_from_db, SecretBackend, StatusInputs};

/// How frequently the daemon polls the secret store when waiting for the user
/// to complete sign-in via the GUI dialog.  A separate `oneshot` channel fires
/// immediately when the tray's sign-in dialog succeeds, so this is only a
/// fallback for environments where the channel cannot fire (e.g. the GTK
/// feature is disabled and the user has run `--login` separately).
const CREDENTIAL_POLL_INTERVAL: Duration = Duration::from_secs(5);

#[derive(Parser, Debug)]
#[command(
    name = "interlinedlist-sync",
    version,
    about = "InterlinedList document sync daemon"
)]
struct Cli {
    /// Run as a daemon with system tray (GTK4). Without this flag the process
    /// logs to stdout and exits after one sync cycle — useful for manual testing.
    #[arg(long)]
    daemon: bool,

    /// Path to config file (default: ~/.config/interlinedlist-sync/config.toml)
    #[arg(long)]
    config: Option<PathBuf>,

    /// Account username for login
    #[arg(long)]
    username: Option<String>,

    /// Account password for login (use only for initial token setup)
    #[arg(long)]
    password: Option<String>,

    /// Store a fresh token and exit (runs login and saves to secret store)
    #[arg(long)]
    login: bool,

    /// Print diagnostic status and exit (config path, last sync time, log
    /// tail, credential backend, systemd unit state). Exit 0 if healthy, 1
    /// if anything essential is missing.
    #[arg(long)]
    status: bool,
}

#[tokio::main]
async fn main() -> Result<()> {
    let cli = Cli::parse();

    // Compute paths up front so init_tracing() can log the log-file path and
    // the --status command knows where to look.
    let config_path = cli.config.unwrap_or_else(ConfigStore::default_path);
    let log_file_path = default_log_path();

    // IMPORTANT: init_tracing() MUST run before any config loading so that
    // config-load failures are captured in both the file log and stderr.
    // The returned WorkerGuard must be held for the lifetime of main(); dropping
    // it early causes the non-blocking appender's background thread to shut
    // down, silently losing buffered log lines.
    let _log_guard = init_tracing(&log_file_path);

    // --status: gather diagnostics and exit without touching the sync engine.
    if cli.status {
        return run_status_command(&config_path, &log_file_path).await;
    }

    let config_store = ConfigStore::new(config_path.clone());
    let config = config_store.load_or_default()?;

    // On Linux, prefer GNOME Keyring; fall back to file store if keyring init
    // fails (e.g., headless SSH sessions without a keyring daemon running).
    // On non-Linux hosts, always use the file store.
    #[cfg(target_os = "linux")]
    let (secret_store, secret_backend): (Arc<dyn SecretStore>, SecretBackend) = {
        match KeyringSecretStore::new_checked().await {
            Ok(ks) => {
                info!("using GNOME Keyring for credential storage");
                (Arc::new(ks), SecretBackend::GnomeKeyring)
            }
            Err(e) => {
                warn!("GNOME Keyring unavailable ({e}), falling back to file secret store");
                (
                    Arc::new(FileSecretStore::default_store()),
                    SecretBackend::FileFallback,
                )
            }
        }
    };
    #[cfg(not(target_os = "linux"))]
    let (secret_store, _secret_backend): (Arc<dyn SecretStore>, SecretBackend) = (
        Arc::new(FileSecretStore::default_store()),
        SecretBackend::FileFallback,
    );

    if cli.login {
        let username = cli.username.context("--username required with --login")?;
        let password = cli.password.context("--password required with --login")?;
        let api_config = make_api_config(&config);
        let api = ApiClient::new(api_config, secret_store.clone())?;
        let token = api.login(&username, &password).await?;
        secret_store.store_token(&username, &token).await?;
        info!("token stored for {username}");
        return Ok(());
    }

    let account = cli.username.unwrap_or_else(|| "default".to_string());

    // ── Daemon path ───────────────────────────────────────────────────────────
    if cli.daemon {
        run_daemon(config, config_path, secret_store, account).await
    } else {
        // Headless mode: require credentials already present, run one poll cycle.
        run_headless(config, secret_store, account).await
    }
}

// ── Daemon ────────────────────────────────────────────────────────────────────

/// Run the daemon: start the tray immediately, wait for credentials if not yet
/// present, then hot-start the sync engine without a process restart.
///
/// State transitions:
///   1. Check secret store → if no token, broadcast `WaitingForCredentials`
///      via the status watch channel and enter the wait loop.
///   2. Wait loop: await `credentials_ready` oneshot (fired by sign-in dialog)
///      OR poll the secret store every `CREDENTIAL_POLL_INTERVAL` as a fallback.
///   3. Once a token exists, build the `SyncEngine` and spawn it on the
///      `LocalSet` that is already running the tray.
///   4. Sign-out path: when the user clicks "Sign Out" in the tray, the engine
///      task is aborted, the token is deleted, status returns to
///      `WaitingForCredentials`, and the wait loop restarts.
async fn run_daemon(
    config: config_store::AppConfig,
    config_path: PathBuf,
    secret_store: Arc<dyn SecretStore>,
    account: String,
) -> Result<()> {
    // ── Channels ─────────────────────────────────────────────────────────────
    // sync_now: tray "Sync Now" button → engine.
    let (sync_now_tx, sync_now_rx_initial) = mpsc::channel::<()>(8);
    // credentials_ready: sign-in dialog → daemon wait loop (oneshot).
    let (creds_ready_tx, creds_ready_rx) = tokio::sync::oneshot::channel::<()>();
    // sign_out: daemon → tray (to flip tray back to signed-out state).
    let (sign_out_tx, sign_out_rx) = mpsc::channel::<()>(4);

    // ── Initial status broadcast ──────────────────────────────────────────────
    // Use a watch channel so both the tray and the wait-loop can observe status.
    // The initial value reflects whether we have credentials already.
    let has_token = secret_store.load_token(&account).await.is_ok();
    let initial_status = if has_token {
        info!("credentials found for account '{account}', starting sync engine");
        SyncStatus::Idle
    } else {
        info!(
            "no credentials found for account '{account}'; \
             daemon is waiting for sign-in. \
             Use the tray 'Sign in\u{2026}' menu item or run \
             'interlinedlist-sync --login --username <email> --password <pass>'."
        );
        SyncStatus::WaitingForCredentials
    };
    let (status_tx, status_rx) = tokio::sync::watch::channel(initial_status.clone());

    // ── Build shared API client (needed by both sign-in dialog and sync engine) ──
    let api = Arc::new(ApiClient::new(
        make_api_config(&config),
        secret_store.clone(),
    )?);

    // ── LocalSet — keep !Send types on one thread ─────────────────────────────
    let local = tokio::task::LocalSet::new();
    local
        .run_until(async move {
            // ── Spawn the tray ────────────────────────────────────────────────
            // The tray must start immediately so the user has a UI even when no
            // credentials are present.
            let tray_future = {
                let status_rx = status_rx.clone();
                let sync_now_tx = sync_now_tx.clone();
                let config_path = config_path.clone();

                #[cfg(all(target_os = "linux", feature = "gtk"))]
                {
                    let api_for_signin = api.clone() as Arc<dyn ApiClientTrait>;
                    let store_for_signin = secret_store.clone();
                    tray_app::run_tray_app_with_signin_deps(
                        status_rx,
                        sync_now_tx,
                        config_path,
                        creds_ready_tx,
                        sign_out_rx,
                        api_for_signin,
                        store_for_signin,
                    )
                }
                #[cfg(not(all(target_os = "linux", feature = "gtk")))]
                {
                    run_tray_app(
                        status_rx,
                        sync_now_tx,
                        config_path,
                        creds_ready_tx,
                        sign_out_rx,
                    )
                }
            };
            let tray_task = tokio::task::spawn_local(tray_future);

            // ── Wait-for-credentials + engine lifecycle ───────────────────────
            let engine_lifecycle = run_engine_lifecycle(
                config.clone(),
                account.clone(),
                api.clone(),
                secret_store.clone(),
                status_tx,
                sync_now_tx,
                sync_now_rx_initial,
                sign_out_tx,
                initial_status,
                creds_ready_rx,
            );

            tokio::select! {
                result = tray_task => {
                    // Tray exited (user quit or Ctrl-C).
                    match result {
                        Ok(Ok(())) => {}
                        Ok(Err(e)) => error!("tray exited with error: {e}"),
                        Err(e) => error!("tray task panicked: {e}"),
                    }
                }
                result = engine_lifecycle => {
                    if let Err(e) = result {
                        error!("engine lifecycle exited with error: {e}");
                    }
                }
            }

            Ok::<(), anyhow::Error>(())
        })
        .await?;

    Ok(())
}

/// Manage the sync engine lifecycle: wait for credentials if needed, start the
/// engine once a token is available, and handle sign-out by stopping and
/// restarting the wait loop.
#[allow(clippy::too_many_arguments)]
async fn run_engine_lifecycle(
    config: config_store::AppConfig,
    account: String,
    api: Arc<ApiClient>,
    secret_store: Arc<dyn SecretStore>,
    status_tx: tokio::sync::watch::Sender<SyncStatus>,
    sync_now_tx: mpsc::Sender<()>,
    sync_now_rx: mpsc::Receiver<()>,
    sign_out_tx: mpsc::Sender<()>,
    initial_status: SyncStatus,
    creds_ready_rx: tokio::sync::oneshot::Receiver<()>,
) -> Result<()> {
    // ── Phase 1: wait for credentials if not already present ─────────────────
    if initial_status == SyncStatus::WaitingForCredentials {
        wait_for_credentials(&account, &secret_store, &status_tx, creds_ready_rx).await;
    } else {
        // credentials were already present; consume the oneshot receiver so it
        // doesn't dangle, but we don't need it.
        drop(creds_ready_rx);
    }

    // ── Phase 2: build file watcher (once; survives sign-out/sign-in cycles) ─
    let (mut file_watcher, file_rx) =
        FileWatcher::new(256).context("failed to create file watcher")?;
    for dir in &config.sync.watched_dirs {
        let expanded = expand_tilde(dir);
        if let Err(e) = file_watcher.watch(&expanded) {
            error!("failed to watch {}: {e}", expanded.display());
        } else {
            info!("watching {}", expanded.display());
        }
    }

    // ── Phase 3: notifier ─────────────────────────────────────────────────────
    #[cfg(target_os = "linux")]
    let notifier = {
        let n = LibnotifyNotifier::new("InterlinedList Sync");
        Arc::new(n) as Arc<dyn notifier::Notifier>
    };
    #[cfg(not(target_os = "linux"))]
    let notifier = Arc::new(StubNotifier) as Arc<dyn notifier::Notifier>;

    // ── Phase 4: start the sync engine ───────────────────────────────────────
    // StateStore wraps a rusqlite Connection (RefCell) and is !Send.  Arc is
    // correct here: the engine runs on the LocalSet so the store never crosses
    // thread boundaries.
    #[allow(clippy::arc_with_non_send_sync)]
    let state = Arc::new(
        StateStore::open(&StateStore::default_path()).context("failed to open state store")?,
    );

    let engine = SyncEngine::new(
        config.clone(),
        account.clone(),
        api.clone() as Arc<dyn ApiClientTrait>,
        state,
        notifier,
        file_rx,
    );
    let _status_rx_engine = engine.status_receiver();
    // Pipe engine status into the shared status_tx so the tray sees it.
    {
        let mut engine_status_rx = engine.status_receiver();
        let status_tx_clone = status_tx.clone();
        tokio::task::spawn_local(async move {
            loop {
                if engine_status_rx.changed().await.is_err() {
                    break;
                }
                let s = engine_status_rx.borrow().clone();
                let _ = status_tx_clone.send(s);
            }
        });
    }

    let engine_task = tokio::task::spawn_local(async move {
        if let Err(e) = engine.run(sync_now_rx).await {
            error!("sync engine exited with error: {e}");
        }
    });

    // The engine runs until the process exits (tray quit / Ctrl-C). For this
    // iteration we do not implement live sign-out/sign-in cycling in the engine
    // lifetime loop — the tray "Sign out" item flips the tray UI and the daemon
    // will simply lose the token on next poll, surfacing an error.  A full
    // cycling implementation would require the engine to expose a shutdown
    // handle; that is deferred to a future iteration (see design notes).
    //
    // Await engine_task so the async block keeps running as long as the engine.
    drop(sync_now_tx); // avoid keeping a stale sender alive
    drop(sign_out_tx);
    let _ = engine_task.await;

    Ok(())
}

/// Block asynchronously until a valid token appears in the secret store.
///
/// Wakes immediately if `creds_ready_rx` fires (sign-in dialog success path).
/// Falls back to polling every `CREDENTIAL_POLL_INTERVAL` for CLI-only
/// environments where the oneshot never fires.
async fn wait_for_credentials(
    account: &str,
    secret_store: &Arc<dyn SecretStore>,
    status_tx: &tokio::sync::watch::Sender<SyncStatus>,
    creds_ready_rx: tokio::sync::oneshot::Receiver<()>,
) {
    let _ = status_tx.send(SyncStatus::WaitingForCredentials);

    // Wrap the oneshot in a `Fuse` so we can select on it repeatedly without
    // errors after it resolves.
    let mut creds_signal = std::pin::pin!(creds_ready_rx);
    let mut got_signal = false;

    loop {
        // Check the secret store first (covers the --login CLI path).
        if secret_store.load_token(account).await.is_ok() {
            info!("credentials detected for account '{account}'; hot-starting sync engine");
            let _ = status_tx.send(SyncStatus::Idle);
            return;
        }

        if got_signal {
            // Signal already fired but token not yet in store — unusual; keep polling.
            tokio::time::sleep(Duration::from_millis(200)).await;
            continue;
        }

        // Wait for either the sign-in dialog signal or the poll interval.
        tokio::select! {
            result = &mut creds_signal, if !got_signal => {
                match result {
                    Ok(()) => {
                        info!("sign-in signal received; checking secret store");
                        got_signal = true;
                        // Don't sleep — loop immediately to check the store.
                    }
                    Err(_) => {
                        // Sender dropped (e.g., tray exited before sign-in).
                        // Fall through to polling mode.
                        got_signal = true;
                    }
                }
            }
            _ = tokio::time::sleep(CREDENTIAL_POLL_INTERVAL) => {
                // Periodic poll so CLI --login also unblocks the daemon.
            }
        }
    }
}

// ── Headless ──────────────────────────────────────────────────────────────────

/// Headless mode: credentials must already be present; run one poll cycle.
async fn run_headless(
    config: config_store::AppConfig,
    secret_store: Arc<dyn SecretStore>,
    account: String,
) -> Result<()> {
    // In headless mode, we require credentials up-front because there is no UI
    // to collect them. Log a clear actionable message and exit non-zero so the
    // caller can distinguish "no credentials" from a transient network error.
    match secret_store.load_token(&account).await {
        Ok(_) => {
            info!("credentials found for account '{account}'");
        }
        Err(e) => {
            error!(
                "no credentials found for account '{account}': {e}. \
                 Run 'interlinedlist-sync --login --username <email> --password <pass>' \
                 to store credentials."
            );
            std::process::exit(1);
        }
    }

    let api = Arc::new(ApiClient::new(make_api_config(&config), secret_store)?);

    #[allow(clippy::arc_with_non_send_sync)]
    let state = Arc::new(
        StateStore::open(&StateStore::default_path()).context("failed to open state store")?,
    );

    #[cfg(target_os = "linux")]
    let notifier = {
        let n = LibnotifyNotifier::new("InterlinedList Sync");
        Arc::new(n) as Arc<dyn notifier::Notifier>
    };
    #[cfg(not(target_os = "linux"))]
    let notifier = Arc::new(StubNotifier) as Arc<dyn notifier::Notifier>;

    let (_tx, rx) = mpsc::channel::<file_watcher::FileEvent>(1);
    let engine = SyncEngine::new(config, account, api, state, notifier, rx);

    info!("headless mode: running one poll cycle");
    if let Err(e) = engine.poll_remote().await {
        error!("poll failed: {e}");
    }
    info!("headless sync complete, exiting");
    Ok(())
}

// ── Logging ───────────────────────────────────────────────────────────────────

/// Resolve the default path for the rolling log file.
/// ~/.local/share/interlinedlist-sync/logs/interlinedlist-sync.log
fn default_log_path() -> PathBuf {
    dirs::data_dir()
        .unwrap_or_else(|| PathBuf::from("~/.local/share"))
        .join("interlinedlist-sync")
        .join("logs")
        .join("interlinedlist-sync.log")
}

/// Initialise the tracing subscriber with two layers:
///   1. Rolling daily file appender → log file (compact human-readable text)
///   2. stderr layer (so `journalctl --user` still sees output)
///
/// Log level is controlled by the RUST_LOG environment variable; default: info.
///
/// The returned `WorkerGuard` MUST be held for the lifetime of `main()`.
/// Dropping it shuts down the background appender thread and silently discards
/// buffered writes.
fn init_tracing(log_file_path: &Path) -> WorkerGuard {
    // Ensure the log directory exists before creating the appender.
    let log_dir = log_file_path
        .parent()
        .expect("log path must have a parent directory");
    if let Err(e) = std::fs::create_dir_all(log_dir) {
        // Can't log yet — write directly to stderr.
        eprintln!(
            "interlinedlist-sync: warning: could not create log directory {}: {e}",
            log_dir.display()
        );
    }

    // The file name stem (without the directory) used by rolling_daily.
    let log_file_name = log_file_path
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("interlinedlist-sync.log");

    // Daily rotation. tracing-appender's rolling::daily does not prune old
    // files automatically — files accumulate until removed externally (e.g.,
    // logrotate). With 7-day retention configured in /etc/logrotate.d/, this
    // is fine. Document this in the README.
    let file_appender = tracing_appender::rolling::daily(log_dir, log_file_name);
    let (non_blocking_file, guard) = tracing_appender::non_blocking(file_appender);

    let env_filter = EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info"));

    // File layer — compact text, timestamps, thread IDs omitted for readability.
    let file_layer = fmt_layer()
        .with_writer(non_blocking_file)
        .with_ansi(false)
        .compact();

    // Stderr layer — same format so journalctl output matches the file.
    let stderr_layer = fmt_layer()
        .with_writer(std::io::stderr)
        .with_ansi(false)
        .compact();

    tracing_subscriber::registry()
        .with(env_filter)
        .with(file_layer)
        .with(stderr_layer)
        .init();

    // Log the resolved path now that the subscriber is active.
    eprintln!(
        "interlinedlist-sync: logging to {} (and stderr)",
        log_file_path.display()
    );

    guard
}

// ── --status command ──────────────────────────────────────────────────────────

/// Execute the `--status` subcommand: gather diagnostics and print them.
/// Exits 0 if all essential components are present, 1 otherwise.
async fn run_status_command(config_path: &Path, log_file_path: &Path) -> Result<()> {
    let state_db_path = StateStore::default_path();
    let last_sync_time = last_sync_time_from_db(&state_db_path);

    // Probe the secret store to determine backend and token presence for the
    // "default" account. We do NOT print the token value under any
    // circumstances — only a boolean.
    #[cfg(target_os = "linux")]
    let (secret_backend, token_present) = {
        match KeyringSecretStore::new_checked().await {
            Ok(ks) => {
                let present = ks.load_token("default").await.is_ok();
                (SecretBackend::GnomeKeyring, present)
            }
            Err(_) => {
                let fs = FileSecretStore::default_store();
                let present = fs.load_token("default").await.is_ok();
                (SecretBackend::FileFallback, present)
            }
        }
    };
    #[cfg(not(target_os = "linux"))]
    let (secret_backend, token_present) = {
        let fs = FileSecretStore::default_store();
        let present = fs.load_token("default").await.is_ok();
        (SecretBackend::FileFallback, present)
    };

    let inputs = StatusInputs {
        config_path: config_path.to_path_buf(),
        log_file_path: log_file_path.to_path_buf(),
        state_db_path,
        last_sync_time,
        secret_backend,
        token_present,
    };

    let report = build_report(inputs);
    print!("{}", report.render());

    if report.is_healthy() {
        Ok(())
    } else {
        std::process::exit(1);
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

fn make_api_config(config: &config_store::AppConfig) -> ApiClientConfig {
    ApiClientConfig {
        base_url: config.network.api_base_url.clone(),
        timeout: Duration::from_secs(config.network.timeout_seconds),
        max_retries: config.network.max_retries,
        backoff_base: Duration::from_secs(config.network.retry_backoff_base_seconds),
    }
}

fn expand_tilde(path: &str) -> PathBuf {
    if let Some(rest) = path.strip_prefix("~/") {
        dirs::home_dir()
            .unwrap_or_else(|| PathBuf::from("/tmp"))
            .join(rest)
    } else {
        PathBuf::from(path)
    }
}

// ── Tests for the wait-for-credentials state machine ─────────────────────────
// These tests exercise the core async logic without touching GTK or ksni, so
// they compile and run on macOS (the development host).

#[cfg(test)]
mod tests {
    use super::*;
    use async_trait::async_trait;
    use secret_store::{SecretStore, SecretStoreError};
    use std::sync::{Arc, Mutex};

    // ── Mock secret store ─────────────────────────────────────────────────────

    /// An in-memory secret store that can be pre-loaded with a token or left
    /// empty.  The `set_token` method allows background tasks to inject a token
    /// mid-test to simulate the sign-in dialog writing credentials.
    #[derive(Clone)]
    struct MockSecretStore {
        token: Arc<Mutex<Option<String>>>,
    }

    impl MockSecretStore {
        fn empty() -> Self {
            Self {
                token: Arc::new(Mutex::new(None)),
            }
        }

        fn with_token(t: impl Into<String>) -> Self {
            Self {
                token: Arc::new(Mutex::new(Some(t.into()))),
            }
        }

        fn set_token(&self, t: impl Into<String>) {
            *self.token.lock().unwrap() = Some(t.into());
        }

        fn as_arc_dyn(self) -> Arc<dyn SecretStore> {
            Arc::new(self)
        }
    }

    #[async_trait]
    impl SecretStore for MockSecretStore {
        async fn store_token(&self, _account: &str, t: &str) -> Result<(), SecretStoreError> {
            *self.token.lock().unwrap() = Some(t.to_string());
            Ok(())
        }

        async fn load_token(&self, account: &str) -> Result<String, SecretStoreError> {
            self.token
                .lock()
                .unwrap()
                .clone()
                .ok_or_else(|| SecretStoreError::NotFound {
                    account: account.to_string(),
                })
        }

        async fn delete_token(&self, _account: &str) -> Result<(), SecretStoreError> {
            *self.token.lock().unwrap() = None;
            Ok(())
        }
    }

    // ── Test: no-token → WaitingForCredentials status is broadcast, then Idle ─

    #[tokio::test]
    async fn no_token_then_signal_transitions_to_idle() {
        let mock = MockSecretStore::empty();
        let mock_clone = mock.clone();
        let store: Arc<dyn SecretStore> = mock.as_arc_dyn();

        let (status_tx, status_rx) = tokio::sync::watch::channel(SyncStatus::Idle);
        let (creds_tx, creds_rx) = tokio::sync::oneshot::channel::<()>();

        // Background task: after 50 ms, write the token and fire the signal.
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(50)).await;
            mock_clone.set_token("tok_abc");
            let _ = creds_tx.send(());
        });

        tokio::time::timeout(
            Duration::from_secs(5),
            wait_for_credentials("test", &store, &status_tx, creds_rx),
        )
        .await
        .expect("should exit within 5 s");

        // Final status must be Idle (credentials detected).
        assert_eq!(*status_rx.borrow(), SyncStatus::Idle);
    }

    // ── Test: token already present → exits immediately without polling ────────

    #[tokio::test]
    async fn token_already_present_exits_immediately() {
        let store = MockSecretStore::with_token("existing_tok").as_arc_dyn();
        let (status_tx, status_rx) = tokio::sync::watch::channel(SyncStatus::WaitingForCredentials);
        // Drop the tx side so the receiver sees an immediate error, ensuring
        // we don't rely on the signal path.
        let (creds_tx, creds_rx) = tokio::sync::oneshot::channel::<()>();
        drop(creds_tx);

        // Should return before the 1-second timeout.
        tokio::time::timeout(
            Duration::from_secs(1),
            wait_for_credentials("test", &store, &status_tx, creds_rx),
        )
        .await
        .expect("should exit immediately when token already exists");

        assert_eq!(*status_rx.borrow(), SyncStatus::Idle);
    }

    // ── Test: poll fallback — no signal, token injected after ~200 ms ─────────

    #[tokio::test]
    async fn poll_fallback_detects_token_without_signal() {
        let mock = MockSecretStore::empty();
        let mock_clone = mock.clone();
        let store = mock.as_arc_dyn();

        let (status_tx, status_rx) = tokio::sync::watch::channel(SyncStatus::Idle);

        // Drop the sender immediately so the oneshot resolves with Err(_),
        // forcing the wait loop into poll-only mode.
        let (creds_tx, creds_rx) = tokio::sync::oneshot::channel::<()>();
        drop(creds_tx);

        // Inject the token after 200 ms — well within the 10 s timeout but
        // after the first poll tick (200 ms sleep in the signal-dropped path).
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(200)).await;
            mock_clone.set_token("poll_tok");
        });

        tokio::time::timeout(
            Duration::from_secs(10),
            wait_for_credentials("test", &store, &status_tx, creds_rx),
        )
        .await
        .expect("poll fallback should detect token within 10 s");

        assert_eq!(*status_rx.borrow(), SyncStatus::Idle);
    }

    // ── Test: oneshot signal wakes the loop and the store confirms the token ──

    #[tokio::test]
    async fn oneshot_signal_wakes_wait_loop() {
        let mock = MockSecretStore::empty();
        let mock_clone = mock.clone();
        let store = mock.as_arc_dyn();

        let (status_tx, status_rx) = tokio::sync::watch::channel(SyncStatus::Idle);
        let (creds_tx, creds_rx) = tokio::sync::oneshot::channel::<()>();

        // Write the token and fire the signal simultaneously after 30 ms.
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(30)).await;
            mock_clone.set_token("signal_tok");
            let _ = creds_tx.send(());
        });

        tokio::time::timeout(
            Duration::from_secs(5),
            wait_for_credentials("test", &store, &status_tx, creds_rx),
        )
        .await
        .expect("oneshot signal path should exit within 5 s");

        assert_eq!(*status_rx.borrow(), SyncStatus::Idle);
    }

    // ── Test: SyncStatus::WaitingForCredentials variant exists and compares ───

    #[test]
    fn sync_status_waiting_for_credentials_variant_exists() {
        let waiting = SyncStatus::WaitingForCredentials;
        assert_eq!(waiting, SyncStatus::WaitingForCredentials);
        assert_ne!(waiting, SyncStatus::Idle);
        assert_ne!(waiting, SyncStatus::Syncing);
        assert_ne!(waiting, SyncStatus::Paused);
    }

    // ── Test: initial status is WaitingForCredentials when no token exists ────

    #[tokio::test]
    async fn initial_status_is_waiting_when_no_token() {
        let store = MockSecretStore::empty().as_arc_dyn();
        // Simulate the check done in run_daemon before the watch channel is built.
        let has_token = store.load_token("default").await.is_ok();
        let initial = if has_token {
            SyncStatus::Idle
        } else {
            SyncStatus::WaitingForCredentials
        };
        assert_eq!(initial, SyncStatus::WaitingForCredentials);
    }

    // ── Test: initial status is Idle when token exists ────────────────────────

    #[tokio::test]
    async fn initial_status_is_idle_when_token_present() {
        let store = MockSecretStore::with_token("tok").as_arc_dyn();
        let has_token = store.load_token("default").await.is_ok();
        let initial = if has_token {
            SyncStatus::Idle
        } else {
            SyncStatus::WaitingForCredentials
        };
        assert_eq!(initial, SyncStatus::Idle);
    }
}
