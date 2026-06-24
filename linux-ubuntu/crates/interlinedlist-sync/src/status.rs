//! `--status` subcommand implementation.
//!
//! Prints a sanitized, human-readable diagnostic report covering:
//!   - config file path and existence
//!   - last sync time from the state DB
//!   - log file path and its last 10 lines
//!   - secret-store backend in use + whether a token is present (boolean only)
//!   - systemd unit state (best-effort, tolerates absence)
//!
//! Exits 0 if all essential components are present, 1 if anything is missing.
//! The token value is NEVER printed.

use std::path::{Path, PathBuf};
use std::process::Command;

/// Summary of a single diagnostic check, used for both display and exit-code
/// decisions.
#[derive(Debug, PartialEq, Eq)]
pub enum CheckState {
    Ok,
    Warning,
    Missing,
}

/// One line in the status report — a label, a value, and a severity.
#[derive(Debug)]
pub struct StatusItem {
    pub label: String,
    pub value: String,
    pub state: CheckState,
}

impl StatusItem {
    pub fn ok(label: impl Into<String>, value: impl Into<String>) -> Self {
        Self {
            label: label.into(),
            value: value.into(),
            state: CheckState::Ok,
        }
    }

    pub fn warning(label: impl Into<String>, value: impl Into<String>) -> Self {
        Self {
            label: label.into(),
            value: value.into(),
            state: CheckState::Warning,
        }
    }

    pub fn missing(label: impl Into<String>, value: impl Into<String>) -> Self {
        Self {
            label: label.into(),
            value: value.into(),
            state: CheckState::Missing,
        }
    }
}

/// All information gathered by the status command.
#[derive(Debug)]
pub struct StatusReport {
    pub items: Vec<StatusItem>,
    pub log_tail: Vec<String>,
    pub log_file_path: PathBuf,
}

impl StatusReport {
    /// Returns true if nothing essential is missing. `Warning` items (e.g. "no
    /// sync yet", "systemd unit not active", "state DB not created") are
    /// "not set up yet" states the user does not have to fix, so they do NOT
    /// fail the health check. Only `Missing` items (e.g. no credential token)
    /// drive a non-zero exit code, matching the spec: "exit 1 if anything's
    /// missing".
    pub fn is_healthy(&self) -> bool {
        !self.items.iter().any(|i| i.state == CheckState::Missing)
    }

    /// Render the report to a human-readable string suitable for printing to
    /// stdout or stderr without revealing secrets.
    pub fn render(&self) -> String {
        let mut out = String::new();
        out.push_str("InterlinedList Sync — status report\n");
        out.push_str("=====================================\n");

        for item in &self.items {
            let marker = match item.state {
                CheckState::Ok => "OK ",
                CheckState::Warning => "WRN",
                CheckState::Missing => "ERR",
            };
            out.push_str(&format!("[{}] {}: {}\n", marker, item.label, item.value));
        }

        if !self.log_tail.is_empty() {
            out.push('\n');
            out.push_str(&format!(
                "Last {} lines of {}:\n",
                self.log_tail.len(),
                self.log_file_path.display()
            ));
            out.push_str("-------------------------------------\n");
            for line in &self.log_tail {
                out.push_str(line);
                out.push('\n');
            }
            out.push_str("-------------------------------------\n");
        } else {
            out.push('\n');
            out.push_str(&format!(
                "Log file: {} (no content or not found)\n",
                self.log_file_path.display()
            ));
        }

        out
    }
}

/// Inputs that the status command needs. All injectable for unit testing — no
/// real keyring, filesystem, or process calls required.
pub struct StatusInputs {
    pub config_path: PathBuf,
    pub log_file_path: PathBuf,
    pub state_db_path: PathBuf,
    pub last_sync_time: Option<String>,
    pub secret_backend: SecretBackend,
    pub token_present: bool,
}

/// Which credential backend is currently active.
#[derive(Debug, Clone, PartialEq, Eq)]
#[allow(dead_code)] // GnomeKeyring is only constructed on Linux targets
pub enum SecretBackend {
    GnomeKeyring,
    FileFallback,
}

impl SecretBackend {
    pub fn as_str(&self) -> &'static str {
        match self {
            SecretBackend::GnomeKeyring => "GNOME Keyring (libsecret)",
            SecretBackend::FileFallback => "file fallback (~/.config/interlinedlist-sync/.token-*)",
        }
    }
}

/// Build the status report from injectable inputs plus best-effort live probes
/// (log tail, systemd state).
pub fn build_report(inputs: StatusInputs) -> StatusReport {
    let mut items = Vec::new();

    // --- Config file ---
    if inputs.config_path.exists() {
        items.push(StatusItem::ok(
            "Config file",
            inputs.config_path.display().to_string(),
        ));
    } else {
        items.push(StatusItem::warning(
            "Config file",
            format!(
                "{} (not found — daemon will create defaults on first run)",
                inputs.config_path.display()
            ),
        ));
    }

    // --- State DB ---
    if inputs.state_db_path.exists() {
        items.push(StatusItem::ok(
            "State DB",
            inputs.state_db_path.display().to_string(),
        ));
    } else {
        items.push(StatusItem::warning(
            "State DB",
            format!(
                "{} (not found — created on first sync)",
                inputs.state_db_path.display()
            ),
        ));
    }

    // --- Last sync time ---
    match inputs.last_sync_time {
        Some(t) => items.push(StatusItem::ok("Last sync time", t)),
        None => items.push(StatusItem::warning(
            "Last sync time",
            "unknown (no documents synced yet or state DB empty)".to_string(),
        )),
    }

    // --- Secret backend ---
    items.push(StatusItem::ok(
        "Credential backend",
        inputs.secret_backend.as_str().to_string(),
    ));

    // --- Token presence (boolean only — NEVER the token value) ---
    if inputs.token_present {
        items.push(StatusItem::ok("Token present", "yes".to_string()));
    } else {
        items.push(StatusItem::missing(
            "Token present",
            "no — run: interlinedlist-sync --login".to_string(),
        ));
    }

    // --- Log file ---
    if inputs.log_file_path.exists() {
        items.push(StatusItem::ok(
            "Log file",
            inputs.log_file_path.display().to_string(),
        ));
    } else {
        items.push(StatusItem::warning(
            "Log file",
            format!(
                "{} (not found — created on first daemon start)",
                inputs.log_file_path.display()
            ),
        ));
    }

    // --- systemd unit state (best-effort, tolerates absence) ---
    let unit_state = probe_systemd_unit_state();
    match unit_state.as_deref() {
        Some("active") => items.push(StatusItem::ok("systemd unit state", "active".to_string())),
        Some(s) => items.push(StatusItem::warning(
            "systemd unit state",
            format!("{s} (to enable: systemctl --user enable --now interlinedlist-sync.service)"),
        )),
        None => items.push(StatusItem::warning(
            "systemd unit state",
            "unavailable (systemctl not found or not running under systemd)".to_string(),
        )),
    }

    // --- Log tail ---
    let log_tail = read_log_tail(&inputs.log_file_path, 10);

    StatusReport {
        items,
        log_tail,
        log_file_path: inputs.log_file_path,
    }
}

/// Shell out to `systemctl --user is-active interlinedlist-sync.service`.
/// Returns None if systemctl is absent or the call fails; returns Some(state)
/// otherwise. The state is a single word such as "active", "inactive", "failed".
fn probe_systemd_unit_state() -> Option<String> {
    let output = Command::new("systemctl")
        .args(["--user", "is-active", "interlinedlist-sync.service"])
        .output()
        .ok()?;
    // is-active writes the state word to stdout; strip trailing newline.
    let state = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if state.is_empty() {
        None
    } else {
        Some(state)
    }
}

/// Read the last `n` lines of a log file. Returns an empty Vec if the file
/// does not exist or cannot be read.
fn read_log_tail(path: &Path, n: usize) -> Vec<String> {
    let content = match std::fs::read_to_string(path) {
        Ok(c) => c,
        Err(_) => return Vec::new(),
    };
    let lines: Vec<String> = content.lines().map(|l| l.to_string()).collect();
    let start = lines.len().saturating_sub(n);
    lines[start..].to_vec()
}

/// Query the state DB for the most-recently-synced timestamp across all
/// documents. Returns `None` if the DB cannot be opened or no synced documents
/// exist. This is intentionally a read-only, best-effort probe.
pub fn last_sync_time_from_db(db_path: &Path) -> Option<String> {
    use rusqlite::Connection;
    let conn = Connection::open(db_path).ok()?;
    // `MAX(synced_at)` returns SQL NULL when the table is empty; rusqlite
    // maps that to `Ok(None)` for `Option<String>`. We flatten both the
    // error case and the NULL case into our outer `Option<String>`.
    conn.query_row(
        "SELECT MAX(synced_at) FROM documents WHERE synced_at IS NOT NULL",
        [],
        |row| row.get::<_, Option<String>>(0),
    )
    .ok()
    .flatten()
}

// ---------------------------------------------------------------------------
// Unit tests — all injectable, no real keyring / filesystem / systemd needed.
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use tempfile::TempDir;

    fn fake_inputs(
        tmp: &TempDir,
        config_exists: bool,
        token_present: bool,
        backend: SecretBackend,
    ) -> StatusInputs {
        let config_path = tmp.path().join("config.toml");
        if config_exists {
            std::fs::write(&config_path, "[sync]\nwatched_dirs = []\n").unwrap();
        }
        StatusInputs {
            config_path,
            log_file_path: tmp.path().join("interlinedlist-sync.log"),
            state_db_path: tmp.path().join("state.db"),
            last_sync_time: None,
            secret_backend: backend,
            token_present,
        }
    }

    #[test]
    fn report_is_healthy_when_config_exists_and_token_present() {
        let tmp = TempDir::new().unwrap();
        let inputs = fake_inputs(&tmp, true, true, SecretBackend::GnomeKeyring);
        let report = build_report(inputs);
        // Token present + config exists = no Missing items (WRN on sync time,
        // log, state-db, and systemd are acceptable — they don't indicate
        // "broken", just "not set up yet").
        let missing_count = report
            .items
            .iter()
            .filter(|i| i.state == CheckState::Missing)
            .count();
        assert_eq!(missing_count, 0, "no Missing items expected");
    }

    #[test]
    fn report_missing_when_no_token() {
        let tmp = TempDir::new().unwrap();
        let inputs = fake_inputs(&tmp, false, false, SecretBackend::FileFallback);
        let report = build_report(inputs);
        let missing: Vec<_> = report
            .items
            .iter()
            .filter(|i| i.state == CheckState::Missing)
            .collect();
        assert_eq!(missing.len(), 1);
        assert_eq!(missing[0].label, "Token present");
        // The value must NOT contain the word "token" paired with any value-like
        // content — just a human hint.
        assert!(missing[0].value.contains("--login"));
    }

    #[test]
    fn render_does_not_expose_token_value() {
        let tmp = TempDir::new().unwrap();
        // Even if we pretend a token is present, the rendered output must only
        // say "yes", never anything that looks like an actual token.
        let inputs = fake_inputs(&tmp, true, true, SecretBackend::GnomeKeyring);
        let report = build_report(inputs);
        let rendered = report.render();
        // "yes" is the only allowed value for the token line
        assert!(rendered.contains("Token present: yes"));
        // Must not contain any long hex/base64-looking strings
        // (a real token would be much longer; "yes" is safe)
        for line in rendered.lines() {
            if line.contains("Token present") {
                assert!(
                    line.ends_with("yes") || line.contains("no —"),
                    "token line must only say yes or no: {line}"
                );
            }
        }
    }

    #[test]
    fn render_shows_ok_wrn_err_markers() {
        let tmp = TempDir::new().unwrap();
        let inputs = fake_inputs(&tmp, true, false, SecretBackend::FileFallback);
        let report = build_report(inputs);
        let rendered = report.render();
        assert!(
            rendered.contains("[OK ]"),
            "should have at least one OK item"
        );
        assert!(
            rendered.contains("[ERR]"),
            "should have at least one ERR item (no token)"
        );
    }

    #[test]
    fn render_includes_log_tail_when_log_exists() {
        let tmp = TempDir::new().unwrap();
        let log_path = tmp.path().join("interlinedlist-sync.log");
        let mut f = std::fs::File::create(&log_path).unwrap();
        for i in 0..15 {
            writeln!(f, "line {i}").unwrap();
        }
        let mut inputs = fake_inputs(&tmp, true, true, SecretBackend::GnomeKeyring);
        inputs.log_file_path = log_path;
        let report = build_report(inputs);
        assert_eq!(report.log_tail.len(), 10, "must show at most 10 lines");
        assert_eq!(report.log_tail[0], "line 5", "must be the last 10 lines");
        assert_eq!(report.log_tail[9], "line 14");
    }

    #[test]
    fn render_log_tail_absent_when_no_log_file() {
        let tmp = TempDir::new().unwrap();
        let inputs = fake_inputs(&tmp, true, true, SecretBackend::GnomeKeyring);
        let report = build_report(inputs);
        assert!(report.log_tail.is_empty());
        let rendered = report.render();
        assert!(rendered.contains("no content or not found"));
    }

    #[test]
    fn is_healthy_true_with_warnings_but_no_missing() {
        // Token present (no Missing) but nothing synced yet and no state DB
        // (Warnings). The daemon is correctly set up, so --status must exit 0.
        let tmp = TempDir::new().unwrap();
        let inputs = fake_inputs(&tmp, true, true, SecretBackend::GnomeKeyring);
        let report = build_report(inputs);
        assert!(
            report.items.iter().any(|i| i.state == CheckState::Warning),
            "fixture should produce at least one Warning",
        );
        assert!(
            report.is_healthy(),
            "warnings alone must not fail the health check",
        );
    }

    #[test]
    fn is_healthy_false_when_token_missing() {
        let tmp = TempDir::new().unwrap();
        let inputs = fake_inputs(&tmp, true, false, SecretBackend::FileFallback);
        let report = build_report(inputs);
        assert!(
            !report.is_healthy(),
            "a Missing token must fail the health check (exit 1)",
        );
    }

    #[test]
    fn secret_backend_display_strings() {
        assert_eq!(
            SecretBackend::GnomeKeyring.as_str(),
            "GNOME Keyring (libsecret)"
        );
        assert!(SecretBackend::FileFallback
            .as_str()
            .contains("file fallback"));
    }

    #[test]
    fn last_sync_time_from_db_returns_none_for_missing_db() {
        let tmp = TempDir::new().unwrap();
        let result = last_sync_time_from_db(&tmp.path().join("no_such.db"));
        assert!(result.is_none());
    }

    #[test]
    fn last_sync_time_from_db_returns_none_for_empty_db() {
        let tmp = TempDir::new().unwrap();
        let db_path = tmp.path().join("state.db");
        // Open/create the real schema via StateStore, which creates the documents table.
        let store = state_store::StateStore::open(&db_path).unwrap();
        drop(store);
        let result = last_sync_time_from_db(&db_path);
        assert!(result.is_none());
    }

    #[test]
    fn last_sync_time_from_db_returns_timestamp_after_sync() {
        let tmp = TempDir::new().unwrap();
        let db_path = tmp.path().join("state.db");
        let store = state_store::StateStore::open(&db_path).unwrap();
        let path = std::path::PathBuf::from("/docs/note.md");
        store.upsert(&path, Some("srv-1"), Some("hash")).unwrap();
        drop(store);
        let result = last_sync_time_from_db(&db_path);
        assert!(result.is_some(), "expected a timestamp after upsert");
    }
}
