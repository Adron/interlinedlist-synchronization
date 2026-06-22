use std::sync::Arc;
use std::time::Duration;

use anyhow::Result;
use async_trait::async_trait;
use chrono::{DateTime, Utc};
use reqwest::{Client, StatusCode};
use serde::{Deserialize, Serialize};
use thiserror::Error;
use tracing::{debug, info, warn};

use secret_store::SecretStore;

pub mod mock;

#[derive(Debug, Error)]
pub enum ApiError {
    #[error("authentication failed: {0}")]
    Auth(String),

    #[error("not found: {resource}")]
    NotFound { resource: String },

    #[error("conflict: {0}")]
    Conflict(String),

    #[error("rate limited — retry after {retry_after_secs}s")]
    RateLimited { retry_after_secs: u64 },

    #[error("server error {status}: {body}")]
    Server { status: u16, body: String },

    #[error("network error: {0}")]
    Network(#[from] reqwest::Error),

    #[error("retries exhausted after {attempts} attempts: {last_error}")]
    RetriesExhausted { attempts: u32, last_error: String },

    #[error("no token available for account {account}")]
    NoToken { account: String },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DocumentSummary {
    pub id: String,
    pub title: String,
    pub updated_at: DateTime<Utc>,
    pub sha256: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Document {
    pub id: String,
    pub title: String,
    pub content: String,
    pub updated_at: DateTime<Utc>,
    pub sha256: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CreateDocumentRequest {
    pub title: String,
    pub content: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UpdateDocumentRequest {
    pub title: Option<String>,
    pub content: String,
}

#[derive(Debug, Deserialize)]
struct LoginResponse {
    token: String,
}

#[async_trait]
pub trait ApiClientTrait: Send + Sync {
    async fn login(&self, username: &str, password: &str) -> Result<String, ApiError>;
    async fn list_documents(&self, account: &str) -> Result<Vec<DocumentSummary>, ApiError>;
    async fn get_document(&self, account: &str, id: &str) -> Result<Document, ApiError>;
    async fn create_document(
        &self,
        account: &str,
        req: CreateDocumentRequest,
    ) -> Result<Document, ApiError>;
    async fn update_document(
        &self,
        account: &str,
        id: &str,
        req: UpdateDocumentRequest,
    ) -> Result<Document, ApiError>;
    async fn delete_document(&self, account: &str, id: &str) -> Result<(), ApiError>;
}

#[derive(Clone)]
pub struct ApiClientConfig {
    pub base_url: String,
    pub timeout: Duration,
    pub max_retries: u32,
    pub backoff_base: Duration,
}

pub struct ApiClient {
    http: Client,
    config: ApiClientConfig,
    secrets: Arc<dyn SecretStore>,
}

impl ApiClient {
    pub fn new(config: ApiClientConfig, secrets: Arc<dyn SecretStore>) -> Result<Self> {
        let http = Client::builder()
            .use_rustls_tls()
            .timeout(config.timeout)
            .build()?;
        Ok(Self {
            http,
            config,
            secrets,
        })
    }

    async fn token_for(&self, account: &str) -> Result<String, ApiError> {
        self.secrets
            .load_token(account)
            .await
            .map_err(|_| ApiError::NoToken {
                account: account.to_string(),
            })
    }

    async fn execute_with_retry<F, Fut, T>(&self, op: F) -> Result<T, ApiError>
    where
        F: Fn() -> Fut,
        Fut: std::future::Future<Output = Result<T, ApiError>>,
    {
        let mut attempt = 0u32;
        loop {
            attempt += 1;
            match op().await {
                Ok(v) => return Ok(v),
                Err(ApiError::RateLimited { retry_after_secs }) => {
                    if attempt >= self.config.max_retries {
                        return Err(ApiError::RetriesExhausted {
                            attempts: attempt,
                            last_error: format!("rate limited, retry after {retry_after_secs}s"),
                        });
                    }
                    let wait = Duration::from_secs(retry_after_secs);
                    warn!("rate limited; waiting {retry_after_secs}s (attempt {attempt})");
                    tokio::time::sleep(wait).await;
                }
                Err(ApiError::Network(ref e)) if attempt < self.config.max_retries => {
                    let backoff = self.config.backoff_base * 2u32.pow(attempt - 1);
                    warn!("network error on attempt {attempt}: {e}; retrying in {backoff:?}");
                    tokio::time::sleep(backoff).await;
                }
                Err(ApiError::Server { status, .. })
                    if status >= 500 && attempt < self.config.max_retries =>
                {
                    let backoff = self.config.backoff_base * 2u32.pow(attempt - 1);
                    warn!("server error {status} on attempt {attempt}; retrying in {backoff:?}");
                    tokio::time::sleep(backoff).await;
                }
                Err(e) => {
                    return if attempt > 1 {
                        Err(ApiError::RetriesExhausted {
                            attempts: attempt,
                            last_error: e.to_string(),
                        })
                    } else {
                        Err(e)
                    };
                }
            }
        }
    }

    fn url(&self, path: &str) -> String {
        format!("{}{}", self.config.base_url.trim_end_matches('/'), path)
    }
}

#[async_trait]
impl ApiClientTrait for ApiClient {
    async fn login(&self, username: &str, password: &str) -> Result<String, ApiError> {
        debug!("logging in as {username}");
        let resp = self
            .http
            .post(self.url("/auth/login"))
            .json(&serde_json::json!({ "username": username, "password": password }))
            .send()
            .await?;

        match resp.status() {
            StatusCode::OK => {
                let body: LoginResponse = resp.json().await?;
                info!("login successful for {username}");
                Ok(body.token)
            }
            StatusCode::UNAUTHORIZED => Err(ApiError::Auth("invalid credentials".to_string())),
            s => {
                let status = s.as_u16();
                let body = resp.text().await.unwrap_or_default();
                Err(ApiError::Server { status, body })
            }
        }
    }

    async fn list_documents(&self, account: &str) -> Result<Vec<DocumentSummary>, ApiError> {
        let token = self.token_for(account).await?;
        self.execute_with_retry(|| async {
            let resp = self
                .http
                .get(self.url("/documents"))
                .bearer_auth(&token)
                .send()
                .await?;
            parse_response(resp).await
        })
        .await
    }

    async fn get_document(&self, account: &str, id: &str) -> Result<Document, ApiError> {
        let token = self.token_for(account).await?;
        let url = self.url(&format!("/documents/{id}"));
        self.execute_with_retry(|| async {
            let resp = self.http.get(&url).bearer_auth(&token).send().await?;
            parse_response(resp).await
        })
        .await
    }

    async fn create_document(
        &self,
        account: &str,
        req: CreateDocumentRequest,
    ) -> Result<Document, ApiError> {
        let token = self.token_for(account).await?;
        self.execute_with_retry(|| async {
            let resp = self
                .http
                .post(self.url("/documents"))
                .bearer_auth(&token)
                .json(&req)
                .send()
                .await?;
            parse_response(resp).await
        })
        .await
    }

    async fn update_document(
        &self,
        account: &str,
        id: &str,
        req: UpdateDocumentRequest,
    ) -> Result<Document, ApiError> {
        let token = self.token_for(account).await?;
        let url = self.url(&format!("/documents/{id}"));
        self.execute_with_retry(|| async {
            let resp = self
                .http
                .put(&url)
                .bearer_auth(&token)
                .json(&req)
                .send()
                .await?;
            parse_response(resp).await
        })
        .await
    }

    async fn delete_document(&self, account: &str, id: &str) -> Result<(), ApiError> {
        let token = self.token_for(account).await?;
        let url = self.url(&format!("/documents/{id}"));
        self.execute_with_retry(|| async {
            let resp = self.http.delete(&url).bearer_auth(&token).send().await?;
            match resp.status() {
                StatusCode::OK | StatusCode::NO_CONTENT | StatusCode::ACCEPTED => Ok(()),
                StatusCode::NOT_FOUND => Err(ApiError::NotFound {
                    resource: url.clone(),
                }),
                StatusCode::UNAUTHORIZED => Err(ApiError::Auth("token rejected".to_string())),
                StatusCode::TOO_MANY_REQUESTS => {
                    let retry = retry_after(&resp);
                    Err(ApiError::RateLimited {
                        retry_after_secs: retry,
                    })
                }
                s => {
                    let status = s.as_u16();
                    let body = resp.text().await.unwrap_or_default();
                    Err(ApiError::Server { status, body })
                }
            }
        })
        .await
    }
}

async fn parse_response<T: serde::de::DeserializeOwned>(
    resp: reqwest::Response,
) -> Result<T, ApiError> {
    match resp.status() {
        s if s.is_success() => Ok(resp.json().await?),
        StatusCode::NOT_FOUND => Err(ApiError::NotFound {
            resource: resp.url().to_string(),
        }),
        StatusCode::UNAUTHORIZED => Err(ApiError::Auth("token rejected".to_string())),
        StatusCode::CONFLICT => {
            let body = resp.text().await.unwrap_or_default();
            Err(ApiError::Conflict(body))
        }
        StatusCode::TOO_MANY_REQUESTS => {
            let retry = retry_after(&resp);
            Err(ApiError::RateLimited {
                retry_after_secs: retry,
            })
        }
        s => {
            let status = s.as_u16();
            let body = resp.text().await.unwrap_or_default();
            Err(ApiError::Server { status, body })
        }
    }
}

fn retry_after(resp: &reqwest::Response) -> u64 {
    resp.headers()
        .get("Retry-After")
        .and_then(|v| v.to_str().ok())
        .and_then(|s| s.parse::<u64>().ok())
        .unwrap_or(60)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    use mockito::ServerGuard;
    use secret_store::FileSecretStore;
    use tempfile::TempDir;

    struct TestContext {
        server: ServerGuard,
        client: ApiClient,
        #[allow(dead_code)] // kept alive so the temp dir isn't cleaned up during the test
        dir: TempDir,
        account: String,
    }

    async fn make_context() -> TestContext {
        let server = mockito::Server::new_async().await;
        let dir = TempDir::new().unwrap();
        let secrets = Arc::new(FileSecretStore::new(dir.path().to_path_buf()));
        let account = "test@example.com".to_string();
        secrets.store_token(&account, "test-token").await.unwrap();

        let config = ApiClientConfig {
            base_url: server.url(),
            timeout: Duration::from_secs(5),
            max_retries: 2,
            backoff_base: Duration::from_millis(10),
        };
        let client = ApiClient::new(config, secrets).unwrap();
        TestContext {
            server,
            client,
            dir,
            account,
        }
    }

    #[tokio::test]
    async fn login_returns_token_on_200() {
        let mut ctx = make_context().await;
        let _m = ctx
            .server
            .mock("POST", "/auth/login")
            .with_status(200)
            .with_header("content-type", "application/json")
            .with_body(r#"{"token":"tok-abc"}"#)
            .create_async()
            .await;

        let token = ctx.client.login("user", "pass").await.unwrap();
        assert_eq!(token, "tok-abc");
    }

    #[tokio::test]
    async fn login_returns_auth_error_on_401() {
        let mut ctx = make_context().await;
        let _m = ctx
            .server
            .mock("POST", "/auth/login")
            .with_status(401)
            .create_async()
            .await;

        let err = ctx.client.login("user", "wrong").await.unwrap_err();
        assert!(matches!(err, ApiError::Auth(_)));
    }

    #[tokio::test]
    async fn list_documents_deserializes_correctly() {
        let mut ctx = make_context().await;
        let body =
            r#"[{"id":"doc-1","title":"Note","updated_at":"2026-01-01T00:00:00Z","sha256":null}]"#;
        let _m = ctx
            .server
            .mock("GET", "/documents")
            .match_header("authorization", "Bearer test-token")
            .with_status(200)
            .with_header("content-type", "application/json")
            .with_body(body)
            .create_async()
            .await;

        let docs = ctx.client.list_documents(&ctx.account).await.unwrap();
        assert_eq!(docs.len(), 1);
        assert_eq!(docs[0].id, "doc-1");
    }

    #[tokio::test]
    async fn get_document_returns_not_found_on_404() {
        let mut ctx = make_context().await;
        let _m = ctx
            .server
            .mock("GET", "/documents/missing")
            .with_status(404)
            .create_async()
            .await;

        let err = ctx
            .client
            .get_document(&ctx.account, "missing")
            .await
            .unwrap_err();
        assert!(matches!(err, ApiError::NotFound { .. }));
    }

    #[tokio::test]
    async fn create_document_sends_json_body() {
        let mut ctx = make_context().await;
        let resp_body = r##"{"id":"new-1","title":"Test","content":"# Hello","updated_at":"2026-01-01T00:00:00Z","sha256":null}"##;
        let _m = ctx
            .server
            .mock("POST", "/documents")
            .with_status(200)
            .with_header("content-type", "application/json")
            .with_body(resp_body)
            .create_async()
            .await;

        let doc = ctx
            .client
            .create_document(
                &ctx.account,
                CreateDocumentRequest {
                    title: "Test".into(),
                    content: "# Hello".into(),
                },
            )
            .await
            .unwrap();
        assert_eq!(doc.id, "new-1");
    }

    #[tokio::test]
    async fn update_document_sends_put() {
        let mut ctx = make_context().await;
        let resp_body = r##"{"id":"doc-1","title":"Test","content":"# Updated","updated_at":"2026-01-01T00:00:00Z","sha256":null}"##;
        let _m = ctx
            .server
            .mock("PUT", "/documents/doc-1")
            .with_status(200)
            .with_header("content-type", "application/json")
            .with_body(resp_body)
            .create_async()
            .await;

        let doc = ctx
            .client
            .update_document(
                &ctx.account,
                "doc-1",
                UpdateDocumentRequest {
                    title: None,
                    content: "# Updated".into(),
                },
            )
            .await
            .unwrap();
        assert_eq!(doc.content, "# Updated");
    }

    #[tokio::test]
    async fn delete_document_succeeds_on_204() {
        let mut ctx = make_context().await;
        let _m = ctx
            .server
            .mock("DELETE", "/documents/doc-1")
            .with_status(204)
            .create_async()
            .await;

        ctx.client
            .delete_document(&ctx.account, "doc-1")
            .await
            .unwrap();
    }

    /// Verify that a transient 503 is retried and the second 200 response succeeds.
    /// The test client is configured with `max_retries = 2` and a 10ms backoff base
    /// so the retry happens quickly without slowing the test suite.
    #[tokio::test]
    async fn retry_on_503_then_succeeds_on_200() {
        let mut ctx = make_context().await;
        let doc_body = r##"{"id":"doc-r","title":"Retry","content":"# Retry","updated_at":"2026-01-01T00:00:00Z","sha256":null}"##;

        // First request: 503 — should trigger retry.
        let _m503 = ctx
            .server
            .mock("GET", "/documents/doc-r")
            .with_status(503)
            .create_async()
            .await;

        // Second request: 200 with a valid body.
        let _m200 = ctx
            .server
            .mock("GET", "/documents/doc-r")
            .with_status(200)
            .with_header("content-type", "application/json")
            .with_body(doc_body)
            .create_async()
            .await;

        let doc = ctx
            .client
            .get_document(&ctx.account, "doc-r")
            .await
            .unwrap();
        assert_eq!(doc.id, "doc-r");
        assert_eq!(doc.title, "Retry");
    }

    /// A second 503 with `max_retries = 2` should exhaust retries and return an error.
    #[tokio::test]
    async fn retries_exhausted_returns_error() {
        let mut ctx = make_context().await;

        // Both mock requests return 503, exhausting the two allowed attempts.
        let _m1 = ctx
            .server
            .mock("GET", "/documents/doc-x")
            .with_status(503)
            .create_async()
            .await;
        let _m2 = ctx
            .server
            .mock("GET", "/documents/doc-x")
            .with_status(503)
            .create_async()
            .await;

        let err = ctx
            .client
            .get_document(&ctx.account, "doc-x")
            .await
            .unwrap_err();
        assert!(
            matches!(err, ApiError::RetriesExhausted { .. }),
            "expected RetriesExhausted, got {err:?}"
        );
    }
}
