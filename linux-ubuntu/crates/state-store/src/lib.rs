use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use chrono::{DateTime, Utc};
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};
use thiserror::Error;
use tracing::{debug, info};

#[derive(Debug, Error)]
pub enum StateStoreError {
    #[error("document not found: {local_path}")]
    NotFound { local_path: PathBuf },

    #[error("database error: {0}")]
    Database(#[from] rusqlite::Error),

    #[error("serialization error: {0}")]
    Json(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PendingOp {
    None,
    Upload,
    Download,
    Delete,
}

impl PendingOp {
    fn as_str(&self) -> &'static str {
        match self {
            PendingOp::None => "none",
            PendingOp::Upload => "upload",
            PendingOp::Download => "download",
            PendingOp::Delete => "delete",
        }
    }

    fn from_str(s: &str) -> Self {
        match s {
            "upload" => PendingOp::Upload,
            "download" => PendingOp::Download,
            "delete" => PendingOp::Delete,
            _ => PendingOp::None,
        }
    }
}

#[derive(Debug, Clone)]
pub struct DocumentRecord {
    pub id: i64,
    pub local_path: PathBuf,
    pub server_id: Option<String>,
    pub sha256: Option<String>,
    pub synced_at: Option<DateTime<Utc>>,
    pub pending_op: PendingOp,
}

pub struct StateStore {
    conn: Connection,
}

impl StateStore {
    pub fn open(db_path: &Path) -> Result<Self> {
        if let Some(parent) = db_path.parent() {
            std::fs::create_dir_all(parent)
                .with_context(|| format!("failed to create state dir {}", parent.display()))?;
        }

        let conn = Connection::open(db_path)
            .with_context(|| format!("failed to open state DB at {}", db_path.display()))?;

        let store = Self { conn };
        store.migrate()?;
        info!("state store opened at {}", db_path.display());
        Ok(store)
    }

    pub fn default_path() -> PathBuf {
        dirs::data_dir()
            .unwrap_or_else(|| PathBuf::from("~/.local/share"))
            .join("interlinedlist-sync")
            .join("state.db")
    }

    fn migrate(&self) -> Result<()> {
        self.conn
            .execute_batch(
                "PRAGMA journal_mode=WAL;
                 PRAGMA foreign_keys=ON;
                 CREATE TABLE IF NOT EXISTS documents (
                     id          INTEGER PRIMARY KEY AUTOINCREMENT,
                     local_path  TEXT    NOT NULL UNIQUE,
                     server_id   TEXT,
                     sha256      TEXT,
                     synced_at   TEXT,
                     pending_op  TEXT    NOT NULL DEFAULT 'none'
                 );
                 CREATE INDEX IF NOT EXISTS idx_documents_server_id
                     ON documents(server_id);
                 CREATE TABLE IF NOT EXISTS meta (
                     key   TEXT PRIMARY KEY,
                     value TEXT NOT NULL
                 );
                 CREATE TABLE IF NOT EXISTS pending_ops (
                     id           INTEGER PRIMARY KEY AUTOINCREMENT,
                     op_kind      TEXT    NOT NULL,
                     doc_id       TEXT,
                     payload_json TEXT    NOT NULL,
                     queued_at    INTEGER NOT NULL,
                     retry_count  INTEGER NOT NULL DEFAULT 0,
                     last_error   TEXT
                 );",
            )
            .context("database migration failed")?;
        debug!("state DB schema ready");
        Ok(())
    }

    pub fn upsert(
        &self,
        local_path: &Path,
        server_id: Option<&str>,
        sha256: Option<&str>,
    ) -> Result<DocumentRecord, StateStoreError> {
        let path_str = local_path.to_string_lossy();
        let now = Utc::now().to_rfc3339();

        self.conn.execute(
            "INSERT INTO documents (local_path, server_id, sha256, synced_at, pending_op)
             VALUES (?1, ?2, ?3, ?4, 'none')
             ON CONFLICT(local_path) DO UPDATE SET
                 server_id = excluded.server_id,
                 sha256    = excluded.sha256,
                 synced_at = excluded.synced_at,
                 pending_op = 'none'",
            params![path_str.as_ref(), server_id, sha256, now],
        )?;

        self.lookup(local_path)
    }

    pub fn lookup(&self, local_path: &Path) -> Result<DocumentRecord, StateStoreError> {
        let path_str = local_path.to_string_lossy();
        let result = self.conn.query_row(
            "SELECT id, local_path, server_id, sha256, synced_at, pending_op
             FROM documents WHERE local_path = ?1",
            params![path_str.as_ref()],
            row_to_record,
        );
        match result {
            Ok(record) => Ok(record),
            Err(rusqlite::Error::QueryReturnedNoRows) => Err(StateStoreError::NotFound {
                local_path: local_path.to_path_buf(),
            }),
            Err(e) => Err(StateStoreError::Database(e)),
        }
    }

    pub fn lookup_by_server_id(
        &self,
        server_id: &str,
    ) -> Result<Option<DocumentRecord>, StateStoreError> {
        let result = self.conn.query_row(
            "SELECT id, local_path, server_id, sha256, synced_at, pending_op
             FROM documents WHERE server_id = ?1",
            params![server_id],
            row_to_record,
        );
        match result {
            Ok(record) => Ok(Some(record)),
            Err(rusqlite::Error::QueryReturnedNoRows) => Ok(None),
            Err(e) => Err(StateStoreError::Database(e)),
        }
    }

    pub fn update_hash(
        &self,
        local_path: &Path,
        sha256: &str,
        synced_at: DateTime<Utc>,
    ) -> Result<(), StateStoreError> {
        let path_str = local_path.to_string_lossy();
        self.conn.execute(
            "UPDATE documents SET sha256 = ?1, synced_at = ?2, pending_op = 'none'
             WHERE local_path = ?3",
            params![sha256, synced_at.to_rfc3339(), path_str.as_ref()],
        )?;
        Ok(())
    }

    pub fn set_pending_op(&self, local_path: &Path, op: &PendingOp) -> Result<(), StateStoreError> {
        let path_str = local_path.to_string_lossy();
        self.conn.execute(
            "UPDATE documents SET pending_op = ?1 WHERE local_path = ?2",
            params![op.as_str(), path_str.as_ref()],
        )?;
        Ok(())
    }

    pub fn list_pending(&self) -> Result<Vec<DocumentRecord>, StateStoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT id, local_path, server_id, sha256, synced_at, pending_op
             FROM documents WHERE pending_op != 'none'",
        )?;
        let records = stmt
            .query_map([], row_to_record)?
            .collect::<Result<Vec<_>, _>>()?;
        Ok(records)
    }

    pub fn list_all(&self) -> Result<Vec<DocumentRecord>, StateStoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT id, local_path, server_id, sha256, synced_at, pending_op
             FROM documents",
        )?;
        let records = stmt
            .query_map([], row_to_record)?
            .collect::<Result<Vec<_>, _>>()?;
        Ok(records)
    }

    pub fn delete(&self, local_path: &Path) -> Result<(), StateStoreError> {
        let path_str = local_path.to_string_lossy();
        self.conn.execute(
            "DELETE FROM documents WHERE local_path = ?1",
            params![path_str.as_ref()],
        )?;
        Ok(())
    }

    pub fn get_meta(&self, key: &str) -> Result<Option<String>, StateStoreError> {
        let result = self.conn.query_row(
            "SELECT value FROM meta WHERE key = ?1",
            params![key],
            |row| row.get(0),
        );
        match result {
            Ok(v) => Ok(Some(v)),
            Err(rusqlite::Error::QueryReturnedNoRows) => Ok(None),
            Err(e) => Err(StateStoreError::Database(e)),
        }
    }

    pub fn set_meta(&self, key: &str, value: &str) -> Result<(), StateStoreError> {
        self.conn.execute(
            "INSERT INTO meta (key, value) VALUES (?1, ?2)
             ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            params![key, value],
        )?;
        Ok(())
    }

    pub fn enqueue_op(&self, op: &QueuedOp) -> Result<i64, StateStoreError> {
        let payload =
            serde_json::to_string(&op.payload).map_err(|e| StateStoreError::Json(e.to_string()))?;
        self.conn.execute(
            "INSERT INTO pending_ops (op_kind, doc_id, payload_json, queued_at)
             VALUES (?1, ?2, ?3, ?4)",
            params![
                op.op_kind.as_str(),
                op.doc_id.as_deref(),
                payload,
                op.queued_at.timestamp()
            ],
        )?;
        Ok(self.conn.last_insert_rowid())
    }

    pub fn peek_pending_ops(&self, limit: usize) -> Result<Vec<QueuedOp>, StateStoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT id, op_kind, doc_id, payload_json, queued_at, retry_count, last_error
             FROM pending_ops
             ORDER BY id ASC
             LIMIT ?1",
        )?;
        let ops = stmt
            .query_map(params![limit as i64], row_to_queued_op)?
            .collect::<Result<Vec<_>, _>>()?;
        Ok(ops)
    }

    pub fn mark_op_done(&self, id: i64) -> Result<(), StateStoreError> {
        self.conn
            .execute("DELETE FROM pending_ops WHERE id = ?1", params![id])?;
        Ok(())
    }

    pub fn mark_op_failed(&self, id: i64, error: &str) -> Result<(), StateStoreError> {
        self.conn.execute(
            "UPDATE pending_ops
             SET retry_count = retry_count + 1, last_error = ?1
             WHERE id = ?2",
            params![error, id],
        )?;
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum OpKind {
    Upload,
    Delete,
    Rename,
}

impl OpKind {
    fn as_str(&self) -> &'static str {
        match self {
            OpKind::Upload => "upload",
            OpKind::Delete => "delete",
            OpKind::Rename => "rename",
        }
    }

    fn from_str(s: &str) -> Self {
        match s {
            "delete" => OpKind::Delete,
            "rename" => OpKind::Rename,
            _ => OpKind::Upload,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct QueuedOp {
    pub id: i64,
    pub op_kind: OpKind,
    pub doc_id: Option<String>,
    pub payload: serde_json::Value,
    pub queued_at: DateTime<Utc>,
    pub retry_count: i64,
    pub last_error: Option<String>,
}

impl QueuedOp {
    pub fn new(op_kind: OpKind, doc_id: Option<String>, payload: serde_json::Value) -> Self {
        Self {
            id: 0,
            op_kind,
            doc_id,
            payload,
            queued_at: Utc::now(),
            retry_count: 0,
            last_error: None,
        }
    }
}

fn row_to_queued_op(row: &rusqlite::Row<'_>) -> rusqlite::Result<QueuedOp> {
    let op_kind_str: String = row.get(1)?;
    let payload_str: String = row.get(3)?;
    let queued_at_secs: i64 = row.get(4)?;

    let payload: serde_json::Value =
        serde_json::from_str(&payload_str).unwrap_or(serde_json::Value::Null);

    Ok(QueuedOp {
        id: row.get(0)?,
        op_kind: OpKind::from_str(&op_kind_str),
        doc_id: row.get(2)?,
        payload,
        queued_at: DateTime::from_timestamp(queued_at_secs, 0).unwrap_or_else(Utc::now),
        retry_count: row.get(5)?,
        last_error: row.get(6)?,
    })
}

fn row_to_record(row: &rusqlite::Row<'_>) -> rusqlite::Result<DocumentRecord> {
    let synced_at_str: Option<String> = row.get(4)?;
    let synced_at = synced_at_str
        .as_deref()
        .and_then(|s| DateTime::parse_from_rfc3339(s).ok())
        .map(|dt| dt.with_timezone(&Utc));

    let pending_op_str: String = row.get(5)?;
    let local_path_str: String = row.get(1)?;

    Ok(DocumentRecord {
        id: row.get(0)?,
        local_path: PathBuf::from(local_path_str),
        server_id: row.get(2)?,
        sha256: row.get(3)?,
        synced_at,
        pending_op: PendingOp::from_str(&pending_op_str),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    fn make_store(dir: &TempDir) -> StateStore {
        StateStore::open(&dir.path().join("state.db")).unwrap()
    }

    #[test]
    fn insert_and_lookup() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let path = PathBuf::from("/home/user/docs/note.md");
        let record = store
            .upsert(&path, Some("srv-001"), Some("abc123"))
            .unwrap();
        assert_eq!(record.local_path, path);
        assert_eq!(record.server_id.as_deref(), Some("srv-001"));
        assert_eq!(record.sha256.as_deref(), Some("abc123"));
        assert_eq!(record.pending_op, PendingOp::None);
    }

    #[test]
    fn lookup_missing_returns_not_found() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let result = store.lookup(&PathBuf::from("/no/such/file.md"));
        assert!(matches!(result, Err(StateStoreError::NotFound { .. })));
    }

    #[test]
    fn update_hash_clears_pending_op() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let path = PathBuf::from("/home/user/docs/note.md");
        store.upsert(&path, None, None).unwrap();
        store.set_pending_op(&path, &PendingOp::Upload).unwrap();

        let before = store.lookup(&path).unwrap();
        assert_eq!(before.pending_op, PendingOp::Upload);

        store.update_hash(&path, "newhash", Utc::now()).unwrap();
        let after = store.lookup(&path).unwrap();
        assert_eq!(after.pending_op, PendingOp::None);
        assert_eq!(after.sha256.as_deref(), Some("newhash"));
    }

    #[test]
    fn mark_pending_and_list_pending() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let p1 = PathBuf::from("/docs/a.md");
        let p2 = PathBuf::from("/docs/b.md");
        let p3 = PathBuf::from("/docs/c.md");
        store.upsert(&p1, None, None).unwrap();
        store.upsert(&p2, None, None).unwrap();
        store.upsert(&p3, None, None).unwrap();
        store.set_pending_op(&p1, &PendingOp::Upload).unwrap();
        store.set_pending_op(&p3, &PendingOp::Delete).unwrap();

        let pending = store.list_pending().unwrap();
        assert_eq!(pending.len(), 2);
        let paths: Vec<_> = pending.iter().map(|r| r.local_path.clone()).collect();
        assert!(paths.contains(&p1));
        assert!(paths.contains(&p3));
        assert!(!paths.contains(&p2));
    }

    #[test]
    fn upsert_overwrites_existing() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let path = PathBuf::from("/docs/note.md");
        store
            .upsert(&path, Some("old-id"), Some("old-hash"))
            .unwrap();
        store
            .upsert(&path, Some("new-id"), Some("new-hash"))
            .unwrap();
        let record = store.lookup(&path).unwrap();
        assert_eq!(record.server_id.as_deref(), Some("new-id"));
        assert_eq!(record.sha256.as_deref(), Some("new-hash"));
    }

    #[test]
    fn delete_removes_record() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let path = PathBuf::from("/docs/note.md");
        store.upsert(&path, None, None).unwrap();
        store.delete(&path).unwrap();
        let result = store.lookup(&path);
        assert!(matches!(result, Err(StateStoreError::NotFound { .. })));
    }

    #[test]
    fn lookup_by_server_id() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let path = PathBuf::from("/docs/note.md");
        store.upsert(&path, Some("srv-42"), Some("hash")).unwrap();
        let found = store.lookup_by_server_id("srv-42").unwrap();
        assert!(found.is_some());
        assert_eq!(found.unwrap().local_path, path);
    }

    #[test]
    fn lookup_by_server_id_missing_returns_none() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let found = store.lookup_by_server_id("no-such-id").unwrap();
        assert!(found.is_none());
    }

    #[test]
    fn list_all_returns_all_records() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let p1 = PathBuf::from("/docs/a.md");
        let p2 = PathBuf::from("/docs/b.md");
        store.upsert(&p1, Some("s1"), Some("h1")).unwrap();
        store.upsert(&p2, Some("s2"), Some("h2")).unwrap();

        let all = store.list_all().unwrap();
        assert_eq!(all.len(), 2);
        let paths: Vec<_> = all.iter().map(|r| r.local_path.clone()).collect();
        assert!(paths.contains(&p1));
        assert!(paths.contains(&p2));
    }

    #[test]
    fn set_pending_op_download_then_delete() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let path = PathBuf::from("/docs/change.md");
        store.upsert(&path, Some("srv-99"), None).unwrap();

        store.set_pending_op(&path, &PendingOp::Download).unwrap();
        assert_eq!(store.lookup(&path).unwrap().pending_op, PendingOp::Download);

        store.set_pending_op(&path, &PendingOp::Delete).unwrap();
        assert_eq!(store.lookup(&path).unwrap().pending_op, PendingOp::Delete);

        store.set_pending_op(&path, &PendingOp::None).unwrap();
        assert_eq!(store.lookup(&path).unwrap().pending_op, PendingOp::None);
    }

    #[test]
    fn enqueue_then_peek_returns_op() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);

        let op = QueuedOp::new(
            OpKind::Upload,
            Some("doc-1".to_string()),
            serde_json::json!({"path": "/docs/note.md"}),
        );
        let id = store.enqueue_op(&op).unwrap();
        assert!(id > 0);

        let ops = store.peek_pending_ops(10).unwrap();
        assert_eq!(ops.len(), 1);
        assert_eq!(ops[0].id, id);
        assert_eq!(ops[0].op_kind, OpKind::Upload);
        assert_eq!(ops[0].doc_id.as_deref(), Some("doc-1"));
    }

    #[test]
    fn mark_op_done_removes_it() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);

        let op = QueuedOp::new(
            OpKind::Delete,
            Some("doc-2".to_string()),
            serde_json::json!({}),
        );
        let id = store.enqueue_op(&op).unwrap();

        store.mark_op_done(id).unwrap();

        let ops = store.peek_pending_ops(10).unwrap();
        assert!(ops.is_empty());
    }

    #[test]
    fn mark_op_failed_increments_retry() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);

        let op = QueuedOp::new(OpKind::Upload, None, serde_json::json!({}));
        let id = store.enqueue_op(&op).unwrap();

        store.mark_op_failed(id, "connection refused").unwrap();
        store.mark_op_failed(id, "timeout").unwrap();

        let ops = store.peek_pending_ops(10).unwrap();
        assert_eq!(ops[0].retry_count, 2);
        assert_eq!(ops[0].last_error.as_deref(), Some("timeout"));
    }

    #[test]
    fn peek_respects_limit() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);

        for i in 0..5 {
            let op = QueuedOp::new(
                OpKind::Upload,
                Some(format!("doc-{i}")),
                serde_json::json!({}),
            );
            store.enqueue_op(&op).unwrap();
        }

        let ops = store.peek_pending_ops(3).unwrap();
        assert_eq!(ops.len(), 3);
    }

    #[test]
    fn peek_returns_oldest_first() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);

        let id1 = store
            .enqueue_op(&QueuedOp::new(
                OpKind::Upload,
                Some("a".into()),
                serde_json::json!({}),
            ))
            .unwrap();
        let id2 = store
            .enqueue_op(&QueuedOp::new(
                OpKind::Delete,
                Some("b".into()),
                serde_json::json!({}),
            ))
            .unwrap();

        let ops = store.peek_pending_ops(10).unwrap();
        assert_eq!(ops[0].id, id1);
        assert_eq!(ops[1].id, id2);
    }
}
