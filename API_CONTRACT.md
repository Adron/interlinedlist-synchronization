# InterlinedList REST API Contract

This document is a snapshot of the InterlinedList REST API as validated by live integration tests
run against `interlinedlist.com` on **2026-06-22**. It exists so the three sync clients have a
single reference they can diff against when the live API drifts — rather than depending on the
live `/help/api` page.

## Base URL

```
https://interlinedlist.com/api
```

All paths below are relative to this base. All requests and responses use `application/json`.
All communication must be over HTTPS.

## Authentication

### Obtain a sync token

```
POST /auth/sync-token
```

**Request body:**

```json
{
  "email": "you@example.com",
  "password": "your-password"
}
```

**Response (200):**

```json
{
  "token": "il_tok_...",
  "message": "Sync token created."
}
```

The returned token is a long-lived Bearer token. It does not expire under normal circumstances
and does not require a refresh flow.

**Error responses:**

| Status | Meaning |
|--------|---------|
| 400 | Missing or malformed request body |
| 401 | Invalid credentials |

### Using the token

All subsequent requests include the token as an HTTP `Authorization` header:

```
Authorization: Bearer il_tok_...
```

> **Note:** A session-cookie login endpoint (`POST /api/auth/login`) also exists on the web
> application but is intended for browser sessions. Native sync clients must use
> `POST /auth/sync-token` and the Bearer header — not session cookies.

## Document endpoints

### List documents

```
GET /documents
```

**Response (200):**

```json
{
  "documents": [
    {
      "id": "abc123",
      "title": "My Document",
      "updatedAt": "2026-06-20T14:00:00.000Z",
      "contentHash": "sha256hexstring",
      "folderId": null
    }
  ]
}
```

The list response returns `DocumentSummary` objects — no `content` field. Fetch the full document
separately to retrieve content.

### Get a document

```
GET /documents/{id}
```

**Response (200):**

```json
{
  "id": "abc123",
  "title": "My Document",
  "content": "# Hello\n\nDocument body.",
  "updatedAt": "2026-06-20T14:00:00.000Z",
  "contentHash": "sha256hexstring",
  "folderId": null
}
```

**Error responses:** 401 (bad token), 404 (not found).

### Create a document

```
POST /documents
```

**Request body:**

```json
{
  "title": "My New Document",
  "content": "# Hello\n\nInitial content."
}
```

**Response (200):**

```json
{
  "document": {
    "id": "newid",
    "title": "My New Document",
    "content": "# Hello\n\nInitial content.",
    "updatedAt": "2026-06-22T10:00:00.000Z",
    "contentHash": "sha256hexstring",
    "folderId": null
  },
  "message": "created"
}
```

The response wraps the document under a `"document"` key, not at the root.

### Update a document

```
PATCH /documents/{id}
```

> **Note:** The verb is `PATCH`, not `PUT`. Sending `PUT` returns an error.

**Request body:**

```json
{
  "title": "Updated Title",
  "content": "# Updated\n\nNew content."
}
```

`title` is optional in the request body; `content` is required.

**Response (200):** same `{ "document": { ... }, "message": "updated" }` envelope as create.

### Delete a document

```
DELETE /documents/{id}
```

**Success responses:** 200, 202, or 204 (all treated as success by clients).

**Error responses:** 401 (bad token), 404 (not found — treated as success by clients since the
document is already gone).

## Delta sync endpoint

```
GET /documents/sync
GET /documents/sync?lastSyncAt=<ISO-8601 timestamp>
```

Returns documents changed since `lastSyncAt`. Omit the parameter on first sync to receive all
documents.

**Response (200):**

```json
{
  "lastSyncAt": "2026-06-22T10:05:00.000Z",
  "documents": [
    {
      "id": "abc123",
      "title": "My Document",
      "content": "# Hello",
      "folderId": null,
      "updatedAt": "2026-06-22T09:55:00.000Z",
      "deletedAt": null
    }
  ],
  "folders": []
}
```

**Field notes:**

- `lastSyncAt` — ISO-8601 timestamp the client should store and pass as `lastSyncAt` on the next
  poll to receive only changes since this point.
- `documents` — array of delta entries. Each entry may include `deletedAt`.
- `folders` — array of folder objects changed since `lastSyncAt` (same structure as folder
  list endpoint).
- `deletedAt` — when non-null, the document was deleted. Clients should remove the local file
  (or move it to trash) and purge the entry from their state store.

### Tombstone behavior

> **Note:** As validated by integration tests on 2026-06-22, the live API does **not** reliably
> include `deletedAt` tombstones in delta responses. Deleted documents are typically absent from
> subsequent delta responses rather than appearing with `deletedAt` set. Clients must treat
> absence from the delta (after having seen the document previously) as a potential deletion
> signal, not rely solely on `deletedAt`. A full list fetch (`GET /documents`) every N cycles
> is the safe way to detect deletions definitively.

## Field reference

| Field | Type | Present in | Description |
|-------|------|-----------|-------------|
| `id` | string | All responses | Stable server-assigned document identifier. Persists across renames. |
| `title` | string | All responses | Human-readable document title. Used as the local filename base (with `.md` extension). |
| `content` | string | GET /documents/{id}, create/update responses, delta | Full Markdown document body. |
| `contentHash` | string or null | List, get, delta | SHA-256 hex digest of `content`. Null when the server has not computed it. |
| `folderId` | string or null | All responses | ID of the parent folder, or null for root-level documents. |
| `updatedAt` | ISO-8601 string | All responses | Last modification timestamp (UTC). |
| `createdAt` | ISO-8601 string | Some responses | Creation timestamp (UTC). Not present in all response shapes. |
| `deletedAt` | ISO-8601 string or null | Delta only | Non-null when the document was deleted. See tombstone note above. |
| `relativePath` | string or null | Some responses | Server-suggested local relative path. Behavior not fully specified; treat as advisory. |
| `userId` | string | Some responses | ID of the owning user account. |
| `isPublic` | boolean | Some responses | Whether the document is publicly visible on the web app. |

## Folder endpoints

### List folders

```
GET /documents/folders
```

**Response (200):**

```json
{
  "folders": [
    {
      "id": "folder-1",
      "name": "Project Notes",
      "parentId": null,
      "createdAt": "2026-05-01T08:00:00.000Z"
    }
  ]
}
```

### Create a folder

```
POST /documents/folders
```

**Request body:**

```json
{
  "name": "Project Notes",
  "parentId": null
}
```

**Response (200):** `{ "folder": { "id": "...", "name": "...", "parentId": null, "createdAt": "..." }, "message": "created" }`

### Update a folder

```
PATCH /documents/folders/{id}
```

**Request body:** `{ "name": "Renamed Folder" }` (partial update).

### Delete a folder

```
DELETE /documents/folders/{id}
```

Success responses: 200, 202, 204.

## Known divergences and limitations

| Issue | Detail |
|-------|--------|
| Tombstones unreliable | The `/documents/sync` delta endpoint does not consistently return `deletedAt` tombstones. Clients fall back to detecting deletions by comparing full list responses. |
| `relativePath` semantics unclear | The `relativePath` field appears in some responses but its exact server behavior (path-within-folder vs. historical artifact) is unspecified. Do not rely on it for path computation. |
| `PUT` not supported for updates | Use `PATCH /documents/{id}`. `PUT` is not a valid verb for document updates on this API. |
| `POST /api/auth/login` is session-only | This endpoint returns a session cookie for the web app, not a Bearer token. Native clients must use `POST /auth/sync-token`. |
| No webhook or WebSocket push | The server does not support server-push for remote changes. Clients must poll. The minimum recommended poll interval is 30 seconds. |
| Rate limiting | HTTP 429 responses include a `Retry-After` header (seconds). Clients must honor it with exponential backoff. |

---

*Validated against `interlinedlist.com` on 2026-06-22.*
*Re-validate this document whenever a platform's integration test suite is updated.*
