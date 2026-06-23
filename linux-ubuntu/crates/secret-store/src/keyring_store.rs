use async_trait::async_trait;
use secret_service::{EncryptionType, SecretService};
use tracing::debug;

use crate::{SecretStore, SecretStoreError};

const SERVICE_NAME: &str = "interlinedlist-sync";

pub struct KeyringSecretStore;

impl KeyringSecretStore {
    pub fn new() -> Self {
        Self
    }

    /// Attempt to connect to GNOME Keyring and open the default collection.
    /// Returns `Ok(Self)` if the keyring is reachable, `Err` if not.
    /// Call this at startup and fall back to `FileSecretStore` on error.
    pub async fn new_checked() -> Result<Self, SecretStoreError> {
        let ss = SecretService::connect(EncryptionType::Dh)
            .await
            .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;
        // Verify the default collection is accessible. This fails, e.g., when
        // no keyring daemon is running (headless / SSH session).
        ss.get_default_collection()
            .await
            .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;
        Ok(Self)
    }
}

impl Default for KeyringSecretStore {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait]
impl SecretStore for KeyringSecretStore {
    async fn store_token(&self, account: &str, token: &str) -> Result<(), SecretStoreError> {
        let ss = SecretService::connect(EncryptionType::Dh)
            .await
            .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;

        let collection = ss
            .get_default_collection()
            .await
            .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;

        collection
            .create_item(
                &format!("{SERVICE_NAME} token for {account}"),
                std::collections::HashMap::from([("service", SERVICE_NAME), ("account", account)]),
                token.as_bytes(),
                true, // replace existing
                "text/plain",
            )
            .await
            .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;

        debug!("token stored in GNOME Keyring for account {account}");
        Ok(())
    }

    async fn load_token(&self, account: &str) -> Result<String, SecretStoreError> {
        let ss = SecretService::connect(EncryptionType::Dh)
            .await
            .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;

        let collection = ss
            .get_default_collection()
            .await
            .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;

        let items = collection
            .search_items(std::collections::HashMap::from([
                ("service", SERVICE_NAME),
                ("account", account),
            ]))
            .await
            .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;

        let item = items.first().ok_or_else(|| SecretStoreError::NotFound {
            account: account.to_string(),
        })?;

        let secret_bytes = item
            .get_secret()
            .await
            .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;

        let token = String::from_utf8(secret_bytes)
            .map_err(|e| SecretStoreError::Keyring(format!("invalid UTF-8 in keyring: {e}")))?;

        debug!("token loaded from GNOME Keyring for account {account}");
        Ok(token)
    }

    async fn delete_token(&self, account: &str) -> Result<(), SecretStoreError> {
        let ss = SecretService::connect(EncryptionType::Dh)
            .await
            .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;

        let collection = ss
            .get_default_collection()
            .await
            .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;

        let items = collection
            .search_items(std::collections::HashMap::from([
                ("service", SERVICE_NAME),
                ("account", account),
            ]))
            .await
            .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;

        for item in items {
            item.delete()
                .await
                .map_err(|e| SecretStoreError::Keyring(e.to_string()))?;
        }

        debug!("token deleted from GNOME Keyring for account {account}");
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;
    use std::sync::Mutex;

    use async_trait::async_trait;

    use crate::{SecretStore, SecretStoreError};

    /// In-memory `SecretStore` used to verify trait-level contracts without a
    /// running D-Bus session.  Not exported — test-only.
    struct InMemorySecretStore {
        tokens: Mutex<HashMap<String, String>>,
    }

    impl InMemorySecretStore {
        fn new() -> Self {
            Self {
                tokens: Mutex::new(HashMap::new()),
            }
        }
    }

    #[async_trait]
    impl SecretStore for InMemorySecretStore {
        async fn store_token(&self, account: &str, token: &str) -> Result<(), SecretStoreError> {
            self.tokens
                .lock()
                .unwrap()
                .insert(account.to_string(), token.to_string());
            Ok(())
        }

        async fn load_token(&self, account: &str) -> Result<String, SecretStoreError> {
            self.tokens
                .lock()
                .unwrap()
                .get(account)
                .cloned()
                .ok_or_else(|| SecretStoreError::NotFound {
                    account: account.to_string(),
                })
        }

        async fn delete_token(&self, account: &str) -> Result<(), SecretStoreError> {
            self.tokens.lock().unwrap().remove(account);
            Ok(())
        }
    }

    #[tokio::test]
    async fn in_memory_store_and_load() {
        let store = InMemorySecretStore::new();
        store
            .store_token("user@example.com", "secret-abc")
            .await
            .unwrap();
        let tok = store.load_token("user@example.com").await.unwrap();
        assert_eq!(tok, "secret-abc");
    }

    #[tokio::test]
    async fn in_memory_load_missing_returns_not_found() {
        let store = InMemorySecretStore::new();
        let err = store.load_token("nobody@example.com").await.unwrap_err();
        assert!(matches!(err, SecretStoreError::NotFound { .. }));
    }

    #[tokio::test]
    async fn in_memory_delete_then_load_returns_not_found() {
        let store = InMemorySecretStore::new();
        store.store_token("user@example.com", "tok").await.unwrap();
        store.delete_token("user@example.com").await.unwrap();
        let err = store.load_token("user@example.com").await.unwrap_err();
        assert!(matches!(err, SecretStoreError::NotFound { .. }));
    }

    #[tokio::test]
    async fn in_memory_store_overwrites_existing() {
        let store = InMemorySecretStore::new();
        store.store_token("u", "first").await.unwrap();
        store.store_token("u", "second").await.unwrap();
        let tok = store.load_token("u").await.unwrap();
        assert_eq!(tok, "second");
    }

    /// Real GNOME Keyring round-trip.  Requires a running D-Bus session with
    /// GNOME Keyring or KWallet.  Run manually on a Linux desktop with:
    ///   cargo test -p secret-store -- --ignored keyring_roundtrip
    #[tokio::test]
    #[ignore]
    async fn keyring_roundtrip() {
        use super::KeyringSecretStore;

        let store = KeyringSecretStore::new_checked()
            .await
            .expect("GNOME Keyring must be running for this test");

        let account = "test-keyring-roundtrip@example.com";
        store.store_token(account, "roundtrip-tok").await.unwrap();
        let loaded = store.load_token(account).await.unwrap();
        assert_eq!(loaded, "roundtrip-tok");
        store.delete_token(account).await.unwrap();
        let err = store.load_token(account).await.unwrap_err();
        assert!(matches!(err, SecretStoreError::NotFound { .. }));
    }
}
