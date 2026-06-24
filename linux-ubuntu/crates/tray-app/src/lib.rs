use std::path::PathBuf;

use anyhow::Result;
use tokio::sync::{mpsc, watch};

#[cfg(all(target_os = "linux", feature = "gtk"))]
use std::sync::Arc;

use sync_engine::SyncStatus;

#[cfg(target_os = "linux")]
mod linux;

#[cfg(all(target_os = "linux", feature = "gtk"))]
pub(crate) mod settings;

#[cfg(all(target_os = "linux", feature = "gtk"))]
pub(crate) mod signin;

#[cfg(not(target_os = "linux"))]
mod stub;

/// Run the tray application.
///
/// Parameters:
/// - `status_rx`: watch channel carrying the current `SyncStatus`.
///   The tray reads this to update its icon and label.
/// - `sync_now_tx`: send a `()` to trigger an immediate sync cycle.
/// - `config_path`: path to the user's `config.toml`; forwarded to the settings dialog.
/// - `credentials_ready`: oneshot sender the tray fires when the sign-in
///   dialog stores a token so the daemon can hot-start the sync engine.
/// - `sign_out_rx`: receives a `()` from the daemon when a sign-out
///   operation completes so the tray can flip back to the signed-out state.
pub async fn run_tray_app(
    status_rx: watch::Receiver<SyncStatus>,
    sync_now_tx: mpsc::Sender<()>,
    config_path: PathBuf,
    credentials_ready: tokio::sync::oneshot::Sender<()>,
    sign_out_rx: mpsc::Receiver<()>,
) -> Result<()> {
    #[cfg(target_os = "linux")]
    return linux::run_tray_app(
        status_rx,
        sync_now_tx,
        config_path,
        credentials_ready,
        sign_out_rx,
    )
    .await;

    #[cfg(not(target_os = "linux"))]
    return stub::run_tray_app(
        status_rx,
        sync_now_tx,
        config_path,
        credentials_ready,
        sign_out_rx,
    )
    .await;
}

/// Extended entry point that wires in the API client and secret store for the
/// GTK sign-in dialog.  Only available on Linux with the `gtk` feature; on
/// other targets (and when GTK is not compiled in) falls back to `run_tray_app`.
#[cfg(all(target_os = "linux", feature = "gtk"))]
pub async fn run_tray_app_with_signin_deps(
    status_rx: watch::Receiver<SyncStatus>,
    sync_now_tx: mpsc::Sender<()>,
    config_path: PathBuf,
    credentials_ready: tokio::sync::oneshot::Sender<()>,
    sign_out_rx: mpsc::Receiver<()>,
    api: Arc<dyn api_client::ApiClientTrait>,
    store: Arc<dyn secret_store::SecretStore>,
) -> Result<()> {
    linux::run_tray_app_with_signin_deps(
        status_rx,
        sync_now_tx,
        config_path,
        credentials_ready,
        sign_out_rx,
        api,
        store,
    )
    .await
}

#[cfg(test)]
mod tests {
    /// Confirm that the config path passed to the tray (and forwarded to the
    /// settings dialog) can be round-tripped through `ConfigStore` without loss.
    /// This exercises the same load/save path that `open_settings_window` relies on.
    #[test]
    fn settings_config_round_trip() {
        use config_store::{AppConfig, ConfigStore, ConflictResolution};
        use tempfile::TempDir;

        let dir = TempDir::new().unwrap();
        let path = dir.path().join("config.toml");
        let store = ConfigStore::new(path.clone());

        let mut cfg = AppConfig::default();
        cfg.sync.interval_seconds = 120;
        cfg.sync.conflict_resolution = ConflictResolution::ConflictCopy;
        cfg.notifications.show_success = false;
        cfg.notifications.show_errors = false;
        store.save(&cfg).unwrap();

        let loaded = store.load().unwrap();
        assert_eq!(loaded.sync.interval_seconds, 120);
        assert_eq!(
            loaded.sync.conflict_resolution,
            ConflictResolution::ConflictCopy
        );
        assert!(!loaded.notifications.show_success);
        assert!(!loaded.notifications.show_errors);
    }
}
