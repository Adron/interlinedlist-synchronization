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
    private static let keychainAccount = "session-token"
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
        return try await perform(request)
    }

    func createDocument(_ update: DocumentUpdateRequest) async throws -> DocumentDTO {
        var request = try authenticatedRequest(path: "/api/documents", method: "POST")
        try attachJSON(update, to: &request)
        return try await perform(request)
    }

    func deleteDocument(id: String) async throws {
        let request = try authenticatedRequest(path: "/api/documents/\(id)", method: "DELETE")
        try await performVoid(request)
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
        let base = baseURL.appendingPathComponent(path)
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

    private func performVoid(_ request: URLRequest) async throws {
        _ = try await send(request)
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
        case 401, 403:
            throw SyncError.notAuthenticated
        default:
            throw SyncError.network(URLError(.badServerResponse))
        }
    }
}
