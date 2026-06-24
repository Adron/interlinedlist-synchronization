import Foundation

struct DocumentDTO: Codable, Sendable, Equatable {
    let id: String
    let title: String
    let content: String
    let folderId: String?
    let updatedAt: Date
}

struct DocumentListResponse: Codable, Sendable {
    let documents: [DocumentDTO]
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
    let deleted: Bool

    init(
        id: String,
        title: String,
        content: String?,
        folderId: String?,
        updatedAt: Date,
        deleted: Bool
    ) {
        self.id = id
        self.title = title
        self.content = content
        self.folderId = folderId
        self.updatedAt = updatedAt
        self.deleted = deleted
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(String.self, forKey: .id)
        title = try container.decode(String.self, forKey: .title)
        content = try container.decodeIfPresent(String.self, forKey: .content)
        folderId = try container.decodeIfPresent(String.self, forKey: .folderId)
        updatedAt = try container.decode(Date.self, forKey: .updatedAt)
        deleted = try container.decodeIfPresent(Bool.self, forKey: .deleted) ?? false
    }
}

struct DeltaResponse: Codable, Sendable, Equatable {
    let syncedAt: Date
    let documents: [DocumentDelta]
}

extension JSONDecoder {
    static func interlinedList() -> JSONDecoder {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        // d.keyDecodingStrategy = .convertFromSnakeCase
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
