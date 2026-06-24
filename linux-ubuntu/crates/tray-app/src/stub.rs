use std::path::PathBuf;

use anyhow::Result;
use tokio::sync::{mpsc, watch};

use sync_engine::SyncStatus;

/// Non-Linux stub so `cargo check --workspace` passes on macOS dev machines.
pub async fn run_tray_app(
    _status_rx: watch::Receiver<SyncStatus>,
    _sync_now_tx: mpsc::Sender<()>,
    _config_path: PathBuf,
    _credentials_ready: tokio::sync::oneshot::Sender<()>,
    _sign_out_rx: mpsc::Receiver<()>,
) -> Result<()> {
    anyhow::bail!("tray-app requires Linux (GTK4 / ksni)")
}
