import Foundation

struct DocumentDTO: Codable, Sendable, Equatable {
    let id: String
    let title: String
    let content: String
    let folderId: String?
    let updatedAt: Date
    let contentHash: String?

    init(
        id: String,
        title: String,
        content: String,
        folderId: String?,
        updatedAt: Date,
        contentHash: String? = nil
    ) {
        self.id = id
        self.title = title
        self.content = content
        self.folderId = folderId
        self.updatedAt = updatedAt
        self.contentHash = contentHash
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(String.self, forKey: .id)
        title = try container.decode(String.self, forKey: .title)
        content = try container.decodeIfPresent(String.self, forKey: .content) ?? ""
        folderId = try container.decodeIfPresent(String.self, forKey: .folderId)
        updatedAt = try container.decode(Date.self, forKey: .updatedAt)
        contentHash = try container.decodeIfPresent(String.self, forKey: .contentHash)
    }
}

struct DocumentListResponse: Codable, Sendable {
    let documents: [DocumentDTO]
}

struct DocumentEnvelope: Codable, Sendable {
    let message: String?
    let document: DocumentDTO
}

struct DocumentUpdateRequest: Codable, Sendable {
    let title: String
    let content: String
}

struct DocumentDelta: Codable, Sendable, Equatable {
    let id: String
    let title: String
    let content: String?
    let folderId: String?
    let updatedAt: Date
    let deletedAt: Date?

    var isDeleted: Bool { deletedAt != nil }

    init(
        id: String,
        title: String,
        content: String?,
        folderId: String?,
        updatedAt: Date,
        deletedAt: Date?
    ) {
        self.id = id
        self.title = title
        self.content = content
        self.folderId = folderId
        self.updatedAt = updatedAt
        self.deletedAt = deletedAt
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(String.self, forKey: .id)
        title = try container.decodeIfPresent(String.self, forKey: .title) ?? ""
        content = try container.decodeIfPresent(String.self, forKey: .content)
        folderId = try container.decodeIfPresent(String.self, forKey: .folderId)
        updatedAt = try container.decode(Date.self, forKey: .updatedAt)
        deletedAt = try container.decodeIfPresent(Date.self, forKey: .deletedAt)
    }
}

struct DeltaResponse: Codable, Sendable, Equatable {
    let syncedAt: Date
    let documents: [DocumentDelta]

    enum CodingKeys: String, CodingKey {
        case syncedAt = "lastSyncAt"
        case documents
    }

    init(syncedAt: Date, documents: [DocumentDelta]) {
        self.syncedAt = syncedAt
        self.documents = documents
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        syncedAt = try container.decode(Date.self, forKey: .syncedAt)
        documents = try container.decodeIfPresent([DocumentDelta].self, forKey: .documents) ?? []
    }
}

extension JSONDecoder {
    static func interlinedList() -> JSONDecoder {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601WithFractionalSeconds
        return d
    }
}

extension JSONEncoder {
    static func interlinedList() -> JSONEncoder {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        return e
    }
}

extension JSONDecoder.DateDecodingStrategy {
    static let iso8601WithFractionalSeconds = custom { decoder in
        let container = try decoder.singleValueContainer()
        let raw = try container.decode(String.self)
        if let date = ISO8601DateFormatter.fractionalSeconds.date(from: raw)
            ?? ISO8601DateFormatter.standard.date(from: raw) {
            return date
        }
        throw DecodingError.dataCorruptedError(
            in: container,
            debugDescription: "Unrecognized ISO-8601 date: \(raw)"
        )
    }
}

private extension ISO8601DateFormatter {
    static let fractionalSeconds: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    static let standard: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()
}
