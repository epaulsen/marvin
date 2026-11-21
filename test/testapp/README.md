# Marvin Test

Lokal macOS-app for å teste Voice PE-protokollen uten fysisk enhet. Krever
macOS 13 eller nyere og Xcode med kommandolinjeverktøy.

Start gatewayen separat, og kjør:

```sh
sh test/testapp/run.sh
```

Oppgi gatewayens WebSocket-adresse, for eksempel `ws://localhost:8080/voice`,
og koble til. Trykk på opptaksknappen for å starte og stoppe opptak. Gi appen
tilgang til mikrofonen når macOS spør. Appen sender `wake` og 16 kHz PCM16 mono
mens du snakker, og legger til ett sekund stillhet ved stopp så VAD kan avslutte
ytringen. Gatewayens lydsvar spilles av som 24 kHz PCM16 mono.

Appen tillater ukryptert `ws://` for lokal testing. Bruk kun et pålitelig
nettverk: gatewayen har ingen autentisering. Appen viser protokollfaser, ikke
transkripsjonen; gatewayen sender foreløpig ikke transkribert tekst til klienten.

Kjør protokolltestene med:

```sh
swift test --package-path test/testapp
```
