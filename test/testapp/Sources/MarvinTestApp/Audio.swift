import AVFoundation
import Foundation
import MarvinProtocol

enum AudioError: LocalizedError {
    case unavailable
    case conversion

    var errorDescription: String? {
        switch self {
        case .unavailable: return "Lydformatet eller mikrofonen er ikke tilgjengelig."
        case .conversion: return "Kunne ikke konvertere mikrofonens lyd."
        }
    }
}

protocol AudioRecording {
    func start(onData: @escaping (Data) -> Void, onError: @escaping (Error) -> Void) throws
    func stop()
}

protocol AudioPlaying {
    func play(_ data: Data) throws
    func stop()
}

final class PCMInputConverter {
    private let inputFormat: AVAudioFormat
    private let outputFormat: AVAudioFormat
    private let converter: AVAudioConverter

    init(inputFormat: AVAudioFormat) throws {
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0,
              let outputFormat = AVAudioFormat(
                commonFormat: .pcmFormatInt16,
                sampleRate: Double(VoiceProtocol.inputSampleRate),
                channels: 1,
                interleaved: false),
              let converter = AVAudioConverter(from: inputFormat, to: outputFormat) else {
            throw AudioError.unavailable
        }
        converter.primeMethod = .none
        self.inputFormat = inputFormat
        self.outputFormat = outputFormat
        self.converter = converter
    }

    func convert(_ buffer: AVAudioPCMBuffer) throws -> Data {
        let capacity = AVAudioFrameCount(
            ceil(Double(buffer.frameLength) * outputFormat.sampleRate / inputFormat.sampleRate) + 32)
        guard let output = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: capacity) else {
            throw AudioError.conversion
        }
        var offset: AVAudioFrameCount = 0
        var allocationFailed = false
        var error: NSError?
        let status = converter.convert(to: output, error: &error) { requested, inputStatus in
            if offset >= buffer.frameLength {
                inputStatus.pointee = .noDataNow
                return nil
            }
            let count = min(requested, buffer.frameLength - offset)
            guard let chunk = AVAudioPCMBuffer(pcmFormat: self.inputFormat, frameCapacity: count) else {
                allocationFailed = true
                inputStatus.pointee = .noDataNow
                return nil
            }
            chunk.frameLength = count
            let source = UnsafeMutableAudioBufferListPointer(buffer.mutableAudioBufferList)
            let destination = UnsafeMutableAudioBufferListPointer(chunk.mutableAudioBufferList)
            let bytesPerFrame = Int(self.inputFormat.streamDescription.pointee.mBytesPerFrame)
            for index in source.indices {
                guard let from = source[index].mData, let to = destination[index].mData else {
                    allocationFailed = true
                    inputStatus.pointee = .noDataNow
                    return nil
                }
                to.copyMemory(from: from.advanced(by: Int(offset) * bytesPerFrame),
                              byteCount: Int(count) * bytesPerFrame)
            }
            offset += count
            inputStatus.pointee = .haveData
            return chunk
        }
        if status == .error || allocationFailed {
            throw error ?? AudioError.conversion
        }
        guard output.frameLength > 0, let samples = output.int16ChannelData?[0] else { return Data() }
        return Data(bytes: samples, count: Int(output.frameLength) * 2)
    }
}

final class AudioRecorder: AudioRecording {
    private let engine = AVAudioEngine()
    private let lock = NSLock()
    private var running = false

    func start(onData: @escaping (Data) -> Void, onError: @escaping (Error) -> Void) throws {
        let input = engine.inputNode
        let inputFormat = input.outputFormat(forBus: 0)
        let converter = try PCMInputConverter(inputFormat: inputFormat)

        lock.lock()
        running = true
        lock.unlock()
        input.installTap(onBus: 0, bufferSize: 1024, format: inputFormat) { [weak self] buffer, _ in
            guard let self else { return }
            self.lock.lock()
            defer { self.lock.unlock() }
            guard self.running else { return }

            do {
                let audio = try converter.convert(buffer)
                if !audio.isEmpty {
                    onData(audio)
                }
            } catch {
                self.running = false
                onError(error)
            }
        }

        do {
            engine.prepare()
            try engine.start()
        } catch {
            stop()
            throw error
        }
    }

    func stop() {
        engine.stop()
        engine.inputNode.removeTap(onBus: 0)
        lock.lock()
        running = false
        lock.unlock()
    }
}

final class AudioPlayer: AudioPlaying {
    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let format = AVAudioFormat(
        standardFormatWithSampleRate: Double(VoiceProtocol.outputSampleRate), channels: 1)!
    private var decoder = PCM16Decoder()

    init() {
        engine.attach(player)
        engine.connect(player, to: engine.mainMixerNode, format: format)
    }

    func play(_ data: Data) throws {
        let samples = decoder.decode(data)
        guard !samples.isEmpty else { return }
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(samples.count)),
              let channel = buffer.floatChannelData?[0] else {
            throw AudioError.unavailable
        }
        buffer.frameLength = AVAudioFrameCount(samples.count)
        samples.withUnsafeBufferPointer { source in
            channel.update(from: source.baseAddress!, count: source.count)
        }
        if !engine.isRunning {
            engine.prepare()
            try engine.start()
        }
        player.scheduleBuffer(buffer)
        if !player.isPlaying {
            player.play()
        }
    }

    func stop() {
        player.stop()
        engine.stop()
        decoder.reset()
    }
}
