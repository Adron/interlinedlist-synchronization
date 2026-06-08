use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use thiserror::Error;
use tracing::{debug, info};

#[derive(Debug, Error)]
pub enum ConfigError {
    #[error("config file not found at {path}")]
    NotFound { path: PathBuf },

    #[error("failed to parse config: {0}")]
    Parse(#[from] toml::de::Error),

    #[error("config validation failed: {0}")]
    Validation(String),

    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SyncConfig {
    pub watched_dirs: Vec<String>,
    pub interval_seconds: u64,
    pub conflict_resolution: ConflictResolution,
    pub pause_on_battery: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "kebab-case")]
pub enum ConflictResolution {
    RemoteWins,
    ConflictCopy,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct NotificationsConfig {
    pub show_success: bool,
    pub show_conflicts: bool,
    pub show_errors: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct NetworkConfig {
    pub api_base_url: String,
    pub timeout_seconds: u64,
    pub max_retries: u32,
    pub retry_backoff_base_seconds: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct UiConfig {
    pub autostart: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct AppConfig {
    pub sync: SyncConfig,
    pub notifications: NotificationsConfig,
    pub network: NetworkConfig,
    pub ui: UiConfig,
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            sync: SyncConfig {
                watched_dirs: vec!["~/Documents/InterlinedList".to_string()],
                interval_seconds: 300,
                conflict_resolution: ConflictResolution::RemoteWins,
                pause_on_battery: false,
            },
            notifications: NotificationsConfig {
                show_success: true,
                show_conflicts: true,
                show_errors: true,
            },
            network: NetworkConfig {
                api_base_url: "https://interlinedlist.com/api".to_string(),
                timeout_seconds: 30,
                max_retries: 5,
                retry_backoff_base_seconds: 2,
            },
            ui: UiConfig { autostart: true },
        }
    }
}

impl AppConfig {
    pub fn validate(&self) -> Result<(), ConfigError> {
        if self.sync.watched_dirs.is_empty() {
            return Err(ConfigError::Validation(
                "sync.watched_dirs must not be empty".to_string(),
            ));
        }
        if self.network.api_base_url.is_empty() {
            return Err(ConfigError::Validation(
                "network.api_base_url must not be empty".to_string(),
            ));
        }
        if self.network.max_retries == 0 {
            return Err(ConfigError::Validation(
                "network.max_retries must be at least 1".to_string(),
            ));
        }
        if self.network.retry_backoff_base_seconds == 0 {
            return Err(ConfigError::Validation(
                "network.retry_backoff_base_seconds must be at least 1".to_string(),
            ));
        }
        Ok(())
    }
}

pub struct ConfigStore {
    path: PathBuf,
}

impl ConfigStore {
    pub fn new(path: PathBuf) -> Self {
        Self { path }
    }

    pub fn default_path() -> PathBuf {
        dirs::config_dir()
            .unwrap_or_else(|| PathBuf::from("~/.config"))
            .join("interlinedlist-sync")
            .join("config.toml")
    }

    pub fn load(&self) -> Result<AppConfig, ConfigError> {
        debug!("loading config from {}", self.path.display());

        if !self.path.exists() {
            return Err(ConfigError::NotFound {
                path: self.path.clone(),
            });
        }

        let raw = std::fs::read_to_string(&self.path)?;
        let config: AppConfig = toml::from_str(&raw)?;
        config.validate()?;

        info!("config loaded from {}", self.path.display());
        Ok(config)
    }

    pub fn load_or_default(&self) -> Result<AppConfig> {
        match self.load() {
            Ok(cfg) => Ok(cfg),
            Err(ConfigError::NotFound { .. }) => {
                info!("no config found, using defaults");
                Ok(AppConfig::default())
            }
            Err(e) => Err(e).context("failed to load config"),
        }
    }

    pub fn save(&self, config: &AppConfig) -> Result<()> {
        config.validate().context("config validation failed")?;

        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent)
                .with_context(|| format!("failed to create config dir {}", parent.display()))?;
        }

        let serialized = toml::to_string_pretty(config).context("failed to serialize config")?;

        // Atomic write: write to a temp file then rename.
        let tmp_path = self.path.with_extension("toml.tmp");
        std::fs::write(&tmp_path, &serialized)
            .with_context(|| format!("failed to write {}", tmp_path.display()))?;
        std::fs::rename(&tmp_path, &self.path)
            .with_context(|| format!("failed to rename to {}", self.path.display()))?;

        // 0600: only the owner can read the config (it may reference watched dirs).
        set_file_permissions_600(&self.path)?;

        debug!("config saved to {}", self.path.display());
        Ok(())
    }

    pub fn path(&self) -> &Path {
        &self.path
    }
}

#[cfg(unix)]
fn set_file_permissions_600(path: &Path) -> Result<()> {
    use std::os::unix::fs::PermissionsExt;
    let perms = std::fs::Permissions::from_mode(0o600);
    std::fs::set_permissions(path, perms)
        .with_context(|| format!("failed to set 0600 on {}", path.display()))
}

#[cfg(not(unix))]
fn set_file_permissions_600(_path: &Path) -> Result<()> {
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    fn make_store(dir: &TempDir) -> ConfigStore {
        ConfigStore::new(dir.path().join("config.toml"))
    }

    #[test]
    fn round_trip_default_config() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let original = AppConfig::default();
        store.save(&original).unwrap();
        let loaded = store.load().unwrap();
        assert_eq!(original, loaded);
    }

    #[test]
    fn round_trip_custom_config() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let mut cfg = AppConfig::default();
        cfg.sync.watched_dirs = vec!["~/Notes".to_string(), "~/Docs".to_string()];
        cfg.sync.interval_seconds = 60;
        cfg.sync.conflict_resolution = ConflictResolution::ConflictCopy;
        cfg.sync.pause_on_battery = true;
        cfg.notifications.show_success = false;
        cfg.network.max_retries = 3;
        cfg.ui.autostart = false;

        store.save(&cfg).unwrap();
        let loaded = store.load().unwrap();
        assert_eq!(cfg, loaded);
    }

    #[test]
    fn load_missing_returns_not_found() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let result = store.load();
        assert!(matches!(result, Err(ConfigError::NotFound { .. })));
    }

    #[test]
    fn load_or_default_returns_defaults_when_missing() {
        let dir = TempDir::new().unwrap();
        let store = make_store(&dir);
        let cfg = store.load_or_default().unwrap();
        assert_eq!(cfg, AppConfig::default());
    }

    #[test]
    fn validation_rejects_empty_watched_dirs() {
        let mut cfg = AppConfig::default();
        cfg.sync.watched_dirs.clear();
        let result = cfg.validate();
        assert!(matches!(result, Err(ConfigError::Validation(_))));
    }

    #[test]
    fn validation_rejects_empty_api_url() {
        let mut cfg = AppConfig::default();
        cfg.network.api_base_url = String::new();
        let result = cfg.validate();
        assert!(matches!(result, Err(ConfigError::Validation(_))));
    }

    #[test]
    fn validation_rejects_zero_max_retries() {
        let mut cfg = AppConfig::default();
        cfg.network.max_retries = 0;
        let result = cfg.validate();
        assert!(matches!(result, Err(ConfigError::Validation(_))));
    }
}