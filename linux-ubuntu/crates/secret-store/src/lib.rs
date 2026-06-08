use anyhow::Result;
use async_trait::async_trait;
use thiserror::Error;

pub mod file_store;

#[cfg(feature = "gnome-keyring")]
pub mod keyring_store;

#[derive(Debug, Error)]
pub enum SecretStoreError {
    #[error("secret not found for account: {account}")]
    NotFound { account: String },

    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),

    #[error("keyring error: {0}")]
    Keyring(String),
}

#[async_trait]
pub trait SecretStore: Send + Sync {
    async fn store_token(&self, account: &str, token: &str) -> Result<(), SecretStoreError>;
    async fn load_token(&self, account: &str) -> Result<String, SecretStoreError>;
    async fn delete_token(&self, account: &str) -> Result<(), SecretStoreError>;
}

pub use file_store::FileSecretStore;

#[cfg(feature = "gnome-keyring")]
pub use keyring_store::KeyringSecretStore;
