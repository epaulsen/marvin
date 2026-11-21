import AVFoundation
import Foundation
import MarvinProtocol

@MainActor
protocol VoiceWebSocket: AnyObject {
    func resume()
    func receive() async throws -> URLSessionWebSocketTask.Message
    func send(_ message: URLSessionWebSocketTask.Message) async throws
    func cancel()
}

@MainActor
final class URLVoiceWebSocket: VoiceWebSocket {
    private let task: URLSessionWebSocketTask

    init(url: URL) {
        task = URLSession.shared.webSocketTask(with: url)
    }

    func resume() { task.resume() }
    func receive() async throws -> URLSessionWebSocketTask.Message { try await task.receive() }
    func send(_ message: URLSessionWebSocketTask.Message) async throws { try await task.send(message) }
    func cancel() { task.cancel(with: .normalClosure, reason: nil) }
}

@MainActor
final class VoiceClient: ObservableObject {
    @Published var address = "ws://localhost:8080/voice"
    @Published private(set) var connected = false
    @Published private(set) var connecting = false
    @Published private(set) var recording = false
    @Published private(set) var busy = false
    @Published private(set) var status = "Ikke tilkoblet"
    @Published private(set) var events: [String] = []

    private let recorder: AudioRecording
    private let player: AudioPlaying
    private let requestMicrophone: () async -> Bool
    private let createSocket: @MainActor (URL) -> any VoiceWebSocket
    private var socket: (any VoiceWebSocket)?
    private var receiver: Task<Void, Never>?
    private var sender: Task<Void, Never>?
    private var limitTimer: Task<Void, Never>?
    private var audioContinuation: AsyncStream<Data>.Continuation?
    private var recorderStarted = false

    init(
        recorder: AudioRecording = AudioRecorder(),
        player: AudioPlaying = AudioPlayer(),
        requestMicrophone: @escaping () async -> Bool = { await AVCaptureDevice.requestAccess(for: .audio) },
        createSocket: @escaping @MainActor (URL) -> any VoiceWebSocket = { URLVoiceWebSocket(url: $0) }
    ) {
        self.recorder = recorder
        self.player = player
        self.requestMicrophone = requestMicrophone
        self.createSocket = createSocket
    }

    func connect() {
        guard !connecting, !connected,
              let url = URL(string: address),
              ["ws", "wss"].contains(url.scheme?.lowercased() ?? ""),
              url.host != nil else {
            status = "Oppgi en gyldig ws://- eller wss://-adresse."
            return
        }
        let connection = createSocket(url)
        socket = connection
        connecting = true
        status = "Kobler til ..."
        connection.resume()
        receiver = Task { [weak self] in
            await self?.receive(from: connection)
        }
    }

    func disconnect() {
        limitTimer?.cancel()
        if recorderStarted {
            recorder.stop()
            recorderStarted = false
        }
        audioContinuation?.finish()
        audioContinuation = nil
        sender?.cancel()
        sender = nil
        receiver?.cancel()
        receiver = nil
        socket?.cancel()
        socket = nil
        player.stop()
        connected = false
        connecting = false
        recording = false
        busy = false
        status = "Ikke tilkoblet"
        log("Frakoblet")
    }

    func startRecording() {
        guard connected, !busy, !recording, let connection = socket else { return }
        busy = true
        Task { [self, connection] in
            let allowed = await requestMicrophone()
            guard socket === connection else { return }
            guard allowed else {
                busy = false
                status = "Mikrofontilgang ble avslått."
                return
            }

            do {
                player.stop()
                try await connection.send(.string(VoiceProtocol.wake))
                guard socket === connection else { return }
                log("Sendte wake")

                var continuation: AsyncStream<Data>.Continuation!
                let frames = AsyncStream<Data>(bufferingPolicy: .bufferingOldest(128)) {
                    continuation = $0
                }
                audioContinuation = continuation
                try recorder.start(onData: { [weak self, weak connection] data in
                    if case .dropped = continuation.yield(data) {
                        Task { @MainActor in
                            guard let self, self.socket === connection else { return }
                            self.fail("Nettverket er for tregt til å sende lyd.")
                        }
                    }
                }, onError: { [weak self, weak connection] error in
                    Task { @MainActor in
                        guard let self, self.socket === connection else { return }
                        self.fail(error.localizedDescription)
                    }
                })
                recorderStarted = true
                recording = true
                busy = false
                status = "Tar opp ..."
                log("Sender mikrofon som 16 kHz PCM16 mono")

                sender = Task { [weak self] in
                    do {
                        for await frame in frames {
                            try Task.checkCancellation()
                            try await connection.send(.data(frame))
                        }
                        guard let self, self.socket === connection else { return }
                        self.sender = nil
                        self.busy = false
                        self.status = "Venter på svar ..."
                        self.log("Opptak og stillhet sendt")
                    } catch {
                        guard let self, self.socket === connection else { return }
                        self.fail(error.localizedDescription)
                    }
                }
                limitTimer = Task { [weak self] in
                    try? await Task.sleep(nanoseconds: 55_000_000_000)
                    guard !Task.isCancelled else { return }
                    self?.stopRecording()
                }
            } catch {
                guard socket === connection else { return }
                fail(error.localizedDescription)
            }
        }
    }

    func stopRecording() {
        guard recording, recorderStarted, let continuation = audioContinuation else { return }
        limitTimer?.cancel()
        recorder.stop()
        recorderStarted = false
        recording = false
        busy = true
        status = "Avslutter opptak ..."
        audioContinuation = nil
        if case .dropped = continuation.yield(VoiceProtocol.silence) {
            fail("Nettverket er for tregt til å sende lyd.")
            return
        }
        continuation.finish()
    }

    private func receive(from connection: any VoiceWebSocket) async {
        do {
            while !Task.isCancelled {
                let message = try await connection.receive()
                guard socket === connection else { return }
                switch message {
                case .string(let text):
                    let control = try JSONDecoder().decode(VoiceProtocol.Message.self, from: Data(text.utf8))
                    switch control.type {
                    case "hello":
                        guard !connected, control.audioOut == "pcm" else {
                            throw ClientError.unexpectedHello
                        }
                        log("Mottok hello")
                        try await connection.send(.string(VoiceProtocol.start))
                        guard socket === connection else { return }
                        connected = true
                        connecting = false
                        status = "Tilkoblet"
                        log("Sendte start")
                    case "phase":
                        let phase = control.value ?? "ukjent"
                        status = "Fase: \(phase)"
                        log("Mottok fase: \(phase)")
                        if phase == "replying" {
                            player.stop()
                        }
                    case "error":
                        status = "Gatewayen meldte feil."
                        log("Mottok feil fra gatewayen")
                    default:
                        log("Mottok ukjent kontrollmelding")
                    }
                case .data(let data):
                    try player.play(data)
                @unknown default:
                    break
                }
            }
        } catch {
            guard socket === connection else { return }
            fail(error.localizedDescription)
        }
    }

    private func fail(_ message: String) {
        disconnect()
        status = "Feil: \(message)"
        log(status)
    }

    private func log(_ message: String) {
        events.append(message)
        if events.count > 100 {
            events.removeFirst(events.count - 100)
        }
    }
}

private enum ClientError: LocalizedError {
    case unexpectedHello

    var errorDescription: String? {
        "Gatewayen sendte en uventet hello-melding eller et annet lydformat enn PCM."
    }
}
