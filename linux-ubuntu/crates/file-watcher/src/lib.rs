use std::path::{Path, PathBuf};
use std::time::Duration;

use anyhow::{Context, Result};
use notify::RecursiveMode;
use notify_debouncer_full::{new_debouncer, DebounceEventResult};
use thiserror::Error;
use tokio::sync::mpsc;
use tracing::{debug, info, warn};

pub const DEBOUNCE_DURATION: Duration = Duration::from_millis(500);

#[derive(Debug, Error)]
pub enum WatcherError {
    #[error("failed to watch path {path}: {reason}")]
    Watch { path: PathBuf, reason: String },

    #[error("watcher setup failed: {0}")]
    Setup(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FileEvent {
    Changed(PathBuf),
    Deleted(PathBuf),
    Created(PathBuf),
}

impl FileEvent {
    pub fn path(&self) -> &Path {
        match self {
            FileEvent::Changed(p) | FileEvent::Deleted(p) | FileEvent::Created(p) => p,
        }
    }
}

pub struct FileWatcher {
    // The debouncer must be kept alive for the lifetime of the watcher.
    // Dropping it stops the underlying inotify watches.
    debouncer: notify_debouncer_full::Debouncer<
        notify::RecommendedWatcher,
        notify_debouncer_full::FileIdMap,
    >,
    watched_paths: std::sync::Mutex<Vec<PathBuf>>,
}

impl FileWatcher {
    pub fn new(buffer: usize) -> Result<(Self, mpsc::Receiver<FileEvent>)> {
        let (tx, receiver) = mpsc::channel(buffer);

        let debouncer = new_debouncer(DEBOUNCE_DURATION, None, move |result: DebounceEventResult| {
            match result {
                Ok(events) => {
                    for event in events {
                        let paths = event.event.paths;
                        let kind = event.event.kind;

                        for path in paths {
                            if !is_markdown(&path) {
                                continue;
                            }

                            let file_event = classify_event(&kind, path);
                            if let Some(fe) = file_event {
                                debug!("file event: {fe:?}");
                                if tx.blocking_send(fe).is_err() {
                                    // Receiver dropped; the watcher loop is shutting down.
                                    return;
                                }
                            }
                        }
                    }
                }
                Err(errors) => {
                    for e in errors {
                        warn!("inotify error: {e}");
                    }
                }
            }
        })
        .context("failed to create debouncer")?;

        let watcher = Self {
            debouncer,
            watched_paths: std::sync::Mutex::new(Vec::new()),
        };

        Ok((watcher, receiver))
    }

    pub fn watch(&mut self, path: &Path) -> Result<(), WatcherError> {
        // notify-debouncer-full exposes the inner watcher through `debouncer.watcher()`.
        self.debouncer
            .watcher()
            .watch(path, RecursiveMode::Recursive)
            .map_err(|e| WatcherError::Watch {
                path: path.to_path_buf(),
                reason: e.to_string(),
            })?;
        info!("watching {}", path.display());
        self.watched_paths.lock().unwrap().push(path.to_path_buf());
        Ok(())
    }

    pub fn unwatch(&mut self, path: &Path) -> Result<(), WatcherError> {
        self.debouncer
            .watcher()
            .unwatch(path)
            .map_err(|e| WatcherError::Watch {
                path: path.to_path_buf(),
                reason: e.to_string(),
            })?;
        self.watched_paths.lock().unwrap().retain(|p| p != path);
        Ok(())
    }

    pub fn watched_paths(&self) -> Vec<PathBuf> {
        self.watched_paths.lock().unwrap().clone()
    }
}

fn is_markdown(path: &Path) -> bool {
    path.extension()
        .and_then(|e| e.to_str())
        .map(|e| e.eq_ignore_ascii_case("md"))
        .unwrap_or(false)
}

fn classify_event(kind: &notify::EventKind, path: PathBuf) -> Option<FileEvent> {
    use notify::EventKind::*;
    use notify::event::{CreateKind, ModifyKind, RemoveKind};

    match kind {
        Create(CreateKind::File) | Create(CreateKind::Any) => Some(FileEvent::Created(path)),
        Modify(ModifyKind::Data(_)) | Modify(ModifyKind::Any) => Some(FileEvent::Changed(path)),
        Remove(RemoveKind::File) | Remove(RemoveKind::Any) => Some(FileEvent::Deleted(path)),
        // Access events, metadata-only changes, and directory events are not surfaced.
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;
    use tempfile::TempDir;

    #[tokio::test]
    async fn watch_and_receive_change_event() {
        let dir = TempDir::new().unwrap();
        let md_file = dir.path().join("test.md");
        std::fs::write(&md_file, "# initial").unwrap();

        let (mut watcher, mut rx) = FileWatcher::new(32).unwrap();
        watcher.watch(dir.path()).unwrap();

        // Give the watcher time to set up before writing.
        tokio::time::sleep(Duration::from_millis(100)).await;

        std::fs::write(&md_file, "# updated").unwrap();

        // Wait for the debounce window plus margin.
        let timeout = Duration::from_millis(1500);
        let result = tokio::time::timeout(timeout, rx.recv()).await;

        assert!(result.is_ok(), "expected a file event within timeout");
        let event = result.unwrap();
        assert!(event.is_some());
        let event = event.unwrap();
        assert!(matches!(event, FileEvent::Changed(_) | FileEvent::Created(_)));
        assert_eq!(event.path(), md_file.as_path());
    }

    #[tokio::test]
    async fn non_markdown_files_are_ignored() {
        let dir = TempDir::new().unwrap();
        let txt_file = dir.path().join("notes.txt");

        let (mut watcher, mut rx) = FileWatcher::new(32).unwrap();
        watcher.watch(dir.path()).unwrap();

        tokio::time::sleep(Duration::from_millis(100)).await;
        std::fs::write(&txt_file, "hello").unwrap();

        // Wait longer than the debounce window; no event should arrive.
        let timeout = Duration::from_millis(1200);
        let result = tokio::time::timeout(timeout, rx.recv()).await;
        assert!(result.is_err(), "non-markdown file should produce no event");
    }

    #[test]
    fn is_markdown_recognises_md_extension() {
        assert!(is_markdown(Path::new("/docs/note.md")));
        assert!(is_markdown(Path::new("/docs/note.MD")));
        assert!(!is_markdown(Path::new("/docs/note.txt")));
        assert!(!is_markdown(Path::new("/docs/note")));
    }
}
