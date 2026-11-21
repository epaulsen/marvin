import SwiftUI

@main
struct MarvinTestApp: App {
    @StateObject private var client = VoiceClient()

    var body: some Scene {
        WindowGroup {
            VStack(alignment: .leading, spacing: 16) {
                Text("Marvin Test").font(.title)
                HStack {
                    TextField("WebSocket-adresse", text: $client.address)
                        .textFieldStyle(.roundedBorder)
                        .disabled(client.connected || client.connecting)
                    if client.connected || client.connecting {
                        Button("Koble fra") { client.disconnect() }
                    } else {
                        Button("Koble til") { client.connect() }
                    }
                }
                HStack {
                    Button(client.recording ? "Stopp opptak" : "Start opptak") {
                        if client.recording {
                            client.stopRecording()
                        } else {
                            client.startRecording()
                        }
                    }
                    .disabled(!client.connected || client.busy)
                    Text(client.status)
                }
                Text("Trykk for å starte og stoppe opptak. Lydsvar spilles av automatisk.")
                    .foregroundStyle(.secondary)
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) {
                        ForEach(Array(client.events.enumerated()), id: \.offset) { _, event in
                            Text(event).textSelection(.enabled)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(minHeight: 140)
            }
            .padding(20)
            .frame(minWidth: 560, minHeight: 300)
        }
    }
}
