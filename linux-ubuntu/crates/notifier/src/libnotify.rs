use async_trait::async_trait;
use tracing::debug;

use crate::{Notification, Notifier, NotifierError, Urgency};

pub struct LibnotifyNotifier {
    app_name: String,
}

impl LibnotifyNotifier {
    pub fn new(app_name: impl Into<String>) -> Self {
        Self {
            app_name: app_name.into(),
        }
    }
}

#[async_trait]
impl Notifier for LibnotifyNotifier {
    async fn notify(&self, notification: Notification) -> Result<(), NotifierError> {
        let app_name = self.app_name.clone();
        tokio::task::spawn_blocking(move || {
            let mut n = notify_rust::Notification::new();
            n.appname(&app_name)
                .summary(&notification.summary)
                .timeout(notify_rust::Timeout::Default);

            if let Some(body) = &notification.body {
                n.body(body);
            }

            match notification.urgency {
                Urgency::Low => {
                    n.urgency(notify_rust::Urgency::Low);
                }
                Urgency::Normal => {
                    n.urgency(notify_rust::Urgency::Normal);
                }
                Urgency::Critical => {
                    n.urgency(notify_rust::Urgency::Critical);
                }
            }

            debug!("sending libnotify notification: {}", notification.summary);
            n.show()
                .map_err(|e| NotifierError::Backend(e.to_string()))?;
            Ok(())
        })
        .await
        .map_err(|e| NotifierError::Backend(format!("task join error: {e}")))?
    }
}
