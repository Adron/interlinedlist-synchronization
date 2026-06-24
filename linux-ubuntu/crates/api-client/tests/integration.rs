use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use api_client::{
    ApiClient, ApiClientConfig, ApiClientTrait, CreateDocumentRequest, UpdateDocumentRequest,
};
use secret_store::{FileSecretStore, SecretStore};
use tempfile::TempDir;

fn skip_if_unconfigured() -> Option<(String, String, String)> {
    let email = std::env::var("INTERLINEDLIST_EMAIL").ok()?;
    let password = std::env::var("INTERLINEDLIST_PASSWORD").ok()?;
    let base_url = std::env::var("INTERLINEDLIST_API_BASE_URL").ok()?;
    Some((email, password, base_url))
}

// INTERLINEDLIST_API_BASE_URL in .env is "https://interlinedlist.com/" (no /api segment).
// ApiClient::url() trims trailing slash then prepends paths like "/auth/login", producing
// "https://interlinedlist.com/auth/login" — missing the required /api prefix.
// This function normalises: strips trailing slash, appends /api if absent.
fn normalise_base_url(raw: &str) -> String {
    let trimmed = raw.trim_end_matches('/');
    if trimmed.ends_with("/api") {
        trimmed.to_string()
    } else {
        format!("{trimmed}/api")
    }
}

fn unique_title() -> String {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or(Duration::ZERO)
        .subsec_nanos();
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or(Duration::ZERO)
        .as_secs();
    format!("__linux-integ-{secs}{nanos:09}")
}

struct TestClient {
    client: ApiClient,
    account: String,
    _dir: TempDir,
}

async fn make_live_client(email: &str, password: &str, base_url: &str) -> TestClient {
    let dir = TempDir::new().expect("tempdir");
    let secrets = Arc::new(FileSecretStore::new(dir.path().to_path_buf()));
    let config = ApiClientConfig {
        base_url: base_url.to_string(),
        timeout: Duration::from_secs(30),
        max_retries: 1,
        backoff_base: Duration::from_millis(200),
    };
    let client = ApiClient::new(config, secrets.clone()).expect("ApiClient::new");

    let session_cookie = client
        .login(email, password)
        .await
        .expect("login during test setup");
    secrets
        .store_token(email, &session_cookie)
        .await
        .expect("store session cookie");

    TestClient {
        client,
        account: email.to_string(),
        _dir: dir,
    }
}

// RAII guard: deletes a document on Drop if id is Some.
// Uses block_in_place so Drop can call async code without spawning a new runtime.
struct DocGuard {
    client: Arc<ApiClient>,
    account: String,
    id: Option<String>,
}

impl DocGuard {
    fn new(client: Arc<ApiClient>, account: String, id: String) -> Self {
        Self {
            client,
            account,
            id: Some(id),
        }
    }

    fn defuse(&mut self) {
        self.id = None;
    }
}

impl Drop for DocGuard {
    fn drop(&mut self) {
        if let Some(id) = self.id.take() {
            let client = self.client.clone();
            let account = self.account.clone();
            let _ = tokio::task::block_in_place(|| {
                tokio::runtime::Handle::current()
                    .block_on(async move { client.delete_document(&account, &id).await })
            });
        }
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn login_returns_token() {
    let Some((email, password, raw_url)) = skip_if_unconfigured() else {
        eprintln!("Integration creds not set; skipping.");
        return;
    };
    let base_url = normalise_base_url(&raw_url);

    let dir = TempDir::new().expect("tempdir");
    let secrets = Arc::new(FileSecretStore::new(dir.path().to_path_buf()));
    let config = ApiClientConfig {
        base_url,
        timeout: Duration::from_secs(30),
        max_retries: 1,
        backoff_base: Duration::from_millis(200),
    };
    let client = ApiClient::new(config, secrets).expect("ApiClient::new");

    let result = client.login(&email, &password).await;
    assert!(result.is_ok(), "login failed: {:?}", result.unwrap_err());
    let cookie = result.unwrap();
    assert!(
        cookie.starts_with("session="),
        "expected session= cookie, got: {cookie:?}"
    );
    assert!(
        cookie.len() > "session=".len(),
        "session cookie value must be non-empty"
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn fetch_documents_with_bearer() {
    let Some((email, password, raw_url)) = skip_if_unconfigured() else {
        eprintln!("Integration creds not set; skipping.");
        return;
    };
    let base_url = normalise_base_url(&raw_url);
    let ctx = make_live_client(&email, &password, &base_url).await;

    let result = ctx.client.list_documents(&ctx.account).await;
    assert!(
        result.is_ok(),
        "list_documents failed: {:?}",
        result.unwrap_err()
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn create_update_delete_round_trip() {
    let Some((email, password, raw_url)) = skip_if_unconfigured() else {
        eprintln!("Integration creds not set; skipping.");
        return;
    };
    let base_url = normalise_base_url(&raw_url);
    let ctx = make_live_client(&email, &password, &base_url).await;
    let client = Arc::new(ctx.client);
    let account = ctx.account.clone();

    let title = unique_title();
    let doc = client
        .create_document(
            &account,
            CreateDocumentRequest {
                title: title.clone(),
                content: "# Initial content".to_string(),
            },
        )
        .await
        .expect("create_document");

    assert!(!doc.id.is_empty(), "created doc must have an id");
    assert_eq!(doc.title, title);

    let mut guard = DocGuard::new(client.clone(), account.clone(), doc.id.clone());

    let updated = client
        .update_document(
            &account,
            &doc.id,
            UpdateDocumentRequest {
                title: None,
                content: "# Updated content".to_string(),
            },
        )
        .await
        .expect("update_document");

    assert_eq!(updated.content, "# Updated content");

    guard.defuse();
    client
        .delete_document(&account, &doc.id)
        .await
        .expect("delete_document");

    let fetch = client.get_document(&account, &doc.id).await;
    assert!(
        matches!(fetch, Err(api_client::ApiError::NotFound { .. })),
        "expected NotFound after delete, got: {:?}",
        fetch
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn fetch_delta_initial() {
    let Some((email, password, raw_url)) = skip_if_unconfigured() else {
        eprintln!("Integration creds not set; skipping.");
        return;
    };
    let base_url = normalise_base_url(&raw_url);
    let ctx = make_live_client(&email, &password, &base_url).await;

    let delta = ctx
        .client
        .fetch_delta(&ctx.account, None)
        .await
        .expect("fetch_delta(None)");

    assert!(
        delta.synced_at.timestamp() > 0,
        "synced_at (lastSyncAt) should be a real timestamp"
    );
}

// The interlinedlist.com /documents/sync endpoint does not return tombstones for deleted
// documents — deleted docs are absent from the delta rather than present with deleted:true.
// This test verifies the delta endpoint with a `since` timestamp returns a valid response
// and that the deleted doc is absent (not that it is present with a deleted flag).
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn fetch_delta_with_tombstone() {
    let Some((email, password, raw_url)) = skip_if_unconfigured() else {
        eprintln!("Integration creds not set; skipping.");
        return;
    };
    let base_url = normalise_base_url(&raw_url);
    let ctx = make_live_client(&email, &password, &base_url).await;
    let client = Arc::new(ctx.client);
    let account = ctx.account.clone();

    let title = unique_title();
    let doc = client
        .create_document(
            &account,
            CreateDocumentRequest {
                title: title.clone(),
                content: "# Tombstone test".to_string(),
            },
        )
        .await
        .expect("create_document for tombstone test");

    let mut guard = DocGuard::new(client.clone(), account.clone(), doc.id.clone());

    let baseline = client
        .fetch_delta(&account, None)
        .await
        .expect("fetch_delta baseline");
    let synced_at = baseline.synced_at;

    client
        .delete_document(&account, &doc.id)
        .await
        .expect("delete_document for tombstone test");
    guard.defuse();

    let delta = client
        .fetch_delta(&account, Some(synced_at))
        .await
        .expect("fetch_delta with since");

    // The API does not return tombstones. The deleted doc should be absent from the delta.
    let found = delta.documents.iter().any(|d| d.id == doc.id);
    assert!(
        !found,
        "deleted doc should not appear in delta (API does not return tombstones), but found: {:?}",
        delta.documents.iter().find(|d| d.id == doc.id)
    );
}
