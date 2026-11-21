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

For serverinstallasjon med Docker Hub-imaget, kopier
`samples/.env.example` til `samples/.env`, fyll inn verdiene og kjør:

```sh
cd samples
docker compose pull
docker compose up -d
```

Porten er `5678` som standard. Etter en ny publisering kjører du de samme to
compose-kommandoene for å laste ned imaget og opprette containeren på nytt.
Begrens tilgang til `.env`, som inneholder API-nøkler, og sørg for at Home
Assistant-adressen kan nås fra containeren.

Når en pull request merges til `main`, bygger GitHub Actions et `linux/amd64`-
image og publiserer `epaulsen/marvin:latest`. Workflowen krever GitHub Actions-
secrets `DOCKERHUB_USERNAME` og `DOCKERHUB_TOKEN`, der tokenet har push-tilgang
til Docker Hub-repositoriet. Legg dem inn under **Settings → Secrets and
variables → Actions** i GitHub-repositoriet.
Workflowen kan også startes manuelt fra Actions eller med
`gh workflow run publish-image.yml --ref main`; den bygger da siste `main`.

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
Gatewayen sender fasene `listening`, `thinking`, `replying` og `idle`. Med
OpenAI og TTS konfigurert sendes transkripsjonen til agenten, og svaret spilles
av som 24 kHz PCM. Automatisk lytting etter alle svar er deaktivert
(`follow_up_ms: 0`). Når agenten trenger en avklaring, kan den i stedet bruke
`ask_user`: spørsmålet leses opp, og en kompatibel Voice PE-firmware åpner et
kort oppfølgingsvindu uten vekkeord. Hvis ingen svarer, lukkes mikrofonen.

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

### Home Assistant TTS

Angi Home Assistant-adresse, et long-lived access token og entity-ID for den
eksisterende TTS-tjenesten i den ignorerte utviklingsfilen:

```json
{
  "OPPER_API_KEY": "DIN_OPPER_NOKKEL",
  "HomeAssistant": {
    "Url": "http://homeassistant.local:8123",
    "Token": "DITT_HOME_ASSISTANT_TOKEN",
    "TtsEntity": "tts.din_tjeneste"
  }
}
```

Opprett token under Home Assistant-profilens **Security**-innstillinger. Adressen
må være tilgjengelig fra gatewayen og kan ikke inneholde en sti. TTS-entity-ID
finnes i Home Assistant under utviklerverktøyets tilstander. For containeren
bruk `HomeAssistant__Url`, `HomeAssistant__Token` og `HomeAssistant__TtsEntity`
som miljøvariabler. Uten disse fungerer transkripsjon fortsatt, men uten talesvar.

Gatewayen ber Home Assistant om WAV med 24 kHz, mono og 16-bit. Home Assistant
kan bruke FFmpeg til konvertering hvis TTS-tjenesten trenger det. Fast fil-cache
er deaktivert for forespørselen; tekst og lyd kan likevel behandles eller
mellomlagres av Home Assistant og den valgte TTS-leverandøren. Bruk HTTPS eller
et pålitelig nettverk for å beskytte token og taleinnhold.

### OpenAI og Home Assistant MCP

Sett `OPENAI_API_KEY` i miljøet eller utviklingsfilen. Konfigurer eventuelt
`OpenAI:Model` (miljøvariabel `OpenAI__Model`); standard er `gpt-6-luna`.
En OpenAI API-nøkkel og API-fakturering kreves. Uten nøkkel leses
transkripsjonen tilbake som før.

Samtalehistorikken holdes kun i minnet, separat per WebSocket-forbindelse, med
opptil seks tidligere ytringer og svar. Den slettes ved neste ytring etter fem
minutters inaktivitet, eller når forbindelsen lukkes. Flere personer som bruker samme
enhet deler denne historikken; taleridentifikasjon er ikke implementert.

For å aktivere enhetsstyring, aktiver Home Assistants Model Context Protocol-
integrasjon og sett `HomeAssistant:McpEnabled` til `true`
(`HomeAssistant__McpEnabled` i container). Gatewayen bruker eksisterende
Home Assistant-URL og token, og kobler lokalt til `/api/mcp/assist`. Bare
enheter eksponert for Home Assistants stemmeassistenter er tilgjengelige.
Gatewayen krever ikke at Home Assistant eksponeres mot internett. Når MCP er
aktivert, feiler taleturen hvis MCP-tilkoblingen ikke virker.

**Enhetsstyring skjer uten ekstra bekreftelse.** WebSocket-endepunktet har ingen
autentisering, så alle med tilgang kan sende forespørsler som styrer eksponerte
enheter. Bruk bare et pålitelig nettverk og eksponer kun ønskede enheter.
En handling kan allerede være utført selv om taleturen avbrytes.
Transkripsjoner, samtalehistorikk og relevante verktøyresultater sendes til
OpenAI; kontroller tjenestens personvern- og lagringsvilkår.

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