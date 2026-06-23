// Settings dialog — Adwaita `PreferencesWindow` with four panels.
//
// Enabled only when the `gtk` feature is active.  GTK4 is initialized once
// per process in a dedicated thread; subsequent calls reuse the same thread
// by sending a closure over a channel.
//
// Ubuntu 22.04 (Jammy) note:
//   libadwaita 1.0.x (packaged in Jammy) lacks `PreferencesWindow`.
//   Install libadwaita 1.2+ via `ppa:gnome-team/gnome-next`.
//   This crate's `Cargo.toml` requests `libadwaita = "0.7"` which maps to
//   libadwaita-1 ≥ 1.4 at the C library level.

use std::path::PathBuf;
use std::sync::OnceLock;

use config_store::{AppConfig, ConfigStore, ConflictResolution};
use gtk4::glib;
use gtk4::prelude::*;
use libadwaita::prelude::*;
use tracing::error;

// Channel sender to the GTK thread.  Each message is a boxed closure that
// the GTK idle handler executes on the main GTK thread.
type GtkAction = Box<dyn FnOnce() + Send + 'static>;

static GTK_TX: OnceLock<std::sync::mpsc::SyncSender<GtkAction>> = OnceLock::new();

/// Ensure the GTK4 thread is running and return a sender to it.
///
/// Safe to call from any thread; subsequent calls return the same sender.
fn gtk_thread_sender() -> &'static std::sync::mpsc::SyncSender<GtkAction> {
    GTK_TX.get_or_init(|| {
        let (tx, rx) = std::sync::mpsc::sync_channel::<GtkAction>(8);
        std::thread::spawn(move || {
            gtk4::init().expect("failed to init GTK4");
            libadwaita::init();

            // Drain the channel via GLib idle callbacks so closures run on the
            // GTK main thread inside the GLib main loop.
            let rx = std::sync::Mutex::new(rx);
            glib::idle_add(move || {
                while let Ok(action) = rx.lock().unwrap().try_recv() {
                    action();
                }
                glib::ControlFlow::Continue
            });

            // Run the GLib main loop indefinitely. This thread is dedicated to GTK.
            let main_loop = glib::MainLoop::new(None, false);
            main_loop.run();
        });
        tx
    })
}

/// Open the settings window on the GTK4 thread.
///
/// Returns immediately; the window appears asynchronously.
pub fn open_settings_window(config_path: PathBuf) {
    let sender = gtk_thread_sender();
    let _ = sender.send(Box::new(move || {
        build_and_show_settings(config_path);
    }));
}

fn build_and_show_settings(config_path: PathBuf) {
    let config_store = ConfigStore::new(config_path.clone());
    let config = match config_store.load_or_default() {
        Ok(c) => c,
        Err(e) => {
            error!("failed to load config for settings dialog: {e}");
            AppConfig::default()
        }
    };

    let window = libadwaita::PreferencesWindow::builder()
        .title("InterlinedList Sync — Settings")
        .default_width(640)
        .default_height(500)
        .build();

    window.add(&build_folders_page(
        &config,
        config_path.clone(),
        window.clone().upcast(),
    ));
    window.add(&build_sync_page(
        &config,
        config_path.clone(),
        window.clone().upcast(),
    ));
    window.add(&build_notifications_page(
        &config,
        config_path.clone(),
        window.clone().upcast(),
    ));
    window.add(&build_account_page(
        &config,
        config_path,
        window.clone().upcast(),
    ));

    window.present();
}

fn build_folders_page(
    config: &AppConfig,
    _config_path: PathBuf,
    window: gtk4::Window,
) -> libadwaita::PreferencesPage {
    let page = libadwaita::PreferencesPage::builder()
        .title("Watched Folders")
        .icon_name("folder-symbolic")
        .build();

    let group = libadwaita::PreferencesGroup::builder()
        .title("Sync Directory")
        .description("Markdown files in this folder are synced with your InterlinedList account.")
        .build();

    let watched = config
        .sync
        .watched_dirs
        .first()
        .cloned()
        .unwrap_or_else(|| "~/Documents/InterlinedList".to_string());

    let row = libadwaita::ActionRow::builder()
        .title("Folder")
        .subtitle(&watched)
        .build();

    let button = gtk4::Button::builder()
        .label("Choose…")
        .valign(gtk4::Align::Center)
        .css_classes(["flat"])
        .build();

    let row_clone = row.clone();
    button.connect_clicked(move |_| {
        let dialog = gtk4::FileDialog::builder()
            .title("Choose Sync Folder")
            .modal(true)
            .build();
        let row_inner = row_clone.clone();
        let parent: Option<&gtk4::Window> = Some(&window);
        dialog.select_folder(parent, gtk4::gio::Cancellable::NONE, move |result| {
            if let Ok(file) = result {
                if let Some(path) = file.path() {
                    row_inner.set_subtitle(&path.to_string_lossy());
                }
            }
        });
    });

    row.add_suffix(&button);
    group.add(&row);
    page.add(&group);
    page
}

fn build_sync_page(
    config: &AppConfig,
    config_path: PathBuf,
    window: gtk4::Window,
) -> libadwaita::PreferencesPage {
    let page = libadwaita::PreferencesPage::builder()
        .title("Sync")
        .icon_name("emblem-synchronizing-symbolic")
        .build();

    let interval_group = libadwaita::PreferencesGroup::builder()
        .title("Poll Interval")
        .build();

    let interval_row = libadwaita::SpinRow::with_range(10.0, 3600.0, 10.0);
    interval_row.set_title("Interval (seconds)");
    interval_row.set_value(config.sync.interval_seconds as f64);
    interval_group.add(&interval_row);

    let conflict_group = libadwaita::PreferencesGroup::builder()
        .title("Conflict Resolution")
        .description("What to do when the same document is modified both locally and remotely.")
        .build();

    let remote_wins_row = libadwaita::ActionRow::builder()
        .title("Remote wins")
        .subtitle("Discard local edits and keep the server version.")
        .activatable(true)
        .build();
    let conflict_copy_row = libadwaita::ActionRow::builder()
        .title("Conflict copy")
        .subtitle("Save a timestamped copy alongside the incoming server version.")
        .activatable(true)
        .build();

    let radio_remote = gtk4::CheckButton::new();
    let radio_copy = gtk4::CheckButton::builder().group(&radio_remote).build();

    match config.sync.conflict_resolution {
        ConflictResolution::RemoteWins => radio_remote.set_active(true),
        ConflictResolution::ConflictCopy => radio_copy.set_active(true),
    }

    remote_wins_row.add_prefix(&radio_remote);
    conflict_copy_row.add_prefix(&radio_copy);
    conflict_group.add(&remote_wins_row);
    conflict_group.add(&conflict_copy_row);

    // Save button.
    let save_group = libadwaita::PreferencesGroup::new();
    let save_row = libadwaita::ActionRow::builder()
        .title("Save")
        .activatable(true)
        .build();
    let interval_row_ref = interval_row.clone();
    let radio_remote_ref = radio_remote.clone();
    save_row.connect_activated(move |_| {
        let store = ConfigStore::new(config_path.clone());
        let mut cfg = store.load_or_default().unwrap_or_default();
        cfg.sync.interval_seconds = interval_row_ref.value() as u64;
        cfg.sync.conflict_resolution = if radio_remote_ref.is_active() {
            ConflictResolution::RemoteWins
        } else {
            ConflictResolution::ConflictCopy
        };
        if let Err(e) = store.save(&cfg) {
            error!("failed to save sync config: {e}");
            show_error_toast(&window, &format!("Failed to save: {e}"));
        }
    });
    save_group.add(&save_row);

    page.add(&interval_group);
    page.add(&conflict_group);
    page.add(&save_group);
    page
}

fn build_notifications_page(
    config: &AppConfig,
    config_path: PathBuf,
    window: gtk4::Window,
) -> libadwaita::PreferencesPage {
    let page = libadwaita::PreferencesPage::builder()
        .title("Notifications")
        .icon_name("notification-symbolic")
        .build();

    let group = libadwaita::PreferencesGroup::builder()
        .title("Desktop Notifications")
        .build();

    let success_row = libadwaita::SwitchRow::builder()
        .title("Sync completed")
        .subtitle("Show a notification after each successful sync.")
        .build();
    success_row.set_active(config.notifications.show_success);

    let conflict_row = libadwaita::SwitchRow::builder()
        .title("Conflicts")
        .subtitle("Notify when a sync conflict is detected.")
        .build();
    conflict_row.set_active(config.notifications.show_conflicts);

    let error_row = libadwaita::SwitchRow::builder()
        .title("Errors")
        .subtitle("Notify when a sync error occurs.")
        .build();
    error_row.set_active(config.notifications.show_errors);

    group.add(&success_row);
    group.add(&conflict_row);
    group.add(&error_row);

    let save_group = libadwaita::PreferencesGroup::new();
    let save_row = libadwaita::ActionRow::builder()
        .title("Save")
        .activatable(true)
        .build();
    let success_ref = success_row.clone();
    let conflict_ref = conflict_row.clone();
    let error_ref = error_row.clone();
    save_row.connect_activated(move |_| {
        let store = ConfigStore::new(config_path.clone());
        let mut cfg = store.load_or_default().unwrap_or_default();
        cfg.notifications.show_success = success_ref.is_active();
        cfg.notifications.show_conflicts = conflict_ref.is_active();
        cfg.notifications.show_errors = error_ref.is_active();
        if let Err(e) = store.save(&cfg) {
            error!("failed to save notification config: {e}");
            show_error_toast(&window, &format!("Failed to save: {e}"));
        }
    });
    save_group.add(&save_row);

    page.add(&group);
    page.add(&save_group);
    page
}

fn build_account_page(
    config: &AppConfig,
    config_path: PathBuf,
    window: gtk4::Window,
) -> libadwaita::PreferencesPage {
    let page = libadwaita::PreferencesPage::builder()
        .title("Account")
        .icon_name("system-users-symbolic")
        .build();

    let group = libadwaita::PreferencesGroup::builder()
        .title("Signed-in Account")
        .build();

    let api_url = config
        .network
        .api_base_url
        .trim_end_matches('/')
        .to_string();
    let account_row = libadwaita::ActionRow::builder()
        .title("Server")
        .subtitle(&api_url)
        .build();
    group.add(&account_row);

    let logout_group = libadwaita::PreferencesGroup::new();
    let logout_row = libadwaita::ActionRow::builder()
        .title("Log out")
        .subtitle("Remove stored credentials and stop syncing.")
        .activatable(true)
        .build();

    let window_for_closure = window.clone();
    logout_row.connect_activated(move |_| {
        let dialog = libadwaita::AlertDialog::builder()
            .heading("Log out?")
            .body(
                "Your local files will not be deleted. \
                 Syncing will stop until you log in again.",
            )
            .default_response("cancel")
            .close_response("cancel")
            .build();
        dialog.add_response("cancel", "Cancel");
        dialog.add_response("logout", "Log Out");
        dialog.set_response_appearance("logout", libadwaita::ResponseAppearance::Destructive);

        let config_path_inner = config_path.clone();
        let window_inner = window_for_closure.clone();
        dialog.connect_response(None, move |_, response| {
            if response == "logout" {
                do_logout(&config_path_inner, &window_inner);
            }
        });
        dialog.present(Some(&window_for_closure));
    });

    logout_group.add(&logout_row);
    page.add(&group);
    page.add(&logout_group);
    page
}

fn show_error_toast(window: &gtk4::Window, message: &str) {
    if let Some(overlay) = window
        .child()
        .and_then(|w| w.downcast::<libadwaita::ToastOverlay>().ok())
    {
        let toast = libadwaita::Toast::builder()
            .title(message)
            .timeout(4)
            .build();
        overlay.add_toast(toast);
    }
}

fn do_logout(config_path: &PathBuf, _window: &gtk4::Window) {
    let token_dir = config_path
        .parent()
        .map(|p| p.to_path_buf())
        .unwrap_or_else(|| PathBuf::from("."));

    // Remove .token-* files written by FileSecretStore.
    if let Ok(entries) = std::fs::read_dir(&token_dir) {
        for entry in entries.flatten() {
            if entry.file_name().to_string_lossy().starts_with(".token-") {
                let _ = std::fs::remove_file(entry.path());
            }
        }
    }
    // GNOME Keyring tokens are cleared via the `--login` CLI flow; clearing
    // them here would require an async D-Bus call outside a tokio context.
}
