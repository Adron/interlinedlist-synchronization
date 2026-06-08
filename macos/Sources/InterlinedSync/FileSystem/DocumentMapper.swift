import Foundation

struct DocumentMapper: Sendable {
    let rootURL: URL

    private static let xattrKey = "com.interlinedlist.sync.documentID"

    /// Writes a document's body to disk as `<sanitized-title>.md` and stores the document ID
    /// as an extended attribute so stable lookup by ID is possible even after title renames.
    @discardableResult
    func write(document: DocumentDTO) throws -> URL {
        let fileURL = try resolveURL(for: document)
        try document.body.write(to: fileURL, atomically: true, encoding: .utf8)
        try setDocumentID(document.id, on: fileURL)
        return fileURL
    }

    /// Returns the document ID stored in the file's extended attribute, or nil if absent.
    func documentID(for fileURL: URL) -> String? {
        try? readDocumentID(from: fileURL)
    }

    /// Returns all `.md` files in `rootURL` that carry the sync xattr.
    func localDocuments() throws -> [(url: URL, id: String)] {
        let contents = try FileManager.default.contentsOfDirectory(
            at: rootURL, includingPropertiesForKeys: nil
        )
        return contents
            .filter { $0.pathExtension == "md" }
            .compactMap { url in
                guard let id = documentID(for: url) else { return nil }
                return (url: url, id: id)
            }
    }

    // MARK: - Private

    private func resolveURL(for document: DocumentDTO) throws -> URL {
        if let existingURL = try findExisting(id: document.id) {
            let targetURL = nonConflictingURL(for: document.title, skip: existingURL)
            if existingURL.resolvingSymlinksInPath().path != targetURL.resolvingSymlinksInPath().path {
                try FileManager.default.moveItem(at: existingURL, to: targetURL)
            }
            return targetURL
        }
        return nonConflictingURL(for: document.title, skip: nil)
    }

    private func findExisting(id: String) throws -> URL? {
        let contents = try FileManager.default.contentsOfDirectory(
            at: rootURL, includingPropertiesForKeys: nil
        )
        return contents
            .filter { $0.pathExtension == "md" }
            .first { documentID(for: $0) == id }
    }

    /// Returns a URL that does not conflict with existing files in `rootURL`.
    /// `skip` is a URL that will be moved away and should not count as a conflict.
    private func nonConflictingURL(for title: String, skip: URL?) -> URL {
        let base = sanitize(title)
        var candidate = rootURL.appendingPathComponent("\(base).md")
        // Resolve symlinks before comparing: macOS /var/folders is a symlink to /private/var/folders,
        // so contentsOfDirectory URLs and appendingPathComponent URLs may differ without resolution.
        let skipResolved = skip?.resolvingSymlinksInPath().path
        var counter = 2
        while FileManager.default.fileExists(atPath: candidate.path)
                && candidate.resolvingSymlinksInPath().path != skipResolved {
            candidate = rootURL.appendingPathComponent("\(base)-\(counter).md")
            counter += 1
        }
        return candidate
    }

    private func sanitize(_ title: String) -> String {
        let forbidden = CharacterSet(charactersIn: "/\\:*?\"<>|")
        var result = title
            .components(separatedBy: forbidden)
            .joined(separator: "-")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if result.isEmpty { result = "untitled" }
        return result.count <= 200 ? result : String(result.prefix(200))
    }

    private func setDocumentID(_ id: String, on fileURL: URL) throws {
        let data = Data(id.utf8)
        let rc = data.withUnsafeBytes { ptr -> Int32 in
            setxattr(fileURL.path, Self.xattrKey, ptr.baseAddress!, data.count, 0, 0)
        }
        guard rc == 0 else {
            throw SyncError.fileSystem(NSError(domain: NSPOSIXErrorDomain, code: Int(errno)))
        }
    }

    private func readDocumentID(from fileURL: URL) throws -> String {
        let size = getxattr(fileURL.path, Self.xattrKey, nil, 0, 0, 0)
        guard size > 0 else {
            throw SyncError.fileSystem(NSError(domain: NSPOSIXErrorDomain, code: Int(errno)))
        }
        var buffer = [UInt8](repeating: 0, count: size)
        let read = getxattr(fileURL.path, Self.xattrKey, &buffer, size, 0, 0)
        guard read == size, let id = String(bytes: buffer, encoding: .utf8) else {
            throw SyncError.mapping
        }
        return id
    }
}
