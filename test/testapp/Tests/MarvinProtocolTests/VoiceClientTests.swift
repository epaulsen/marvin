import AVFoundation
import Foundation
import XCTest
@testable import MarvinProtocol
@testable import MarvinTestApp

@MainActor
final class VoiceClientTests: XCTestCase {
    func testResamplesStereoMicrophoneToPCM16Mono() throws {
        let format = AVAudioFormat(standardFormatWithSampleRate: 48_000, channels: 2)!
        let input = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 4_800)!
        input.frameLength = 4_800
        for channel in 0..<2 {
            for frame in 0..<4_800 {
                input.floatChannelData![channel][frame] = 0.25
            }
        }
        let output = try PCMInputConverter(inputFormat: format).convert(input)
        XCTAssertTrue((3_000...3_200).contains(output.count), "PCM length was \(output.count)")
        var decoder = PCM16Decoder()
        let samples = decoder.decode(output)
        XCTAssertTrue(samples.contains { $0 > 0.1 })
    }

    func testRecordsAndSendsAudioInProtocolOrderAndPlaysReply() async throws {
        let socket = TestSocket()
        let recorder = TestRecorder()
        let player = TestPlayer()
        let client = VoiceClient(recorder: recorder, player: player,
                                 requestMicrophone: { true }, createSocket: { _ in socket })

        client.connect()
        socket.enqueue(.string(#"{"type":"hello","audio_out":"pcm"}"#))
        try await waitUntil { client.connected }
        client.startRecording()
        try await waitUntil { client.recording }
        recorder.emit(Data([1, 2, 3, 4]))
        client.stopRecording()
        try await waitUntil { socket.sent.count == 4 && !client.busy }

        guard case .string(let start) = socket.sent[0],
              case .string(let wake) = socket.sent[1],
              case .data(let audio) = socket.sent[2],
              case .data(let silence) = socket.sent[3] else {
            return XCTFail("Unexpected message types")
        }
        XCTAssertEqual(start, VoiceProtocol.start)
        XCTAssertEqual(wake, VoiceProtocol.wake)
        XCTAssertEqual(audio, Data([1, 2, 3, 4]))
        XCTAssertEqual(silence, VoiceProtocol.silence)

        socket.enqueue(.string(#"{"type":"phase","value":"replying"}"#))
        socket.enqueue(.data(Data([0, 0, 255, 127])))
        try await waitUntil { player.received.count == 1 }
        XCTAssertEqual(player.received, [Data([0, 0, 255, 127])])
        client.disconnect()
    }

    func testDeniedMicrophoneDoesNotSendWake() async throws {
        let socket = TestSocket()
        let client = VoiceClient(recorder: TestRecorder(), player: TestPlayer(),
                                 requestMicrophone: { false }, createSocket: { _ in socket })
        client.connect()
        socket.enqueue(.string(#"{"type":"hello","audio_out":"pcm"}"#))
        try await waitUntil { client.connected }
        client.startRecording()
        try await waitUntil { !client.busy }
        XCTAssertFalse(client.recording)
        XCTAssertEqual(socket.sent.count, 1)
        client.disconnect()
    }

    func testConnectsToLocalGatewayWhenConfigured() async throws {
        guard let address = ProcessInfo.processInfo.environment["MARVIN_TEST_WEBSOCKET"] else {
            throw XCTSkip("Set MARVIN_TEST_WEBSOCKET to test a running local gateway")
        }
        let recorder = TestRecorder()
        let client = VoiceClient(recorder: recorder, player: TestPlayer(), requestMicrophone: { true })
        client.address = address
        client.connect()
        try await waitUntil { client.connected }
        client.startRecording()
        try await waitUntil { client.recording }
        recorder.emit(Data(repeating: 0, count: 1024))
        client.stopRecording()
        try await waitUntil { !client.busy }
        XCTAssertTrue(client.connected)
        XCTAssertTrue(client.events.contains("Opptak og stillhet sendt"))
        client.disconnect()
    }

    private func waitUntil(_ condition: () -> Bool) async throws {
        for _ in 0..<200 {
            if condition() { return }
            try await Task.sleep(nanoseconds: 5_000_000)
        }
        throw WaitError.timeout
    }
}

private enum WaitError: Error {
    case timeout
}

@MainActor
private final class TestSocket: VoiceWebSocket {
    private var queue: [URLSessionWebSocketTask.Message] = []
    private var waiting: CheckedContinuation<URLSessionWebSocketTask.Message, Error>?
    private(set) var sent: [URLSessionWebSocketTask.Message] = []

    func resume() {}

    func receive() async throws -> URLSessionWebSocketTask.Message {
        if !queue.isEmpty { return queue.removeFirst() }
        return try await withCheckedThrowingContinuation { waiting = $0 }
    }

    func send(_ message: URLSessionWebSocketTask.Message) async throws {
        sent.append(message)
    }

    func enqueue(_ message: URLSessionWebSocketTask.Message) {
        if let waiting {
            self.waiting = nil
            waiting.resume(returning: message)
        } else {
            queue.append(message)
        }
    }

    func cancel() {
        waiting?.resume(throwing: CancellationError())
        waiting = nil
    }
}

private final class TestRecorder: AudioRecording {
    private var onData: ((Data) -> Void)?

    func start(onData: @escaping (Data) -> Void, onError: @escaping (Error) -> Void) throws {
        self.onData = onData
    }

    func stop() { onData = nil }
    func emit(_ data: Data) { onData?(data) }
}

private final class TestPlayer: AudioPlaying {
    private(set) var received: [Data] = []
    func play(_ data: Data) throws { received.append(data) }
    func stop() {}
}
