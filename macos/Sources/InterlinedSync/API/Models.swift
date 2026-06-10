import Foundation

// TODO: Verify field names and list-response envelope against live API before shipping.
// Assumes camelCase keys and ISO 8601 dates. If the API uses snake_case, uncomment
// keyDecodingStrategy = .convertFromSnakeCase in JSONDecoder.interlinedList().

struct DocumentDTO: Codable, Sendable, Equatable {
    let id: String
    let title: String
    let body: String
    let updatedAt: Date
}

struct DocumentListResponse: Codable, Sendable {
    let documents: [DocumentDTO]
}

struct DocumentUpdateRequest: Codable, Sendable {
    let title: String
    let body: String
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
