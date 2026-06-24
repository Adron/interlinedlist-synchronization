use async_trait::async_trait;
use tracing::{debug, warn};
use zbus::{proxy, Connection};

use crate::NetworkMonitor;

#[proxy(
    interface = "org.freedesktop.NetworkManager",
    default_service = "org.freedesktop.NetworkManager",
    default_path = "/org/freedesktop/NetworkManager"
)]
trait NetworkManager {
    #[zbus(signal)]
    fn state_changed(&self, state: u32) -> zbus::Result<()>;
}

pub struct NetworkManagerMonitor;

impl NetworkManagerMonitor {
    pub async fn new() -> zbus::Result<Self> {
        Ok(Self)
    }
}

#[async_trait]
impl NetworkMonitor for NetworkManagerMonitor {
    async fn wait_for_reconnect(&self) {
        match connect_and_wait().await {
            Ok(()) => {}
            Err(e) => {
                warn!("NetworkManager D-Bus error: {e}; falling back to no-op");
                std::future::pending::<()>().await;
            }
        }
    }
}

async fn connect_and_wait() -> zbus::Result<()> {
    let conn = Connection::system().await?;
    let proxy = NetworkManagerProxy::new(&conn).await?;
    let mut stream = proxy.receive_state_changed().await?;

    loop {
        if let Some(signal) = stream.next().await {
            let args = signal.args()?;
            if args.state == 70 {
                debug!("NetworkManager reports CONNECTED_GLOBAL");
                return Ok(());
            }
        }
    }
}
