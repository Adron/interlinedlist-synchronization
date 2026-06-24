use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use anyhow::Result;
use async_trait::async_trait;
use chrono::Utc;
use sha2::{Digest, Sha256};
use thiserror::Error;
use tokio::sync::{mpsc, watch, Notify};
use tracing::{debug, error, info, warn};

use api_client::{ApiClientTrait, CreateDocumentRequest, DocumentDelta, UpdateDocumentRequest};
use config_store::{AppConfig, ConflictResolution};
use file_watcher::FileEvent;
use notifier::{Notification, Notifier};
use state_store::{OpKind, PendingOp, QueuedOp, StateStore};

const QUEUE_DRAIN_BATCH: usize = 50;

#[async_trait]
pub trait NetworkMonitor: Send + Sync {
    async fn wait_for_reconnect(&self);
}

pub struct StubNetworkMonitor {
    pub reconnect_signal: Arc<Notify>,
}

#[async_trait]
impl NetworkMonitor for StubNetworkMonitor {
    async fn wait_for_reconnect(&self) {
        self.reconnect_signal.notified().await;
    }
}

impl Default for StubNetworkMonitor {
    fn default() -> Self {
        Self {
            reconnect_signal: Arc::new(Notify::new()),
        }
    }
}

#[cfg(all(target_os = "linux", feature = "network-monitor"))]
pub mod nm_monitor;
#[cfg(all(target_os = "linux", feature = "network-monitor"))]
pub use nm_monitor::NetworkManagerMonitor;

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
    network_monitor: Arc<dyn NetworkMonitor>,
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
        Self::with_network_monitor(
            config,
            account,
            api,
            state,
            notifier,
            file_events,
            Arc::new(StubNetworkMonitor::default()),
        )
    }

    pub fn with_network_monitor(
        config: AppConfig,
        account: impl Into<String>,
        api: Arc<dyn ApiClientTrait>,
        state: Arc<StateStore>,
        notifier: Arc<dyn Notifier>,
        file_events: mpsc::Receiver<FileEvent>,
        network_monitor: Arc<dyn NetworkMonitor>,
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
            network_monitor,
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

        // Drain any ops queued while offline before processing new events.
        self.drain_queue().await;

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
                    self.drain_queue().await;
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
                    self.drain_queue().await;
                    if let Err(e) = self.poll_remote().await {
                        error!("manual poll failed: {e}");
                        self.set_status(SyncStatus::Error(e.to_string())).await;
                    }
                }
                _ = self.network_monitor.wait_for_reconnect() => {
                    info!("network reconnected — draining offline queue");
                    self.drain_queue().await;
                }
            }
        }
    }

    async fn drain_queue(&self) {
        let ops = match self.state.peek_pending_ops(QUEUE_DRAIN_BATCH) {
            Ok(ops) => ops,
            Err(e) => {
                error!("failed to read offline queue: {e}");
                return;
            }
        };

        for op in ops {
            let result = self.replay_queued_op(&op).await;
            match result {
                Ok(()) => {
                    if let Err(e) = self.state.mark_op_done(op.id) {
                        error!("failed to mark queued op {} done: {e}", op.id);
                    } else {
                        debug!("drained queued op {} ({:?})", op.id, op.op_kind);
                    }
                }
                Err(e) => {
                    warn!("queued op {} failed (retry {}): {e}", op.id, op.retry_count);
                    let _ = self.state.mark_op_failed(op.id, &e.to_string());
                }
            }
        }
    }

    async fn replay_queued_op(&self, op: &QueuedOp) -> Result<(), SyncError> {
        match op.op_kind {
            OpKind::Upload => {
                let path_str = op
                    .payload
                    .get("path")
                    .and_then(|v| v.as_str())
                    .unwrap_or("");
                let path = PathBuf::from(path_str);
                self.upload_if_changed(&path).await
            }
            OpKind::Delete => {
                let path_str = op
                    .payload
                    .get("path")
                    .and_then(|v| v.as_str())
                    .unwrap_or("");
                self.delete_remote(&PathBuf::from(path_str)).await
            }
            OpKind::Rename => {
                let path_str = op
                    .payload
                    .get("new_path")
                    .and_then(|v| v.as_str())
                    .unwrap_or("");
                self.upload_if_changed(&PathBuf::from(path_str)).await
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
                let _ = self.state.upsert(path, None, Some(&hash));
                self.state.set_pending_op(path, &PendingOp::Upload)?;
                let queued_op = QueuedOp::new(
                    OpKind::Upload,
                    None,
                    serde_json::json!({"path": path.to_string_lossy()}),
                );
                if let Err(qe) = self.state.enqueue_op(&queued_op) {
                    error!("failed to enqueue offline upload op: {qe}");
                }
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

        const META_LAST_SYNC: &str = "last_delta_synced_at";

        let since: Option<chrono::DateTime<Utc>> = self
            .state
            .get_meta(META_LAST_SYNC)
            .ok()
            .flatten()
            .and_then(|s| chrono::DateTime::parse_from_rfc3339(&s).ok())
            .map(|dt| dt.with_timezone(&Utc));

        let mut downloaded = 0usize;

        if let Some(since_ts) = since {
            let delta = self.api.fetch_delta(&self.account, Some(since_ts)).await?;
            for entry in &delta.documents {
                self.apply_delta_entry(entry).await?;
                if !entry.deleted() {
                    downloaded += 1;
                }
            }
            self.state
                .set_meta(META_LAST_SYNC, &delta.synced_at.to_rfc3339())?;
        } else {
            let remote_docs = self.api.list_documents(&self.account).await?;
            for summary in &remote_docs {
                let local_record = self.state.lookup_by_server_id(&summary.id)?;

                let should_download = match &local_record {
                    None => true,
                    Some(record) => {
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

            let delta = self.api.fetch_delta(&self.account, None).await?;
            self.state
                .set_meta(META_LAST_SYNC, &delta.synced_at.to_rfc3339())?;
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

    async fn apply_delta_entry(&self, entry: &DocumentDelta) -> Result<(), SyncError> {
        if entry.deleted() {
            if let Ok(Some(record)) = self.state.lookup_by_server_id(&entry.id) {
                if record.local_path.exists() {
                    std::fs::remove_file(&record.local_path).map_err(|e| SyncError::Io {
                        path: record.local_path.clone(),
                        reason: e.to_string(),
                    })?;
                }
                self.state.delete(&record.local_path)?;
                info!("removed tombstoned document {}", entry.id);
            }
            return Ok(());
        }

        if let Some(content) = &entry.content {
            let local_path = self.resolve_local_path(&entry.title);
            self.write_document_atomic(&local_path, content, &entry.id)
                .await?;
        }
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
                    ConflictResolution::LocalWins => {
                        warn!(
                            "conflict on {} — local wins, skipping remote write",
                            path.display()
                        );
                        if self.config.notifications.show_conflicts {
                            let _ = self
                                .notifier
                                .notify(Notification::normal(format!(
                                    "Conflict in {} — local version kept",
                                    path.file_name()
                                        .and_then(|n| n.to_str())
                                        .unwrap_or("document")
                                )))
                                .await;
                        }
                        return Ok(());
                    }
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
    async fn failed_push_enqueues_op() {
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

        let _ = engine.upload_if_changed(&md_file).await;

        let queued = state.peek_pending_ops(10).unwrap();
        assert_eq!(queued.len(), 1);
        assert_eq!(queued[0].op_kind, state_store::OpKind::Upload);
    }

    #[tokio::test(flavor = "current_thread")]
    async fn drain_replays_pending_uploads() {
        let local = tokio::task::LocalSet::new();
        local
            .run_until(async {
                let dir = TempDir::new().unwrap();
                let watch_dir = dir.path().to_path_buf();
                let md_file = watch_dir.join("queued.md");
                std::fs::write(&md_file, "# Queued").unwrap();

                let api = Arc::new(MockApiClient::new());
                let state = make_state(&dir);

                let queued_op = state_store::QueuedOp::new(
                    state_store::OpKind::Upload,
                    None,
                    serde_json::json!({"path": md_file.to_string_lossy()}),
                );
                state.enqueue_op(&queued_op).unwrap();

                let (_, rx) = mpsc::channel(8);
                let engine = make_engine(make_config(&watch_dir), api.clone(), state.clone(), rx);

                engine.drain_queue().await;

                let docs = api.list_documents("test@example.com").await.unwrap();
                assert_eq!(docs.len(), 1, "queued upload should have been replayed");

                let remaining = state.peek_pending_ops(10).unwrap();
                assert!(
                    remaining.is_empty(),
                    "successful replay should remove op from queue"
                );
            })
            .await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn network_reconnect_triggers_drain() {
        let local = tokio::task::LocalSet::new();
        local
            .run_until(async {
                let dir = TempDir::new().unwrap();
                let watch_dir = dir.path().to_path_buf();
                let md_file = watch_dir.join("offline.md");
                std::fs::write(&md_file, "# Offline doc").unwrap();

                let api = Arc::new(MockApiClient::new());
                let state = make_state(&dir);

                let queued_op = state_store::QueuedOp::new(
                    state_store::OpKind::Upload,
                    None,
                    serde_json::json!({"path": md_file.to_string_lossy()}),
                );
                state.enqueue_op(&queued_op).unwrap();

                let reconnect_signal = Arc::new(tokio::sync::Notify::new());
                let monitor = Arc::new(StubNetworkMonitor {
                    reconnect_signal: reconnect_signal.clone(),
                });

                let (_, rx) = mpsc::channel(8);
                let (sync_now_tx, sync_now_rx) = mpsc::channel::<()>(4);
                let engine = SyncEngine::with_network_monitor(
                    make_config(&watch_dir),
                    "test@example.com",
                    api.clone(),
                    state.clone(),
                    Arc::new(StubNotifier),
                    rx,
                    monitor,
                );
                let status_rx = engine.status_receiver();

                let engine_handle = tokio::task::spawn_local(async move {
                    let _ = engine.run(sync_now_rx).await;
                });

                reconnect_signal.notify_one();

                let mut rx = status_rx;
                tokio::time::timeout(std::time::Duration::from_secs(5), async {
                    loop {
                        rx.changed().await.unwrap();
                        if *rx.borrow() == SyncStatus::Idle {
                            break;
                        }
                    }
                })
                .await
                .expect("engine did not reach Idle within 5 s");

                engine_handle.abort();
                drop(sync_now_tx);

                let docs = api.list_documents("test@example.com").await.unwrap();
                assert_eq!(
                    docs.len(),
                    1,
                    "reconnect should have triggered queue drain and upload"
                );
            })
            .await;
    }

    #[tokio::test]
    async fn local_wins_keeps_local_file_unchanged() {
        let dir = TempDir::new().unwrap();
        let watch_dir = dir.path().to_path_buf();

        let mut config = make_config(&watch_dir);
        config.sync.conflict_resolution = ConflictResolution::LocalWins;

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

        let content = std::fs::read_to_string(&local_path).unwrap();
        assert_eq!(
            content, "# local unsaved change",
            "local-wins must not overwrite local file"
        );

        let entries: Vec<_> = std::fs::read_dir(&watch_dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| e.file_name().to_string_lossy().contains("conflict"))
            .collect();
        assert!(
            entries.is_empty(),
            "local-wins must not create a conflict copy"
        );
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

        let result = engine.upload_if_changed(&md_file).await;
        assert!(result.is_err());

        let record = state.lookup(&md_file).unwrap();
        assert_eq!(record.pending_op, PendingOp::Upload);
    }

    #[tokio::test]
    async fn sync_applies_delta_tombstones() {
        let dir = TempDir::new().unwrap();
        let watch_dir = dir.path().to_path_buf();

        let api = Arc::new(MockApiClient::new());
        let state = make_state(&dir);
        let (_, rx) = mpsc::channel(8);
        let engine = make_engine(make_config(&watch_dir), api.clone(), state.clone(), rx);

        let local_path = watch_dir.join("to-delete.md");
        std::fs::write(&local_path, "# Going away").unwrap();
        state
            .upsert(&local_path, Some("srv-del"), Some("hash-del"))
            .unwrap();

        let delta = api_client::DeltaResponse {
            synced_at: Utc::now(),
            documents: vec![api_client::DocumentDelta {
                id: "srv-del".to_string(),
                title: "to-delete".to_string(),
                content: None,
                folder_id: None,
                updated_at: Utc::now(),
                deleted_at: Some(Utc::now()),
            }],
        };

        engine.apply_delta_entry(&delta.documents[0]).await.unwrap();

        assert!(!local_path.exists(), "tombstoned file should be removed");
        assert!(
            matches!(
                state.lookup(&local_path),
                Err(state_store::StateStoreError::NotFound { .. })
            ),
            "state row should be deleted"
        );
    }

    #[tokio::test]
    async fn sync_uses_full_list_on_first_sync() {
        let dir = TempDir::new().unwrap();
        let watch_dir = dir.path().to_path_buf();

        let api = Arc::new(MockApiClient::new());
        api.create_document(
            "test@example.com",
            api_client::CreateDocumentRequest {
                title: "first-sync-doc".to_string(),
                content: "# First".to_string(),
            },
        )
        .await
        .unwrap();

        let state = make_state(&dir);
        let (_, rx) = mpsc::channel(8);
        let engine = make_engine(make_config(&watch_dir), api.clone(), state.clone(), rx);

        assert!(state.get_meta("last_delta_synced_at").unwrap().is_none());

        engine.poll_remote().await.unwrap();

        let expected = watch_dir.join("first-sync-doc.md");
        assert!(
            expected.exists(),
            "document should be written on first sync"
        );
        assert!(
            state.get_meta("last_delta_synced_at").unwrap().is_some(),
            "synced_at should be persisted after first sync"
        );
    }

    #[tokio::test]
    async fn sync_persists_synced_at_from_delta() {
        let dir = TempDir::new().unwrap();
        let watch_dir = dir.path().to_path_buf();

        let api = Arc::new(MockApiClient::new());
        let state = make_state(&dir);

        let fixed_time: chrono::DateTime<Utc> = "2026-05-01T00:00:00Z".parse().unwrap();
        state
            .set_meta("last_delta_synced_at", &fixed_time.to_rfc3339())
            .unwrap();

        let new_sync_time: chrono::DateTime<Utc> = "2026-06-15T10:30:00Z".parse().unwrap();
        api.set_delta_response(api_client::DeltaResponse {
            synced_at: new_sync_time,
            documents: vec![],
        });

        let (_, rx) = mpsc::channel(8);
        let engine = make_engine(make_config(&watch_dir), api.clone(), state.clone(), rx);

        engine.poll_remote().await.unwrap();

        let stored = state
            .get_meta("last_delta_synced_at")
            .unwrap()
            .expect("synced_at must be stored");
        let stored_dt: chrono::DateTime<Utc> = chrono::DateTime::parse_from_rfc3339(&stored)
            .unwrap()
            .with_timezone(&Utc);
        assert_eq!(stored_dt, new_sync_time);
    }

    /// Verify that sending on `sync_now_tx` causes `SyncEngine::run()` to
    /// execute a remote poll cycle. We seed the mock API with one document and
    /// assert it arrives on disk after the manual-sync signal fires.
    ///
    /// `StateStore` contains a `RefCell` and is `!Sync`, so the engine must run
    /// on a `LocalSet` — matching the daemon's `LocalSet` usage in `main.rs`.
    #[tokio::test(flavor = "current_thread")]
    async fn sync_now_triggers_remote_poll() {
        let local = tokio::task::LocalSet::new();
        local
            .run_until(async {
                let dir = TempDir::new().unwrap();
                let watch_dir = dir.path().to_path_buf();

                let api = Arc::new(MockApiClient::new());
                let state = make_state(&dir);
                let (_file_tx, file_rx) = mpsc::channel(8);
                let engine =
                    make_engine(make_config(&watch_dir), api.clone(), state.clone(), file_rx);

                // Seed a remote document before the engine starts.
                api.create_document(
                    "test@example.com",
                    api_client::CreateDocumentRequest {
                        title: "remote-note".to_string(),
                        content: "# From Server".to_string(),
                    },
                )
                .await
                .unwrap();

                let (sync_now_tx, sync_now_rx) = mpsc::channel::<()>(4);
                let status_rx = engine.status_receiver();

                let engine_handle = tokio::task::spawn_local(async move {
                    let _ = engine.run(sync_now_rx).await;
                });

                sync_now_tx.send(()).await.unwrap();

                // Wait until the engine transitions through Syncing back to Idle.
                let mut rx = status_rx;
                tokio::time::timeout(std::time::Duration::from_secs(5), async {
                    loop {
                        rx.changed().await.unwrap();
                        if *rx.borrow() == SyncStatus::Idle {
                            break;
                        }
                    }
                })
                .await
                .expect("engine did not reach Idle within 5 s");

                engine_handle.abort();

                let expected = watch_dir.join("remote-note.md");
                assert!(
                    expected.exists(),
                    "expected synced file at {}",
                    expected.display()
                );
                let content = std::fs::read_to_string(&expected).unwrap();
                assert_eq!(content, "# From Server");
            })
            .await;
    }
}
