use std::path::PathBuf;

use anyhow::Result;
use async_trait::async_trait;
use tracing::{debug, warn};

use crate::{SecretStore, SecretStoreError};

/// Fallback secret store that writes the token to a 0600 file.
/// Used when GNOME Keyring is unavailable (headless servers, CI, etc.).
/// The token file lives at ~/.config/interlinedlist-sync/.token-<account>.
pub struct FileSecretStore {
    config_dir: PathBuf,
}

impl FileSecretStore {
    pub fn new(config_dir: PathBuf) -> Self {
        Self { config_dir }
    }

    pub fn default_store() -> Self {
        let config_dir = dirs::config_dir()
            .unwrap_or_else(|| PathBuf::from("~/.config"))
            .join("interlinedlist-sync");
        Self::new(config_dir)
    }

    fn token_path(&self, account: &str) -> PathBuf {
        // Sanitize account so it can be used as a filename component.
        let safe: String = account
            .chars()
            .map(|c| {
                if c.is_alphanumeric() || c == '-' || c == '_' || c == '@' || c == '.' {
                    c
                } else {
                    '_'
                }
            })
            .collect();
        self.config_dir.join(format!(".token-{safe}"))
    }
}

#[async_trait]
impl SecretStore for FileSecretStore {
    async fn store_token(&self, account: &str, token: &str) -> Result<(), SecretStoreError> {
        std::fs::create_dir_all(&self.config_dir)?;
        let path = self.token_path(account);
        std::fs::write(&path, token)?;
        set_permissions_600(&path)?;
        debug!("token stored at {}", path.display());
        Ok(())
    }

    async fn load_token(&self, account: &str) -> Result<String, SecretStoreError> {
        let path = self.token_path(account);
        if !path.exists() {
            return Err(SecretStoreError::NotFound {
                account: account.to_string(),
            });
        }
        let token = std::fs::read_to_string(&path)?;
        debug!("token loaded from {}", path.display());
        Ok(token.trim().to_string())
    }

    async fn delete_token(&self, account: &str) -> Result<(), SecretStoreError> {
        let path = self.token_path(account);
        if path.exists() {
            std::fs::remove_file(&path)?;
            debug!("token deleted from {}", path.display());
        } else {
            warn!("delete_token called but no token file found for account {account}");
        }
        Ok(())
    }
}

#[cfg(unix)]
fn set_permissions_600(path: &std::path::Path) -> Result<(), SecretStoreError> {
    use std::os::unix::fs::PermissionsExt;
    let perms = std::fs::Permissions::from_mode(0o600);
    std::fs::set_permissions(path, perms)?;
    Ok(())
}

#[cfg(not(unix))]
fn set_permissions_600(_path: &std::path::Path) -> Result<(), SecretStoreError> {
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    fn make_store(dir: &TempDir) -> FileSecretStore {
        FileSecretStore::new(dir.path().to_path_buf())
    }

    #[tokio::test]
    async fn store_and_load_token() {
        let dir = tempfile::TempDir::new().unwrap();
        let store = make_store(&dir);
        store
            .store_token("user@example.com", "tok123")
            .await
            .unwrap();
        let loaded = store.load_token("user@example.com").await.unwrap();
        assert_eq!(loaded, "tok123");
    }

    #[tokio::test]
    async fn load_missing_returns_not_found() {
        let dir = tempfile::TempDir::new().unwrap();
        let store = make_store(&dir);
        let result = store.load_token("nobody@example.com").await;
        assert!(matches!(result, Err(SecretStoreError::NotFound { .. })));
    }

    #[tokio::test]
    async fn delete_token_removes_file() {
        let dir = tempfile::TempDir::new().unwrap();
        let store = make_store(&dir);
        store
            .store_token("user@example.com", "tok456")
            .await
            .unwrap();
        store.delete_token("user@example.com").await.unwrap();
        let result = store.load_token("user@example.com").await;
        assert!(matches!(result, Err(SecretStoreError::NotFound { .. })));
    }
}
