import Foundation

public enum VoiceProtocol {
    public static let start = #"{"type":"start"}"#
    public static let wake = #"{"type":"wake"}"#
    public static let interrupt = #"{"type":"interrupt"}"#
    public static let inputSampleRate = 16_000
    public static let outputSampleRate = 24_000
    public static let silence = Data(repeating: 0, count: inputSampleRate * 2)

    public struct Message: Decodable {
        public let type: String
        public let value: String?
        public let audioOut: String?

        private enum CodingKeys: String, CodingKey {
            case type, value
            case audioOut = "audio_out"
        }
    }
}

public struct PCM16Decoder {
    private var trailingByte: UInt8?

    public init() {}

    public mutating func reset() {
        trailingByte = nil
    }

    public mutating func decode(_ data: Data) -> [Float] {
        var samples = [Float]()
        samples.reserveCapacity((data.count + (trailingByte == nil ? 0 : 1)) / 2)

        for byte in data {
            if let low = trailingByte {
                let bits = UInt16(low) | (UInt16(byte) << 8)
                samples.append(Float(Int16(bitPattern: bits)) / 32768)
                trailingByte = nil
            } else {
                trailingByte = byte
            }
        }
        return samples
    }
}
