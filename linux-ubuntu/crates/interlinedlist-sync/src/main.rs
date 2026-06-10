use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use anyhow::{Context, Result};
use clap::Parser;
use tokio::sync::mpsc;
use tracing::{error, info};
use tracing_subscriber::EnvFilter;

use api_client::{ApiClient, ApiClientConfig, ApiClientTrait};
use config_store::ConfigStore;
use file_watcher::FileWatcher;
use notifier::StubNotifier;
use secret_store::{FileSecretStore, SecretStore};
use state_store::StateStore;
use sync_engine::SyncEngine;
use tray_app::run_tray_app;

#[derive(Parser, Debug)]
#[command(
    name = "interlinedlist-sync",
    version,
    about = "InterlinedList document sync daemon"
)]
struct Cli {
    /// Run as a daemon with system tray (GTK4). Without this flag the process
    /// logs to stdout and exits after one sync cycle — useful for M1 testing.
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
}

#[tokio::main]
async fn main() -> Result<()> {
    let cli = Cli::parse();

    init_tracing();

    let config_path = cli.config.unwrap_or_else(ConfigStore::default_path);
    let config_store = ConfigStore::new(config_path);
    let config = config_store.load_or_default()?;

    let secret_store = Arc::new(FileSecretStore::default_store());

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

    let api = Arc::new(ApiClient::new(make_api_config(&config), secret_store)?);
    let state = Arc::new(
        StateStore::open(&StateStore::default_path()).context("failed to open state store")?,
    );
    let notifier = Arc::new(StubNotifier);

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

    // sync_now_rx will be wired to the engine in M4 to allow tray-triggered syncs.
    let (sync_now_tx, _sync_now_rx) = mpsc::channel::<()>(8);

    let engine = SyncEngine::new(
        config.clone(),
        account.clone(),
        api,
        state,
        notifier,
        file_rx,
    );
    let status_rx = engine.status_receiver();

    if cli.daemon {
        // StateStore contains RefCell (rusqlite), which is !Send, so both tasks
        // must stay on the same thread. LocalSet provides that guarantee.
        let local = tokio::task::LocalSet::new();
        local
            .run_until(async move {
                let engine_task = tokio::task::spawn_local(async move {
                    if let Err(e) = engine.run().await {
                        error!("sync engine exited with error: {e}");
                    }
                });
                run_tray_app(status_rx, sync_now_tx).await?;
                engine_task.abort();
                Ok::<(), anyhow::Error>(())
            })
            .await?;
    } else {
        // Headless mode: run one poll cycle then exit.
        info!("headless mode: running one poll cycle");
        if let Err(e) = engine.poll_remote().await {
            error!("poll failed: {e}");
        }
        info!("headless sync complete, exiting");
    }

    Ok(())
}

fn init_tracing() {
    tracing_subscriber::fmt()
        .with_env_filter(
            EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info")),
        )
        .with_writer(std::io::stderr)
        .init();
}

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
