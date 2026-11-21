# marvin

## Voice PE-gateway

Kjør fra roten av repoet med .NET 11 SDK:

```sh
dotnet run --project src/marvin.api/marvin.api.csproj
```

Eller bygg og kjør .NET 11-containeren på Alpine. ONNX Runtime krever glibc;
imaget inkluderer derfor glibc og .NET-runtime fra Microsofts Debian-image.
Prosessen kjører uten root.

```sh
docker build -t marvin-api .
docker run --rm -p 8080:8080 -e OPPER_API_KEY marvin-api
```

`GET /health` er HTTP-probe for k3s. WebSocket-endepunktet `/voice` har foreløpig
ingen autentisering. Gatewayen lytter på `0.0.0.0:8080`; begrens tilgangen
til et pålitelig nettverk med brannmur.

Gatewayen implementerer WebSocket-protokollen til
[Voice PE-forken](https://github.com/xandervanerven/home-assistant-voice-pe).
På tilkobling sendes `hello`. Enheten sender `start`, `wake` og deretter binær
PCM (16 kHz, 16-bit, mono). `interrupt` avbryter aktiv behandling; `flush`
forkaster ufullstendig lyd. Etter avsluttet svar kan samme forbindelse brukes
igjen. Stillhet mens enheten er inaktiv lukker ikke forbindelsen.

Silero VAD identifiserer tale lokalt og avslutter en ytring etter 800 ms stillhet.
Gatewayen sender fasene `listening`, `thinking` og `idle`. `replying` og binær
24 kHz PCM sendes hvis en talebehandler leverer et lydsvar. Agent, talesyntese
og lydsvar er ikke implementert, og oppfølgingsvinduet er deaktivert
(`follow_up_ms: 0`).

Sett miljøvariabelen `OPPER_API_KEY` for å aktivere transkripsjon med
NB-Whisper Large via Opper/Berget. Ved lokal debugging kan nøkkelen i stedet
settes i `src/marvin.api/appsettings.Development.json`. Hvis filen mangler,
kopier `appsettings.Development.example.json` til dette navnet. Utviklingsfilen
er ignorert av Git og utelates fra Docker-imaget. Velg launch-profilen
`marvin.api` for å aktivere Development-miljøet. Containeren bruker Production
som standard; send derfor inn nøkkelen som miljøvariabel. Uten nøkkel er
transkripsjon deaktivert. Miljøvariabler og kommandolinjeargumenter overstyrer
appsettings-filene.
Etter VAD sendes hele ytringen som WAV i en base64-data-URI; Opper støtter
ikke direkte binærlyd på transkripsjonsendepunktet. Ytringer begrenses til
60 sekunder og lagres ikke som filer av gatewayen.

Lyd sendes til en ekstern tjeneste. Sjekk personvern- og lagringsvilkår før
bruk. Transkripsjonen logges ikke som standard; sett `Marvin__LogTranscripts`
til `true` for å vise teksten i gatewayens logger. Unngå dette dersom loggene
ikke er egnet for sensitivt innhold.

Eksempel med Docker når `OPPER_API_KEY` allerede er satt i miljøet:

```sh
docker run --rm -p 8080:8080 -e OPPER_API_KEY -e Marvin__LogTranscripts=true marvin-api
```

Debug-logger for `Marvin.Api` viser tilkobling, kontrollmeldinger, lydmengde,
VAD-faser og status for transkripsjonskallet. Tilkoblinger har hver sin
loggkontekst. Verken lyddata eller API-nøkler logges.

Den medfølgende [Silero-modellen](https://github.com/snakers4/silero-vad/blob/066409f3d9db45a30d21dfcbf9147826ffdd7052/src/silero_vad/data/silero_vad.onnx)
er MIT-lisensiert. SHA-256:
`1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3`.

Kjør xUnit-integrasjonstestene med .NET 11 SDK. `WebApplicationFactory` starter
gatewayen i testprosessen, så verken Docker eller en separat gateway trengs.
GitHub Actions kjører samme tester ved pull requests.

```sh
dotnet test --solution marvin.slnx --configuration Release
```