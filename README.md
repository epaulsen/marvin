# marvin

## Voice PE-gateway

Kjør fra roten av repoet med .NET 11 SDK:

```sh
dotnet run --project src/marvin.api/marvin.api.csproj -- --urls http://localhost:8080
```

Eller bygg og kjør .NET 11-containeren på Alpine. Prosessen kjører uten root.

```sh
docker build -t marvin-api .
docker run --rm -p 127.0.0.1:8080:8080 marvin-api
```

`GET /health` er HTTP-probe for k3s. WebSocket-endepunktet `/voice` har foreløpig
ingen autentisering. Hold porten lokal til nettverkstilgang er avklart.

Simulatoren sender `{"type":"start"}`, binær PCM (16 kHz, 16-bit, mono) og
`{"type":"end"}` over samme forbindelse. `end` er en midlertidig testmelding,
ikke en bekreftet del av
[Voice PE-forkens protokoll](https://github.com/xandervanerven/home-assistant-voice-pe).
Gatewayen teller byte uten å lagre lyd eller transkribere den. En ytring kan
inneholde opptil 60 sekunder PCM; frakobling eller 30 sekunder uten data forkaster
en ufullført ytring.

Kjør xUnit-integrasjonstestene med .NET 11 SDK. `WebApplicationFactory` starter
gatewayen i testprosessen, så verken Docker eller en separat gateway trengs.
GitHub Actions kjører samme tester ved pull requests.

```sh
dotnet test --solution marvin.slnx --configuration Release
```