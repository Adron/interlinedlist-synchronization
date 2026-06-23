use std::path::PathBuf;

use anyhow::Result;
use tokio::sync::{mpsc, watch};

use sync_engine::SyncStatus;

#[cfg(target_os = "linux")]
mod linux;

#[cfg(all(target_os = "linux", feature = "gtk"))]
pub(crate) mod settings;

#[cfg(not(target_os = "linux"))]
mod stub;

pub async fn run_tray_app(
    status_rx: watch::Receiver<SyncStatus>,
    sync_now_tx: mpsc::Sender<()>,
    config_path: PathBuf,
) -> Result<()> {
    #[cfg(target_os = "linux")]
    return linux::run_tray_app(status_rx, sync_now_tx, config_path).await;

    #[cfg(not(target_os = "linux"))]
    return stub::run_tray_app(status_rx, sync_now_tx, config_path).await;
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
