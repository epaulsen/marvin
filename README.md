# marvin

## Voice PE-gateway

Kjør fra roten av repoet med .NET 11 SDK:

```sh
dotnet run --project src/marvin.api/marvin.api.csproj -- --urls http://localhost:8080
```

Eller bygg og kjør .NET 11-containeren på Alpine. ONNX Runtime krever glibc;
imaget inkluderer derfor glibc og .NET-runtime fra Microsofts Debian-image.
Prosessen kjører uten root.

```sh
docker build -t marvin-api .
docker run --rm -p 127.0.0.1:8080:8080 marvin-api
```

`GET /health` er HTTP-probe for k3s. WebSocket-endepunktet `/voice` har foreløpig
ingen autentisering. Hold porten lokal til nettverkstilgang er avklart.

Gatewayen implementerer WebSocket-protokollen til
[Voice PE-forken](https://github.com/xandervanerven/home-assistant-voice-pe).
På tilkobling sendes `hello`. Enheten sender `start`, `wake` og deretter binær
PCM (16 kHz, 16-bit, mono). `interrupt` avbryter aktiv behandling; `flush`
forkaster ufullstendig lyd. Etter avsluttet svar kan samme forbindelse brukes
igjen. Stillhet mens enheten er inaktiv lukker ikke forbindelsen.

Silero VAD identifiserer tale lokalt og avslutter en ytring etter 800 ms stillhet.
Gatewayen sender fasene `listening`, `thinking` og `idle`. `replying` og binær
24 kHz PCM sendes hvis en talebehandler leverer et lydsvar. Standardbehandleren
er foreløpig tom: ingen transkripsjon, agent, talesyntese eller lydsvar er
implementert. Oppfølgingsvindu er derfor deaktivert (`follow_up_ms: 0`).
Ytringer begrenses til 60 sekunder, holdes kun i minnet og slettes etter bruk.

Den medfølgende [Silero-modellen](https://github.com/snakers4/silero-vad/blob/066409f3d9db45a30d21dfcbf9147826ffdd7052/src/silero_vad/data/silero_vad.onnx)
er MIT-lisensiert. SHA-256:
`1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3`.

Kjør xUnit-integrasjonstestene med .NET 11 SDK. `WebApplicationFactory` starter
gatewayen i testprosessen, så verken Docker eller en separat gateway trengs.
GitHub Actions kjører samme tester ved pull requests.

```sh
dotnet test --solution marvin.slnx --configuration Release
```