use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use chrono::{DateTime, Utc};
use rusqlite::{params, Connection};
use thiserror::Error;
use tracing::{debug, info};

#[derive(Debug, Error)]
pub enum StateStoreError {
    #[error("document not found: {local_path}")]
    NotFound { local_path: PathBuf },

    #[error("database error: {0}")]
    Database(#[from] rusqlite::Error),
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
                     ON documents(server_id);",
            )
            .context("database migration failed")?;
        debug!("state DB schema ready");
        Ok(())
    }

    pub fn upsert(&self, local_path: &Path, server_id: Option<&str>, sha256: Option<&str>) -> Result<DocumentRecord, StateStoreError> {
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

    pub fn lookup_by_server_id(&self, server_id: &str) -> Result<Option<DocumentRecord>, StateStoreError> {
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

    pub fn update_hash(&self, local_path: &Path, sha256: &str, synced_at: DateTime<Utc>) -> Result<(), StateStoreError> {
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
        let record = store.upsert(&path, Some("srv-001"), Some("abc123")).unwrap();
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
        store.upsert(&path, Some("old-id"), Some("old-hash")).unwrap();
        store.upsert(&path, Some("new-id"), Some("new-hash")).unwrap();
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
}
