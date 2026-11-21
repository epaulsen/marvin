import Foundation
import XCTest
@testable import MarvinProtocol

final class VoiceProtocolTests: XCTestCase {
    func testPCMDecoderHandlesSignedLittleEndianAndSplitSamples() {
        var decoder = PCM16Decoder()
        XCTAssertEqual(decoder.decode(Data([0x00])), [])
        XCTAssertEqual(decoder.decode(Data([0x80, 0xff, 0x7f, 0x00, 0x00])), [-1, 32767 / 32768, 0])
    }

    func testDecoderResetDiscardsIncompleteSample() {
        var decoder = PCM16Decoder()
        XCTAssertEqual(decoder.decode(Data([0x7f])), [])
        decoder.reset()
        XCTAssertEqual(decoder.decode(Data([0x00, 0x00])), [0])
    }

    func testParsesHelloAndPhaseMessages() throws {
        let decoder = JSONDecoder()
        let hello = try decoder.decode(VoiceProtocol.Message.self, from: Data(#"{"type":"hello","audio_out":"pcm","follow_up_ms":0}"#.utf8))
        XCTAssertEqual(hello.type, "hello")
        XCTAssertEqual(hello.audioOut, "pcm")
        let phase = try decoder.decode(VoiceProtocol.Message.self, from: Data(#"{"type":"phase","value":"thinking"}"#.utf8))
        XCTAssertEqual(phase.value, "thinking")
    }
}
