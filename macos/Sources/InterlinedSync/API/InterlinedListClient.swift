import Foundation

protocol DocumentFetching: Sendable {
    func fetchDocuments() async throws -> [DocumentDTO]
    func fetchDelta(since: Date?) async throws -> DeltaResponse
}

protocol DocumentMutating: Sendable {
    func updateDocument(id: String, update: DocumentUpdateRequest) async throws -> DocumentDTO
    func createDocument(_ update: DocumentUpdateRequest) async throws -> DocumentDTO
    func deleteDocument(id: String) async throws
}

actor InterlinedListClient: DocumentFetching, DocumentMutating {
    private let baseURL: URL
    private let session: URLSession
    private let tokenStorage: TokenStorage
    private let decoder = JSONDecoder.interlinedList()
    private static let keychainAccount = KeychainManager.sessionTokenAccount
    private static let isoFormatter = ISO8601DateFormatter()

    init(baseURL: URL, session: URLSession, tokenStorage: TokenStorage) {
        self.baseURL = baseURL
        self.session = session
        self.tokenStorage = tokenStorage
    }

    func fetchDocuments() async throws -> [DocumentDTO] {
        let request = try authenticatedRequest(path: "/api/documents", method: "GET")
        let response: DocumentListResponse = try await perform(request)
        return response.documents
    }

    func fetchDelta(since: Date?) async throws -> DeltaResponse {
        let request = try authenticatedRequest(
            path: "/api/documents/sync",
            method: "GET",
            query: since.map { [URLQueryItem(name: "lastSyncAt", value: Self.isoFormatter.string(from: $0))] }
        )
        return try await perform(request)
    }

    func updateDocument(id: String, update: DocumentUpdateRequest) async throws -> DocumentDTO {
        var request = try authenticatedRequest(path: "/api/documents/\(id)", method: "PATCH")
        try attachJSON(update, to: &request)
        let envelope: DocumentEnvelope = try await perform(request)
        return envelope.document
    }

    func createDocument(_ update: DocumentUpdateRequest) async throws -> DocumentDTO {
        var request = try authenticatedRequest(path: "/api/documents", method: "POST")
        try attachJSON(update, to: &request)
        let envelope: DocumentEnvelope = try await perform(request)
        return envelope.document
    }

    func deleteDocument(id: String) async throws {
        let request = try authenticatedRequest(path: "/api/documents/\(id)", method: "DELETE")
        try await performDelete(request)
    }

    // MARK: - Private

    private func authenticatedRequest(
        path: String,
        method: String,
        query: [URLQueryItem]? = nil
    ) throws -> URLRequest {
        let token: String
        do {
            token = try tokenStorage.load(for: Self.keychainAccount)
        } catch {
            throw SyncError.notAuthenticated
        }
        let url = try resolveURL(path: path, query: query)
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        return request
    }

    private func resolveURL(path: String, query: [URLQueryItem]?) throws -> URL {
        var trimmed = baseURL.absoluteString
        while trimmed.hasSuffix("/") { trimmed.removeLast() }
        let suffix = path.hasPrefix("/") ? path : "/" + path
        guard let base = URL(string: trimmed + suffix) else { throw SyncError.mapping }
        guard let query, !query.isEmpty else { return base }
        guard var components = URLComponents(url: base, resolvingAgainstBaseURL: false) else {
            throw SyncError.mapping
        }
        components.queryItems = query
        guard let url = components.url else { throw SyncError.mapping }
        return url
    }

    private func attachJSON<T: Encodable>(_ body: T, to request: inout URLRequest) throws {
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        do {
            request.httpBody = try JSONEncoder.interlinedList().encode(body)
        } catch {
            throw SyncError.mapping
        }
    }

    private func perform<T: Decodable>(_ request: URLRequest) async throws -> T {
        let (data, _) = try await send(request)
        do {
            return try decoder.decode(T.self, from: data)
        } catch {
            throw SyncError.mapping
        }
    }

    private func performDelete(_ request: URLRequest) async throws {
        let data: Data
        let urlResponse: URLResponse
        do {
            (data, urlResponse) = try await session.data(for: request)
        } catch {
            throw SyncError.network(error)
        }
        guard let http = urlResponse as? HTTPURLResponse else {
            throw SyncError.network(URLError(.badServerResponse))
        }
        switch http.statusCode {
        case 200...299, 404, 410:
            _ = data
        case 401:
            throw SyncError.authExpired
        case 403:
            throw SyncError.notAuthenticated
        case 429:
            throw SyncError.rateLimited(retryAfter: Self.retryAfter(from: http))
        default:
            throw SyncError.network(URLError(.badServerResponse))
        }
    }

    private func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let data: Data
        let urlResponse: URLResponse
        do {
            (data, urlResponse) = try await session.data(for: request)
        } catch {
            throw SyncError.network(error)
        }
        guard let http = urlResponse as? HTTPURLResponse else {
            throw SyncError.network(URLError(.badServerResponse))
        }
        switch http.statusCode {
        case 200...299:
            return (data, http)
        case 401:
            throw SyncError.authExpired
        case 403:
            throw SyncError.notAuthenticated
        case 429:
            throw SyncError.rateLimited(retryAfter: Self.retryAfter(from: http))
        default:
            throw SyncError.network(URLError(.badServerResponse))
        }
    }

    /// Parses a `Retry-After` header as either an integer number of seconds or an HTTP-date,
    /// returning the delay in seconds. Returns nil when the header is absent or unparseable so the
    /// caller can fall back to exponential backoff.
    static func retryAfter(from response: HTTPURLResponse) -> TimeInterval? {
        guard let raw = response.value(forHTTPHeaderField: "Retry-After")?
            .trimmingCharacters(in: .whitespaces), !raw.isEmpty else {
            return nil
        }
        if let seconds = TimeInterval(raw) {
            return max(0, seconds)
        }
        if let date = httpDateFormatter.date(from: raw) {
            return max(0, date.timeIntervalSinceNow)
        }
        return nil
    }

    private static let httpDateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "GMT")
        formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        return formatter
    }()
}
