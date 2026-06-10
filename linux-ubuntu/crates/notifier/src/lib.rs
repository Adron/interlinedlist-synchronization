use async_trait::async_trait;
use thiserror::Error;

#[derive(Debug, Error)]
pub enum NotifierError {
    #[error("notification backend error: {0}")]
    Backend(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Urgency {
    Low,
    Normal,
    Critical,
}

#[derive(Debug, Clone)]
pub struct Notification {
    pub summary: String,
    pub body: Option<String>,
    pub urgency: Urgency,
}

impl Notification {
    pub fn low(summary: impl Into<String>) -> Self {
        Self {
            summary: summary.into(),
            body: None,
            urgency: Urgency::Low,
        }
    }

    pub fn normal(summary: impl Into<String>) -> Self {
        Self {
            summary: summary.into(),
            body: None,
            urgency: Urgency::Normal,
        }
    }

    pub fn critical(summary: impl Into<String>) -> Self {
        Self {
            summary: summary.into(),
            body: None,
            urgency: Urgency::Critical,
        }
    }

    pub fn with_body(mut self, body: impl Into<String>) -> Self {
        self.body = Some(body.into());
        self
    }
}

#[async_trait]
pub trait Notifier: Send + Sync {
    async fn notify(&self, notification: Notification) -> Result<(), NotifierError>;
}

pub struct StubNotifier;

#[async_trait]
impl Notifier for StubNotifier {
    async fn notify(&self, _notification: Notification) -> Result<(), NotifierError> {
        Ok(())
    }
}

#[cfg(feature = "libnotify")]
pub mod libnotify;

#[cfg(feature = "libnotify")]
pub use libnotify::LibnotifyNotifier;
