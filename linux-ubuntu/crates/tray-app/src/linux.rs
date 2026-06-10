use anyhow::Result;
use ksni::menu::{MenuItem, StandardItem};
use ksni::Tray;
use tokio::sync::watch;
use tracing::info;

use sync_engine::SyncStatus;

struct InterlinedTray {
    status_rx: watch::Receiver<SyncStatus>,
    sync_now_tx: tokio::sync::mpsc::Sender<()>,
    paused: bool,
}

impl Tray for InterlinedTray {
    fn icon_name(&self) -> String {
        let status = self.status_rx.borrow();
        match &*status {
            SyncStatus::Idle => "dialog-information",
            SyncStatus::Syncing => "emblem-synchronizing",
            SyncStatus::Paused => "media-playback-pause",
            SyncStatus::Error(_) => "dialog-error",
        }
        .to_string()
    }

    fn title(&self) -> String {
        "InterlinedList Sync".to_string()
    }

    fn menu(&self) -> Vec<MenuItem<Self>> {
        let status_borrow = self.status_rx.borrow();
        let status_label = match &*status_borrow {
            SyncStatus::Idle => "Status: Idle".to_string(),
            SyncStatus::Syncing => "Status: Syncing\u{2026}".to_string(),
            SyncStatus::Paused => "Status: Paused".to_string(),
            SyncStatus::Error(e) => format!("Status: Error \u{2014} {e}"),
        };
        drop(status_borrow);

        let paused = self.paused;
        let tx = self.sync_now_tx.clone();

        vec![
            MenuItem::Standard(StandardItem {
                label: status_label,
                enabled: false,
                ..Default::default()
            }),
            MenuItem::Separator,
            MenuItem::Standard(StandardItem {
                label: "Sync Now".to_string(),
                activate: Box::new(move |_this: &mut Self| {
                    let _ = tx.try_send(());
                }),
                ..Default::default()
            }),
            MenuItem::Standard(StandardItem {
                label: if paused {
                    "Resume Sync".to_string()
                } else {
                    "Pause Sync".to_string()
                },
                activate: Box::new(|this: &mut Self| {
                    this.paused = !this.paused;
                }),
                ..Default::default()
            }),
            MenuItem::Separator,
            MenuItem::Standard(StandardItem {
                label: "Open Web App".to_string(),
                activate: Box::new(|_: &mut Self| {
                    // xdg-open is the freedesktop standard URL opener on Ubuntu.
                    let _ = std::process::Command::new("xdg-open")
                        .arg("https://interlinedlist.com")
                        .spawn();
                }),
                ..Default::default()
            }),
            MenuItem::Separator,
            MenuItem::Standard(StandardItem {
                label: "Quit".to_string(),
                activate: Box::new(|_: &mut Self| {
                    std::process::exit(0);
                }),
                ..Default::default()
            }),
        ]
    }
}

pub async fn run_tray_app(
    status_rx: watch::Receiver<SyncStatus>,
    sync_now_tx: tokio::sync::mpsc::Sender<()>,
) -> Result<()> {
    info!("starting system tray");

    // ksni spawns a background thread for the D-Bus StatusNotifierItem service.
    // In ksni 0.2, spawn() returns () and runs until the process exits.
    let service = ksni::TrayService::new(InterlinedTray {
        status_rx,
        sync_now_tx,
        paused: false,
    });
    service.spawn();

    tokio::signal::ctrl_c().await?;
    // _handle drops here, which shuts down the tray service.
    Ok(())
}
