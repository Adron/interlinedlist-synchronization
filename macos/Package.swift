// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "InterlinedSync",
    platforms: [
        .macOS(.v13)
    ],
    targets: [
        .executableTarget(
            name: "InterlinedSync",
            path: "Sources/InterlinedSync",
            resources: [
                .process("Resources")
            ]
        ),
        .testTarget(
            name: "InterlinedSyncTests",
            dependencies: ["InterlinedSync"],
            path: "Tests/InterlinedSyncTests"
        )
    ]
)
