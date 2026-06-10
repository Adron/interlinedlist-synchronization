use std::collections::HashMap;
use std::sync::Mutex;

use anyhow::Result;
use async_trait::async_trait;
use chrono::Utc;

use crate::{
    ApiClientTrait, ApiError, CreateDocumentRequest, Document, DocumentSummary,
    UpdateDocumentRequest,
};

/// In-memory ApiClient stub for use in sync-engine unit tests.
pub struct MockApiClient {
    pub documents: Mutex<HashMap<String, Document>>,
    pub login_token: String,
    pub fail_next: Mutex<Option<ApiError>>,
}

impl MockApiClient {
    pub fn new() -> Self {
        Self {
            documents: Mutex::new(HashMap::new()),
            login_token: "mock-token".to_string(),
            fail_next: Mutex::new(None),
        }
    }

    pub fn seed(&self, doc: Document) {
        self.documents.lock().unwrap().insert(doc.id.clone(), doc);
    }

    pub fn fail_once(&self, err: ApiError) {
        *self.fail_next.lock().unwrap() = Some(err);
    }

    fn take_failure(&self) -> Option<ApiError> {
        self.fail_next.lock().unwrap().take()
    }
}

impl Default for MockApiClient {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait]
impl ApiClientTrait for MockApiClient {
    async fn login(&self, _username: &str, _password: &str) -> Result<String, ApiError> {
        if let Some(e) = self.take_failure() {
            return Err(e);
        }
        Ok(self.login_token.clone())
    }

    async fn list_documents(&self, _account: &str) -> Result<Vec<DocumentSummary>, ApiError> {
        if let Some(e) = self.take_failure() {
            return Err(e);
        }
        let docs = self.documents.lock().unwrap();
        Ok(docs
            .values()
            .map(|d| DocumentSummary {
                id: d.id.clone(),
                title: d.title.clone(),
                updated_at: d.updated_at,
                sha256: d.sha256.clone(),
            })
            .collect())
    }

    async fn get_document(&self, _account: &str, id: &str) -> Result<Document, ApiError> {
        if let Some(e) = self.take_failure() {
            return Err(e);
        }
        self.documents
            .lock()
            .unwrap()
            .get(id)
            .cloned()
            .ok_or_else(|| ApiError::NotFound {
                resource: id.to_string(),
            })
    }

    async fn create_document(
        &self,
        _account: &str,
        req: CreateDocumentRequest,
    ) -> Result<Document, ApiError> {
        if let Some(e) = self.take_failure() {
            return Err(e);
        }
        let id = format!("mock-{}", uuid_simple());
        let doc = Document {
            id: id.clone(),
            title: req.title,
            content: req.content,
            updated_at: Utc::now(),
            sha256: None,
        };
        self.documents.lock().unwrap().insert(id, doc.clone());
        Ok(doc)
    }

    async fn update_document(
        &self,
        _account: &str,
        id: &str,
        req: UpdateDocumentRequest,
    ) -> Result<Document, ApiError> {
        if let Some(e) = self.take_failure() {
            return Err(e);
        }
        let mut docs = self.documents.lock().unwrap();
        let doc = docs.get_mut(id).ok_or_else(|| ApiError::NotFound {
            resource: id.to_string(),
        })?;
        if let Some(title) = req.title {
            doc.title = title;
        }
        doc.content = req.content;
        doc.updated_at = Utc::now();
        Ok(doc.clone())
    }

    async fn delete_document(&self, _account: &str, id: &str) -> Result<(), ApiError> {
        if let Some(e) = self.take_failure() {
            return Err(e);
        }
        self.documents.lock().unwrap().remove(id);
        Ok(())
    }
}

fn uuid_simple() -> String {
    // Minimal unique ID using timestamp + pointer address — sufficient for tests.
    format!(
        "{:x}",
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .subsec_nanos()
    )
}
