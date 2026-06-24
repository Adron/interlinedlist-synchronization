import Foundation
@testable import InterlinedSync

/// An in-memory stand-in for the InterlinedList REST API, wired through `MockURLProtocol`.
/// Routes `GET/POST/PUT/DELETE /api/documents[/{id}]`, keeps a document store, and records
/// per-verb call counts for assertions. Thread-safe because the handler runs on URLSession's queue.
final class FakeServer: @unchecked Sendable {
    private let lock = NSLock()
    private var store: [String: DocumentDTO] = [:]
    private var nextID = 1

    private(set) var createCount = 0
    private(set) var updateCount = 0
    private(set) var deleteCount = 0
    private(set) var fetchCount = 0
    var failNextFetch = false
    var unauthorizedNextFetch = false
    var rateLimitNextFetchCount = 0
    var rateLimitRetryAfter: String?

    private var delta: DeltaResponse?
    private(set) var lastDeltaSince: String??
    private(set) var deltaFetchCount = 0

    private let encoder = JSONEncoder.interlinedList()
    private let decoder = JSONDecoder.interlinedList()

    var documents: [String: DocumentDTO] {
        lock.lock(); defer { lock.unlock() }
        return store
    }

    func seed(_ document: DocumentDTO) {
        lock.lock(); defer { lock.unlock() }
        store[document.id] = document
    }

    func remove(id: String) {
        lock.lock(); defer { lock.unlock() }
        store[id] = nil
    }

    func update(id: String, body: String, updatedAt: Date) {
        lock.lock(); defer { lock.unlock() }
        guard let existing = store[id] else { return }
        store[id] = DocumentDTO(
            id: id, title: existing.title, content: body, folderId: existing.folderId, updatedAt: updatedAt
        )
    }

    func stageDelta(_ delta: DeltaResponse) {
        lock.lock(); defer { lock.unlock() }
        self.delta = delta
    }

    func handler() -> (URLRequest) throws -> (HTTPURLResponse, Data) {
        { [weak self] request in
            guard let self else { throw URLError(.badServerResponse) }
            return try self.route(request)
        }
    }

    private func route(_ request: URLRequest) throws -> (HTTPURLResponse, Data) {
        lock.lock(); defer { lock.unlock() }

        let method = request.httpMethod ?? "GET"
        let path = request.url?.path ?? ""
        let idComponent = path.hasPrefix("/api/documents/")
            ? String(path.dropFirst("/api/documents/".count))
            : nil

        if method == "GET" && path == "/api/documents/sync" {
            return try routeDelta(request)
        }

        switch (method, idComponent) {
        case ("GET", _):
            fetchCount += 1
            if failNextFetch {
                failNextFetch = false
                return (response(request, 500), Data())
            }
            if unauthorizedNextFetch {
                unauthorizedNextFetch = false
                return (response(request, 401), Data())
            }
            if rateLimitNextFetchCount > 0 {
                rateLimitNextFetchCount -= 1
                let headers = rateLimitRetryAfter.map { ["Retry-After": $0] }
                return (response(request, 429, headers: headers), Data())
            }
            let payload = DocumentListResponse(documents: Array(store.values))
            return (response(request, 200), try encoder.encode(payload))

        case ("POST", _):
            createCount += 1
            let body = readBody(request)
            let req = try decoder.decode(DocumentUpdateRequest.self, from: body)
            let id = "server-\(nextID)"
            nextID += 1
            let created = DocumentDTO(
                id: id, title: req.title, content: req.content, folderId: nil, updatedAt: Date()
            )
            store[id] = created
            return (response(request, 201), try encoder.encode(DocumentEnvelope(message: "Created", document: created)))

        case let ("PATCH", id?):
            updateCount += 1
            let body = readBody(request)
            let req = try decoder.decode(DocumentUpdateRequest.self, from: body)
            let updated = DocumentDTO(
                id: id, title: req.title, content: req.content, folderId: nil, updatedAt: Date()
            )
            store[id] = updated
            return (response(request, 200), try encoder.encode(DocumentEnvelope(message: "Updated", document: updated)))

        case let ("DELETE", id?):
            deleteCount += 1
            store[id] = nil
            return (response(request, 200), Data())

        default:
            return (response(request, 404), Data())
        }
    }

    private func routeDelta(_ request: URLRequest) throws -> (HTTPURLResponse, Data) {
        if failNextFetch {
            failNextFetch = false
            return (response(request, 500), Data())
        }
        deltaFetchCount += 1
        let since = URLComponents(url: request.url!, resolvingAgainstBaseURL: false)?
            .queryItems?.first(where: { $0.name == "lastSyncAt" })?.value
        lastDeltaSince = .some(since)
        let payload = delta ?? DeltaResponse(syncedAt: Date(), documents: [])
        return (response(request, 200), try encoder.encode(payload))
    }

    private func readBody(_ request: URLRequest) -> Data {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return Data() }
        var data = Data()
        let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: 4096)
        defer { buffer.deallocate() }
        stream.open()
        while stream.hasBytesAvailable {
            let read = stream.read(buffer, maxLength: 4096)
            if read > 0 { data.append(buffer, count: read) }
        }
        stream.close()
        return data
    }

    private func response(
        _ request: URLRequest,
        _ status: Int,
        headers: [String: String]? = nil
    ) -> HTTPURLResponse {
        HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: headers)!
    }
}
