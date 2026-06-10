use anyhow::Result;
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
