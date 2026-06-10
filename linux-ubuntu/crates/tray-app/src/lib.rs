use anyhow::Result;
use tokio::sync::{mpsc, watch};

use sync_engine::SyncStatus;

#[cfg(target_os = "linux")]
mod linux;

#[cfg(not(target_os = "linux"))]
mod stub;

pub async fn run_tray_app(
    status_rx: watch::Receiver<SyncStatus>,
    sync_now_tx: mpsc::Sender<()>,
) -> Result<()> {
    #[cfg(target_os = "linux")]
    return linux::run_tray_app(status_rx, sync_now_tx).await;

    #[cfg(not(target_os = "linux"))]
    return stub::run_tray_app(status_rx, sync_now_tx).await;
}
