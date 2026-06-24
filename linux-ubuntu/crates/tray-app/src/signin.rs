// Sign-in dialog — Adwaita `Window` with email + password fields.
//
// Enabled only when the `gtk` feature is active.  Uses the same GTK4 thread
// model as `settings.rs`: a dedicated background thread owns the GLib main
// loop; callers dispatch closures to it via a channel so GTK objects are always
// touched on the single GTK thread.
//
// SECURITY NOTES:
// - The password Entry has `set_visibility(false)` — the text is masked.
// - Credentials are NEVER written to any log at any log level.
// - The token is only stored AFTER a successful `ApiClient::login()` call; a
//   failed login stores nothing.
// - The sign-in HTTP request body is assembled inside the GTK thread and never
//   surfaced to the caller as a string.

use std::sync::Arc;

use gtk4::glib;
use gtk4::prelude::*;
use libadwaita::prelude::*;
use tracing::{error, info, warn};

use api_client::ApiClientTrait;
use secret_store::SecretStore;

use crate::settings::gtk_thread_sender;

/// Open the sign-in dialog on the GTK4 thread.
///
/// `on_success` is called (on the GTK thread) with the account email string
/// after the token has been persisted to the secret store.  Callers use this to
/// notify the daemon that credentials are now available.
///
/// `api` and `store` are `Arc`-wrapped so they can be moved into the closure
/// sent to the GTK thread.
pub fn open_signin_window(
    api: Arc<dyn ApiClientTrait>,
    store: Arc<dyn SecretStore>,
    on_success: impl FnOnce(String) + Send + 'static,
) {
    let sender = gtk_thread_sender();
    let on_success = std::sync::Mutex::new(Some(on_success));
    let _ = sender.send(Box::new(move || {
        build_and_show_signin(api, store, move |email| {
            if let Some(cb) = on_success.lock().unwrap().take() {
                cb(email);
            }
        });
    }));
}

fn build_and_show_signin(
    api: Arc<dyn ApiClientTrait>,
    store: Arc<dyn SecretStore>,
    on_success: impl FnOnce(String) + 'static,
) {
    // Wrap on_success so it can be called at most once from a button click.
    let on_success = std::cell::RefCell::new(Some(on_success));

    let window = libadwaita::Window::builder()
        .title("Sign in to InterlinedList")
        .default_width(400)
        .modal(true)
        .resizable(false)
        .build();

    // ── Root layout ──────────────────────────────────────────────────────────
    let content = gtk4::Box::builder()
        .orientation(gtk4::Orientation::Vertical)
        .spacing(0)
        .build();
    window.set_content(Some(&content));

    // Header bar
    let header = libadwaita::HeaderBar::builder()
        .show_end_title_buttons(false)
        .show_start_title_buttons(false)
        .build();
    content.append(&header);

    // Body box with padding
    let body = gtk4::Box::builder()
        .orientation(gtk4::Orientation::Vertical)
        .spacing(12)
        .margin_top(24)
        .margin_bottom(24)
        .margin_start(24)
        .margin_end(24)
        .build();
    content.append(&body);

    // App logo / title
    let logo_label = gtk4::Label::builder()
        .label("InterlinedList Sync")
        .css_classes(["title-1"])
        .halign(gtk4::Align::Center)
        .build();
    body.append(&logo_label);

    let subtitle = gtk4::Label::builder()
        .label("Enter your account credentials to start syncing.")
        .halign(gtk4::Align::Center)
        .wrap(true)
        .margin_top(4)
        .margin_bottom(16)
        .build();
    body.append(&subtitle);

    // Credentials group
    let group = libadwaita::PreferencesGroup::new();

    let email_row = libadwaita::EntryRow::builder()
        .title("Email")
        .input_purpose(gtk4::InputPurpose::Email)
        .build();

    let password_row = libadwaita::PasswordEntryRow::builder()
        .title("Password")
        .build();

    group.add(&email_row);
    group.add(&password_row);
    body.append(&group);

    // Error label — hidden until there is a message to display.
    let error_label = gtk4::Label::builder()
        .label("")
        .halign(gtk4::Align::Center)
        .wrap(true)
        .css_classes(["error"])
        .visible(false)
        .build();
    body.append(&error_label);

    // Sign-in button
    let sign_in_btn = gtk4::Button::builder()
        .label("Sign In")
        .css_classes(["suggested-action", "pill"])
        .halign(gtk4::Align::Center)
        .margin_top(8)
        .build();
    body.append(&sign_in_btn);

    // ── Button logic ─────────────────────────────────────────────────────────
    {
        let email_row = email_row.clone();
        let password_row = password_row.clone();
        let error_label = error_label.clone();
        let sign_in_btn_clone = sign_in_btn.clone();
        let window_clone = window.clone();

        sign_in_btn.connect_clicked(move |btn| {
            let email = email_row.text().to_string();
            let password = password_row.text().to_string();

            if email.is_empty() || password.is_empty() {
                set_error(&error_label, "Email and password are required.");
                return;
            }

            // Clear any previous error and disable the button while the request
            // is in flight so the user cannot double-submit.
            clear_error(&error_label);
            btn.set_sensitive(false);
            btn.set_label("Signing in\u{2026}");

            let api = api.clone();
            let store = store.clone();
            let email_clone = email.clone();
            let error_label = error_label.clone();
            let sign_in_btn = sign_in_btn_clone.clone();
            let window = window_clone.clone();
            // on_success can only fire once; take it out of the RefCell.
            let success_cb = on_success.borrow_mut().take();

            // Spawn onto the GLib main loop so the GTK thread drives the future.
            // We use `glib::spawn_future_local` (GLib 0.19+) which runs the
            // future on the current GLib context.  The GTK thread's GLib main
            // loop processes it.
            glib::spawn_future_local(async move {
                // IMPORTANT: the password variable is consumed here and NEVER
                // logged.  Do not add debug/trace logging of `password`.
                match api.login(&email_clone, &password).await {
                    Ok(token) => {
                        // Validate before persisting: we already received the token
                        // from a successful login call, so now persist it.
                        match store.store_token(&email_clone, &token).await {
                            Ok(()) => {
                                info!("sign-in successful for {email_clone}; token stored");
                                window.close();
                                if let Some(cb) = success_cb {
                                    cb(email_clone);
                                }
                            }
                            Err(e) => {
                                error!("failed to persist token after sign-in: {e}");
                                set_error(
                                    &error_label,
                                    &format!("Failed to save credentials: {e}"),
                                );
                                reset_button(&sign_in_btn);
                            }
                        }
                    }
                    Err(api_client::ApiError::Auth(_)) => {
                        warn!("sign-in failed: invalid credentials");
                        set_error(
                            &error_label,
                            "Incorrect email or password. Please try again.",
                        );
                        reset_button(&sign_in_btn);
                    }
                    Err(e) => {
                        warn!("sign-in failed: {e}");
                        set_error(&error_label, &format!("Sign-in failed: {e}"));
                        reset_button(&sign_in_btn);
                    }
                }
            });
        });
    }

    // Allow pressing Enter in either field to activate the sign-in button.
    {
        let btn = sign_in_btn.clone();
        email_row.connect_entry_activated(move |_| btn.activate());
    }
    {
        let btn = sign_in_btn.clone();
        password_row.connect_entry_activated(move |_| btn.activate());
    }

    window.present();
}

fn set_error(label: &gtk4::Label, message: &str) {
    label.set_text(message);
    label.set_visible(true);
}

fn clear_error(label: &gtk4::Label) {
    label.set_text("");
    label.set_visible(false);
}

fn reset_button(btn: &gtk4::Button) {
    btn.set_label("Sign In");
    btn.set_sensitive(true);
}
