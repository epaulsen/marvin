// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "MarvinTestApp",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "MarvinTestApp", targets: ["MarvinTestApp"])
    ],
    targets: [
        .target(name: "MarvinProtocol"),
        .executableTarget(name: "MarvinTestApp", dependencies: ["MarvinProtocol"]),
        .testTarget(name: "MarvinProtocolTests", dependencies: ["MarvinProtocol", "MarvinTestApp"])
    ]
)
