use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use anyhow::Result;
use chrono::Utc;
use sha2::{Digest, Sha256};
use thiserror::Error;
use tokio::sync::{mpsc, watch};
use tracing::{debug, error, info, warn};

use api_client::{ApiClientTrait, CreateDocumentRequest, UpdateDocumentRequest};
use config_store::{AppConfig, ConflictResolution};
use file_watcher::FileEvent;
use notifier::{Notification, Notifier};
use state_store::{PendingOp, StateStore};

#[derive(Debug, Error)]
pub enum SyncError {
    #[error("I/O error on {path}: {reason}")]
    Io { path: PathBuf, reason: String },

    #[error("API error: {0}")]
    Api(#[from] api_client::ApiError),

    #[error("state store error: {0}")]
    State(#[from] state_store::StateStoreError),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SyncStatus {
    Idle,
    Syncing,
    Paused,
    Error(String),
}

pub struct SyncEngine {
    config: AppConfig,
    account: String,
    api: Arc<dyn ApiClientTrait>,
    state: Arc<StateStore>,
    notifier: Arc<dyn Notifier>,
    file_events: mpsc::Receiver<FileEvent>,
    status_tx: watch::Sender<SyncStatus>,
    status_rx: watch::Receiver<SyncStatus>,
    paused: Arc<tokio::sync::RwLock<bool>>,
}

impl SyncEngine {
    pub fn new(
        config: AppConfig,
        account: impl Into<String>,
        api: Arc<dyn ApiClientTrait>,
        state: Arc<StateStore>,
        notifier: Arc<dyn Notifier>,
        file_events: mpsc::Receiver<FileEvent>,
    ) -> Self {
        let (status_tx, status_rx) = watch::channel(SyncStatus::Idle);
        Self {
            config,
            account: account.into(),
            api,
            state,
            notifier,
            file_events,
            status_tx,
            status_rx,
            paused: Arc::new(tokio::sync::RwLock::new(false)),
        }
    }

    pub fn status_receiver(&self) -> watch::Receiver<SyncStatus> {
        self.status_rx.clone()
    }

    /// Run the sync engine event loop.
    ///
    /// `sync_now_rx` receives a `()` whenever the tray "Sync Now" action fires.
    /// Each received signal triggers an immediate remote poll in addition to the
    /// normal periodic ticker, so the two paths share the same poll logic.
    pub async fn run(mut self, mut sync_now_rx: mpsc::Receiver<()>) -> Result<()> {
        let poll_interval = Duration::from_secs(self.config.sync.interval_seconds);
        let mut poll_ticker = tokio::time::interval(poll_interval);
        poll_ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);

        info!("sync engine started for account {}", self.account);

        loop {
            tokio::select! {
                Some(event) = self.file_events.recv() => {
                    if *self.paused.read().await {
                        debug!("sync paused, ignoring file event: {event:?}");
                        continue;
                    }
                    if let Err(e) = self.handle_file_event(event).await {
                        error!("file event handling failed: {e}");
                        self.set_status(SyncStatus::Error(e.to_string())).await;
                    }
                }
                _ = poll_ticker.tick() => {
                    if *self.paused.read().await {
                        continue;
                    }
                    if let Err(e) = self.poll_remote().await {
                        error!("remote poll failed: {e}");
                        self.set_status(SyncStatus::Error(e.to_string())).await;
                    }
                }
                Some(()) = sync_now_rx.recv() => {
                    if *self.paused.read().await {
                        debug!("sync paused, ignoring manual sync-now request");
                        continue;
                    }
                    info!("manual sync triggered from tray");
                    if let Err(e) = self.poll_remote().await {
                        error!("manual poll failed: {e}");
                        self.set_status(SyncStatus::Error(e.to_string())).await;
                    }
                }
            }
        }
    }

    async fn set_status(&self, status: SyncStatus) {
        let _ = self.status_tx.send(status);
    }

    async fn handle_file_event(&self, event: FileEvent) -> Result<(), SyncError> {
        match event {
            FileEvent::Changed(path) | FileEvent::Created(path) => {
                self.upload_if_changed(&path).await
            }
            FileEvent::Deleted(path) => self.delete_remote(&path).await,
        }
    }

    pub async fn upload_if_changed(&self, path: &Path) -> Result<(), SyncError> {
        let content = std::fs::read_to_string(path).map_err(|e| SyncError::Io {
            path: path.to_path_buf(),
            reason: e.to_string(),
        })?;

        let hash = sha256_hex(content.as_bytes());

        match self.state.lookup(path) {
            Ok(record) if record.sha256.as_deref() == Some(hash.as_str()) => {
                debug!("hash unchanged for {}, skipping upload", path.display());
                return Ok(());
            }
            _ => {}
        }

        self.set_status(SyncStatus::Syncing).await;
        debug!("uploading {}", path.display());

        let title = path
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or("Untitled")
            .to_string();

        let result = match self.state.lookup(path) {
            Ok(record) if record.server_id.is_some() => {
                let server_id = record.server_id.unwrap();
                self.api
                    .update_document(
                        &self.account,
                        &server_id,
                        UpdateDocumentRequest {
                            title: Some(title),
                            content: content.clone(),
                        },
                    )
                    .await
            }
            _ => {
                self.api
                    .create_document(
                        &self.account,
                        CreateDocumentRequest {
                            title,
                            content: content.clone(),
                        },
                    )
                    .await
            }
        };

        match result {
            Ok(doc) => {
                self.state.upsert(path, Some(&doc.id), Some(&hash))?;
                info!("uploaded {}", path.display());
                if self.config.notifications.show_success {
                    let _ = self
                        .notifier
                        .notify(Notification::low("Synced 1 document"))
                        .await;
                }
                self.set_status(SyncStatus::Idle).await;
                Ok(())
            }
            Err(e) => {
                // Ensure a record exists before setting pending_op.
                let _ = self.state.upsert(path, None, Some(&hash));
                self.state.set_pending_op(path, &PendingOp::Upload)?;
                Err(SyncError::Api(e))
            }
        }
    }

    async fn delete_remote(&self, path: &Path) -> Result<(), SyncError> {
        let record = match self.state.lookup(path) {
            Ok(r) => r,
            Err(state_store::StateStoreError::NotFound { .. }) => return Ok(()),
            Err(e) => return Err(SyncError::State(e)),
        };

        if let Some(server_id) = record.server_id {
            self.set_status(SyncStatus::Syncing).await;
            self.api.delete_document(&self.account, &server_id).await?;
        }
        self.state.delete(path)?;
        self.set_status(SyncStatus::Idle).await;
        Ok(())
    }

    pub async fn poll_remote(&self) -> Result<(), SyncError> {
        self.set_status(SyncStatus::Syncing).await;
        debug!("polling remote for account {}", self.account);

        let remote_docs = self.api.list_documents(&self.account).await?;
        let mut downloaded = 0usize;

        for summary in &remote_docs {
            let local_record = self.state.lookup_by_server_id(&summary.id)?;

            let should_download = match &local_record {
                None => true,
                Some(record) => {
                    // Download if remote hash differs from our last-synced hash.
                    summary.sha256.as_deref() != record.sha256.as_deref()
                        && summary.updated_at
                            > record.synced_at.unwrap_or_else(|| {
                                chrono::TimeZone::timestamp_opt(&Utc, 0, 0).unwrap()
                            })
                }
            };

            if should_download {
                let doc = self.api.get_document(&self.account, &summary.id).await?;
                let local_path = self.resolve_local_path(&doc.title);
                self.write_document_atomic(&local_path, &doc.content, &summary.id)
                    .await?;
                downloaded += 1;
            }
        }

        if downloaded > 0 && self.config.notifications.show_success {
            let _ = self
                .notifier
                .notify(Notification::low(format!(
                    "Synced {downloaded} document(s)"
                )))
                .await;
        }

        self.set_status(SyncStatus::Idle).await;
        Ok(())
    }

    async fn write_document_atomic(
        &self,
        path: &Path,
        content: &str,
        server_id: &str,
    ) -> Result<(), SyncError> {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| SyncError::Io {
                path: parent.to_path_buf(),
                reason: e.to_string(),
            })?;
        }

        let existing_hash = self.state.lookup(path).ok().and_then(|r| r.sha256);
        let incoming_hash = sha256_hex(content.as_bytes());

        if existing_hash.as_deref() == Some(incoming_hash.as_str()) {
            debug!(
                "remote content unchanged for {}, skipping write",
                path.display()
            );
            return Ok(());
        }

        // Conflict: local file exists and has different content than what we last synced.
        if path.exists() {
            let local_content = std::fs::read_to_string(path).map_err(|e| SyncError::Io {
                path: path.to_path_buf(),
                reason: e.to_string(),
            })?;
            let local_hash = sha256_hex(local_content.as_bytes());

            let has_local_unsaved_change = existing_hash
                .as_deref()
                .map(|h| h != local_hash.as_str())
                .unwrap_or(true);

            if has_local_unsaved_change {
                match self.config.sync.conflict_resolution {
                    ConflictResolution::RemoteWins => {
                        warn!("conflict on {} — remote wins", path.display());
                        if self.config.notifications.show_conflicts {
                            let _ = self
                                .notifier
                                .notify(Notification::normal(format!(
                                    "Conflict in {} — remote version kept",
                                    path.file_name()
                                        .and_then(|n| n.to_str())
                                        .unwrap_or("document")
                                )))
                                .await;
                        }
                    }
                    ConflictResolution::ConflictCopy => {
                        let ts = Utc::now().format("%Y%m%d-%H%M%S");
                        let stem = path
                            .file_stem()
                            .and_then(|s| s.to_str())
                            .unwrap_or("document");
                        let conflict_name = format!("{stem}.conflict-{ts}.md");
                        let conflict_path = path.with_file_name(conflict_name);
                        std::fs::copy(path, &conflict_path).map_err(|e| SyncError::Io {
                            path: conflict_path.clone(),
                            reason: e.to_string(),
                        })?;
                        info!("conflict copy created at {}", conflict_path.display());
                        if self.config.notifications.show_conflicts {
                            let _ = self
                                .notifier
                                .notify(Notification::normal(format!(
                                    "Conflict in {} — conflict copy created",
                                    path.file_name()
                                        .and_then(|n| n.to_str())
                                        .unwrap_or("document")
                                )))
                                .await;
                        }
                    }
                }
            }
        }

        // Atomic write: temp file → rename.
        let tmp_path = path.with_extension("md.tmp");
        std::fs::write(&tmp_path, content).map_err(|e| SyncError::Io {
            path: tmp_path.clone(),
            reason: e.to_string(),
        })?;
        std::fs::rename(&tmp_path, path).map_err(|e| SyncError::Io {
            path: path.to_path_buf(),
            reason: e.to_string(),
        })?;

        self.state
            .upsert(path, Some(server_id), Some(&incoming_hash))?;
        debug!("wrote remote doc to {}", path.display());
        Ok(())
    }

    fn resolve_local_path(&self, title: &str) -> PathBuf {
        let watched = self
            .config
            .sync
            .watched_dirs
            .first()
            .map(String::as_str)
            .unwrap_or("~/Documents/InterlinedList");
        let expanded = expand_tilde(watched);
        let safe_name: String = title
            .chars()
            .map(|c| {
                if c.is_alphanumeric() || c == ' ' || c == '-' || c == '_' {
                    c
                } else {
                    '_'
                }
            })
            .collect();
        expanded.join(format!("{safe_name}.md"))
    }
}

fn sha256_hex(data: &[u8]) -> String {
    let mut hasher = Sha256::new();
    hasher.update(data);
    hex::encode(hasher.finalize())
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    use api_client::mock::MockApiClient;
    use notifier::StubNotifier;
    use tempfile::TempDir;

    // StateStore holds a rusqlite Connection (RefCell) which is !Sync. Arc is
    // used here deliberately: tests run on a single thread and never share the
    // store across thread boundaries.
    #[allow(clippy::arc_with_non_send_sync)]
    fn make_state(dir: &TempDir) -> Arc<StateStore> {
        Arc::new(StateStore::open(&dir.path().join("state.db")).unwrap())
    }

    fn make_config(watched_dir: &Path) -> AppConfig {
        let mut cfg = config_store::AppConfig::default();
        cfg.sync.watched_dirs = vec![watched_dir.to_string_lossy().to_string()];
        cfg.sync.interval_seconds = 3600; // don't trigger poll during tests
        cfg
    }

    fn make_engine(
        config: AppConfig,
        api: Arc<MockApiClient>,
        state: Arc<StateStore>,
        rx: mpsc::Receiver<FileEvent>,
    ) -> SyncEngine {
        SyncEngine::new(
            config,
            "test@example.com",
            api,
            state,
            Arc::new(StubNotifier),
            rx,
        )
    }

    #[tokio::test]
    async fn upload_triggered_on_file_changed() {
        let dir = TempDir::new().unwrap();
        let md_file = dir.path().join("note.md");
        std::fs::write(&md_file, "# Hello").unwrap();

        let api = Arc::new(MockApiClient::new());
        let state = make_state(&dir);
        let (_tx, rx) = mpsc::channel(8);
        let engine = make_engine(make_config(dir.path()), api.clone(), state.clone(), rx);

        engine.upload_if_changed(&md_file).await.unwrap();

        let docs = api.list_documents("test@example.com").await.unwrap();
        assert_eq!(docs.len(), 1);
        assert_eq!(docs[0].title, "note");
    }

    #[tokio::test]
    async fn no_upload_when_hash_matches() {
        let dir = TempDir::new().unwrap();
        let md_file = dir.path().join("note.md");
        std::fs::write(&md_file, "# Hello").unwrap();

        let api = Arc::new(MockApiClient::new());
        let state = make_state(&dir);
        let (_, rx) = mpsc::channel(8);
        let engine = make_engine(make_config(dir.path()), api.clone(), state.clone(), rx);

        // First upload
        engine.upload_if_changed(&md_file).await.unwrap();
        let count_after_first = api.list_documents("test@example.com").await.unwrap().len();

        // Second call with unchanged file — should be a no-op.
        engine.upload_if_changed(&md_file).await.unwrap();
        let count_after_second = api.list_documents("test@example.com").await.unwrap().len();

        assert_eq!(count_after_first, count_after_second);
    }

    #[tokio::test]
    async fn conflict_copy_created_when_configured() {
        let dir = TempDir::new().unwrap();
        let watch_dir = dir.path().to_path_buf();

        let mut config = make_config(&watch_dir);
        config.sync.conflict_resolution = ConflictResolution::ConflictCopy;

        let api = Arc::new(MockApiClient::new());
        let state = make_state(&dir);
        let (_, rx) = mpsc::channel(8);
        let engine = make_engine(config, api.clone(), state.clone(), rx);

        // Seed state so the engine believes it last synced "old-hash".
        let local_path = watch_dir.join("report.md");
        std::fs::write(&local_path, "# local unsaved change").unwrap();
        state
            .upsert(&local_path, Some("srv-1"), Some("old-hash"))
            .unwrap();

        // Remote sends different content.
        engine
            .write_document_atomic(&local_path, "# remote content", "srv-1")
            .await
            .unwrap();

        // A conflict copy should exist.
        let entries: Vec<_> = std::fs::read_dir(&watch_dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| e.file_name().to_string_lossy().contains("conflict"))
            .collect();
        assert_eq!(entries.len(), 1, "expected exactly one conflict copy");
    }

    #[tokio::test]
    async fn remote_wins_no_conflict_copy() {
        let dir = TempDir::new().unwrap();
        let watch_dir = dir.path().to_path_buf();

        let mut config = make_config(&watch_dir);
        config.sync.conflict_resolution = ConflictResolution::RemoteWins;

        let api = Arc::new(MockApiClient::new());
        let state = make_state(&dir);
        let (_, rx) = mpsc::channel(8);
        let engine = make_engine(config, api.clone(), state.clone(), rx);

        let local_path = watch_dir.join("report.md");
        std::fs::write(&local_path, "# local unsaved change").unwrap();
        state
            .upsert(&local_path, Some("srv-1"), Some("old-hash"))
            .unwrap();

        engine
            .write_document_atomic(&local_path, "# remote content", "srv-1")
            .await
            .unwrap();

        let entries: Vec<_> = std::fs::read_dir(&watch_dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| e.file_name().to_string_lossy().contains("conflict"))
            .collect();
        assert!(
            entries.is_empty(),
            "remote-wins should not create a conflict copy"
        );

        let content = std::fs::read_to_string(&local_path).unwrap();
        assert_eq!(content, "# remote content");
    }

    #[tokio::test]
    async fn pending_op_set_on_api_failure() {
        let dir = TempDir::new().unwrap();
        let md_file = dir.path().join("note.md");
        std::fs::write(&md_file, "# Hello").unwrap();

        let api = Arc::new(MockApiClient::new());
        api.fail_once(api_client::ApiError::Server {
            status: 503,
            body: "unavailable".into(),
        });

        let state = make_state(&dir);
        let (_, rx) = mpsc::channel(8);
        let engine = make_engine(make_config(dir.path()), api, state.clone(), rx);

        // The upload will fail with a server error and should record a pending op.
        let result = engine.upload_if_changed(&md_file).await;
        assert!(result.is_err());

        let record = state.lookup(&md_file).unwrap();
        assert_eq!(record.pending_op, PendingOp::Upload);
    }
}
