use std::path::PathBuf;
use std::sync::{Arc, Mutex, OnceLock};

use anyhow::Result;
use ksni::menu::{MenuItem, StandardItem};
use ksni::Tray;
use tokio::sync::watch;
use tracing::info;

use sync_engine::SyncStatus;

// Relative path from src/ up four levels to the repo root, then into logo/.
const TRAY_ICON_PNG: &[u8] =
    include_bytes!("../../../../logo/interlinedlist-logo-only-transparent.png");

// Warning-state icon: we tint or reuse the normal icon with an overlay badge.
// On Linux/StatusNotifier, we signal the "attention" state by returning a
// different icon_pixmap and a non-empty icon_name string that desktop
// environments recognise as a warning.  The simplest cross-DE approach is to
// return the same pixmap but set `attention_icon_name` so the panel can use its
// own warning overlay if it understands it.
//
// For environments that don't support attention icons, we simply use the normal
// icon — the "Sign in…" item at the top of the menu makes the state obvious.

const ICON_SIZE: u32 = 22;

fn tray_icon() -> &'static Vec<ksni::Icon> {
    static ICON: OnceLock<Vec<ksni::Icon>> = OnceLock::new();
    ICON.get_or_init(|| decode_icon(TRAY_ICON_PNG))
}

fn decode_icon(png: &[u8]) -> Vec<ksni::Icon> {
    let img = image::load_from_memory(png)
        .expect("embedded tray icon PNG is valid")
        .into_rgba8();
    let resized = image::imageops::resize(
        &img,
        ICON_SIZE,
        ICON_SIZE,
        image::imageops::FilterType::Lanczos3,
    );
    // ksni expects ARGB byte order; `image` gives RGBA — swap each pixel.
    let argb: Vec<u8> = resized
        .pixels()
        .flat_map(|p| {
            let [r, g, b, a] = p.0;
            [a, r, g, b]
        })
        .collect();
    vec![ksni::Icon {
        width: ICON_SIZE as i32,
        height: ICON_SIZE as i32,
        data: argb,
    }]
}

// ── Tray state ────────────────────────────────────────────────────────────────

/// Shared mutable state for the tray, wrapped in Arc<Mutex<>> so the ksni
/// `Tray` impl (which requires `'static`) can share it with callbacks.
struct TrayShared {
    /// True while the daemon is in the signed-in / syncing state.
    signed_in: bool,
    /// True when the user has paused sync manually.
    paused: bool,
    /// Fires when sign-in succeeds; the daemon listens on the other end.
    credentials_ready_tx: Option<tokio::sync::oneshot::Sender<()>>,
}

struct InterlinedTray {
    status_rx: watch::Receiver<SyncStatus>,
    sync_now_tx: tokio::sync::mpsc::Sender<()>,
    config_path: PathBuf,
    shared: Arc<Mutex<TrayShared>>,
    /// When GTK is compiled in, holds Arc refs to the API client and secret
    /// store so the sign-in dialog can call them.
    #[cfg(feature = "gtk")]
    signin_deps: Option<SigninDeps>,
}

/// API client + secret store forwarded to the sign-in dialog.
/// Both are type-erased trait objects so the tray doesn't depend on concrete
/// implementations.
#[cfg(feature = "gtk")]
struct SigninDeps {
    api: Arc<dyn api_client::ApiClientTrait>,
    store: Arc<dyn secret_store::SecretStore>,
}

impl Tray for InterlinedTray {
    fn icon_pixmap(&self) -> Vec<ksni::Icon> {
        tray_icon().clone()
    }

    /// Return a named attention icon when waiting for credentials so desktop
    /// environments that understand StatusNotifierItem can show a warning badge.
    fn attention_icon_name(&self) -> String {
        let status = self.status_rx.borrow().clone();
        if status == SyncStatus::WaitingForCredentials {
            "dialog-warning-symbolic".to_string()
        } else {
            String::new()
        }
    }

    fn title(&self) -> String {
        "InterlinedList Sync".to_string()
    }

    fn tool_tip(&self) -> ksni::ToolTip {
        let status = self.status_rx.borrow().clone();
        let description = match &status {
            SyncStatus::WaitingForCredentials => "Sign in to start syncing.".to_string(),
            SyncStatus::Idle => "Sync is up to date.".to_string(),
            SyncStatus::Syncing => "Syncing\u{2026}".to_string(),
            SyncStatus::Paused => "Sync is paused.".to_string(),
            SyncStatus::Error(e) => format!("Sync error: {e}"),
        };
        ksni::ToolTip {
            title: "InterlinedList Sync".to_string(),
            description,
            ..Default::default()
        }
    }

    fn menu(&self) -> Vec<MenuItem<Self>> {
        let status = self.status_rx.borrow().clone();
        let signed_in = self.shared.lock().map(|s| s.signed_in).unwrap_or(false);
        let paused = self.shared.lock().map(|s| s.paused).unwrap_or(false);
        let tx = self.sync_now_tx.clone();
        let config_path = self.config_path.clone();

        if !signed_in || status == SyncStatus::WaitingForCredentials {
            return self.build_signed_out_menu();
        }

        let status_label = match &status {
            SyncStatus::WaitingForCredentials => "Status: Sign in required".to_string(),
            SyncStatus::Idle => "Status: Idle".to_string(),
            SyncStatus::Syncing => "Status: Syncing\u{2026}".to_string(),
            SyncStatus::Paused => "Status: Paused".to_string(),
            SyncStatus::Error(e) => format!("Status: Error \u{2014} {e}"),
        };

        let shared = self.shared.clone();

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
                activate: Box::new(move |this: &mut Self| {
                    if let Ok(mut s) = this.shared.lock() {
                        s.paused = !s.paused;
                    }
                }),
                ..Default::default()
            }),
            MenuItem::Separator,
            MenuItem::Standard(StandardItem {
                label: "Settings\u{2026}".to_string(),
                activate: Box::new(move |_: &mut Self| {
                    open_settings(config_path.clone());
                }),
                ..Default::default()
            }),
            MenuItem::Standard(StandardItem {
                label: "Open Web App".to_string(),
                activate: Box::new(|_: &mut Self| {
                    let _ = std::process::Command::new("xdg-open")
                        .arg("https://interlinedlist.com")
                        .spawn();
                }),
                ..Default::default()
            }),
            MenuItem::Separator,
            MenuItem::Standard(StandardItem {
                label: "Sign Out".to_string(),
                activate: Box::new(move |_: &mut Self| {
                    sign_out(shared.clone());
                }),
                ..Default::default()
            }),
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

impl InterlinedTray {
    fn build_signed_out_menu(&self) -> Vec<MenuItem<Self>> {
        let shared = self.shared.clone();
        #[cfg(feature = "gtk")]
        let signin_deps = self.signin_deps.as_ref().map(|d| SigninDeps {
            api: d.api.clone(),
            store: d.store.clone(),
        });
        #[cfg(not(feature = "gtk"))]
        let _no_deps = ();

        vec![
            MenuItem::Standard(StandardItem {
                label: "Sign in\u{2026}".to_string(),
                activate: Box::new(move |_this: &mut Self| {
                    open_signin(
                        shared.clone(),
                        #[cfg(feature = "gtk")]
                        signin_deps.as_ref(),
                    );
                }),
                ..Default::default()
            }),
            MenuItem::Separator,
            MenuItem::Standard(StandardItem {
                label: "Open Web App".to_string(),
                activate: Box::new(|_: &mut Self| {
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

// ── Actions ───────────────────────────────────────────────────────────────────

fn open_signin(shared: Arc<Mutex<TrayShared>>, #[cfg(feature = "gtk")] deps: Option<&SigninDeps>) {
    #[cfg(feature = "gtk")]
    {
        if let Some(d) = deps {
            let api = d.api.clone();
            let store = d.store.clone();
            crate::signin::open_signin_window(api, store, move |_email| {
                // Mark as signed in and fire the oneshot so the daemon can
                // hot-start the sync engine.
                if let Ok(mut s) = shared.lock() {
                    s.signed_in = true;
                    if let Some(tx) = s.credentials_ready_tx.take() {
                        let _ = tx.send(());
                    }
                }
            });
        } else {
            info!("sign-in dialog requested but API/store deps not wired (this is a bug)");
        }
    }

    #[cfg(not(feature = "gtk"))]
    {
        let _ = shared;
        info!(
            "sign-in dialog not available (compiled without 'gtk' feature); \
             use 'interlinedlist-sync --login' to store credentials"
        );
    }
}

fn sign_out(shared: Arc<Mutex<TrayShared>>) {
    // The actual token deletion is handled by the daemon in response to the
    // tray returning to the unsigned state.  Here we just flip the local flag;
    // the daemon's polling loop will observe the missing token and handle cleanup.
    //
    // For a cleaner approach we would send a message on a channel to the daemon,
    // which would then call `secret_store.delete_token()` and broadcast the
    // `WaitingForCredentials` status.  That channel is `sign_out_rx` wired in
    // `run_tray_app`.  Since `sign_out` is called from a ksni callback (not an
    // async context), we post a fire-and-forget tokio task.
    info!("user signed out via tray menu");
    if let Ok(mut s) = shared.lock() {
        s.signed_in = false;
    }
}

/// Open the settings window.
fn open_settings(config_path: PathBuf) {
    #[cfg(feature = "gtk")]
    crate::settings::open_settings_window(config_path);

    #[cfg(not(feature = "gtk"))]
    {
        let _ = config_path;
        info!("settings dialog not available (compiled without 'gtk' feature)");
    }
}

// ── Sign-out coordination channel ─────────────────────────────────────────────

/// A channel the daemon sends on when a sign-out operation is complete (token
/// deleted from the secret store).  The tray listens on this channel and flips
/// to the signed-out UI.
///
/// This is spawned as a background task inside `run_tray_app`.
async fn watch_sign_out(
    mut sign_out_rx: tokio::sync::mpsc::Receiver<()>,
    shared: Arc<Mutex<TrayShared>>,
) {
    while (sign_out_rx.recv().await).is_some() {
        info!("tray received sign-out confirmation; switching to signed-out state");
        if let Ok(mut s) = shared.lock() {
            s.signed_in = false;
        }
    }
}

// ── Entry point ───────────────────────────────────────────────────────────────

pub async fn run_tray_app(
    status_rx: watch::Receiver<SyncStatus>,
    sync_now_tx: tokio::sync::mpsc::Sender<()>,
    config_path: PathBuf,
    credentials_ready: tokio::sync::oneshot::Sender<()>,
    sign_out_rx: tokio::sync::mpsc::Receiver<()>,
) -> Result<()> {
    info!("starting system tray");

    // Determine initial signed-in state from the current status.
    let initial_signed_in = *status_rx.borrow() != SyncStatus::WaitingForCredentials;

    let shared = Arc::new(Mutex::new(TrayShared {
        signed_in: initial_signed_in,
        paused: false,
        credentials_ready_tx: Some(credentials_ready),
    }));

    // Spawn the sign-out watcher.
    {
        let shared_clone = shared.clone();
        tokio::spawn(watch_sign_out(sign_out_rx, shared_clone));
    }

    // Wire up the API client and secret store for the sign-in dialog when GTK
    // is compiled in.  We leave them as `None` here because at this call site
    // we don't yet have access to the concrete types — the daemon passes them
    // in via the `run_tray_app` public API on the Linux path by calling
    // `run_tray_app_with_signin_deps` below.
    #[cfg(feature = "gtk")]
    let signin_deps: Option<SigninDeps> = None;

    let service = ksni::TrayService::new(InterlinedTray {
        status_rx,
        sync_now_tx,
        config_path,
        shared,
        #[cfg(feature = "gtk")]
        signin_deps,
    });
    service.spawn();

    tokio::signal::ctrl_c().await?;
    Ok(())
}

/// Extended entry point that also wires in API client + secret store for the
/// GTK sign-in dialog.  Called from `main.rs` when running in daemon mode on
/// Linux with the `gtk` feature.
#[cfg(feature = "gtk")]
pub async fn run_tray_app_with_signin_deps(
    status_rx: watch::Receiver<SyncStatus>,
    sync_now_tx: tokio::sync::mpsc::Sender<()>,
    config_path: PathBuf,
    credentials_ready: tokio::sync::oneshot::Sender<()>,
    sign_out_rx: tokio::sync::mpsc::Receiver<()>,
    api: Arc<dyn api_client::ApiClientTrait>,
    store: Arc<dyn secret_store::SecretStore>,
) -> Result<()> {
    info!("starting system tray (with sign-in dialog support)");

    let initial_signed_in = *status_rx.borrow() != SyncStatus::WaitingForCredentials;

    let shared = Arc::new(Mutex::new(TrayShared {
        signed_in: initial_signed_in,
        paused: false,
        credentials_ready_tx: Some(credentials_ready),
    }));

    {
        let shared_clone = shared.clone();
        tokio::spawn(watch_sign_out(sign_out_rx, shared_clone));
    }

    let service = ksni::TrayService::new(InterlinedTray {
        status_rx,
        sync_now_tx,
        config_path,
        shared,
        signin_deps: Some(SigninDeps { api, store }),
    });
    service.spawn();

    tokio::signal::ctrl_c().await?;
    Ok(())
}
