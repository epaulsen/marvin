# Overordnet plan: Voice PE taleinngang

## Mål

Motta tale fra Home Assistant Voice PE, transkribere den via Otter, identifisere taleren lokalt og returnere et strukturert resultat. Første leveranse stopper ved JSON; Home Assistant-handlinger og AI-agent kommer senere.

Eksempel:

```json
{
  "user": "Erling",
  "text": "Slå på lyset i stua"
}
```

Hvis taleren ikke kan identifiseres, returneres `user: null` eller en avtalt verdi som `"unknown"`.

## Foreslått arkitektur

```text
Home Assistant Voice PE
        │ WebSocket: kontrollmeldinger + PCM-lyd
        ▼
.NET 11 gateway
        ├── Otter API: transkripsjon
        └── Python/SpeechBrain-tjeneste: taleridentifikasjon
                    │
                    ▼
             Embedding-profiler

Gateway returnerer JSON
        │
        └── Senere: AI-agent med Home Assistant-, Entur- og andre MCP-verktøy
```

### 1. Gateway (.NET 11)

- Motta Voice PE-strømmen via WebSocket.
- Implementere protokollen til [Voice PE-forken](https://github.com/xandervanerven/home-assistant-voice-pe) og samle lyd for én ytring. Foreløpig antar vi at enheten avgjør når ytringen er ferdig; signalet må bekreftes på fysisk Voice PE.
- Konvertere/formatere lyd ved behov for Otter.
- Kalle Otter og taleridentifikasjon parallelt.
- Samle resultatene i JSON og håndtere feil, tidsavbrudd og logger.
- Ikke lagre råopptak permanent i første versjon.

### 2. Taleridentifikasjon (Python-container)

- Lage et tynt HTTP-API rundt SpeechBrain og ECAPA-TDNN; modellen og talealgoritmen brukes, ikke implementeres fra bunnen av.
- Laste modellen én gang ved oppstart og holde den varm.
- Støtte registrering av husstandsmedlemmer og lagre stemmeprofiler som embedding-er.
- Sammenligne nye opptak mot profilene og returnere ID, matchscore eller «ukjent».
- Bruke 16 kHz mono-lyd, som samsvarer med Voice PE-strømmen i den undersøkte firmware-forken.
- Lagre profiler lokalt; unngå å lagre rå stemmeopptak etter at profil er laget.

### 3. Framtidig agent-app

- Egen tjeneste som mottar ferdig JSON, ikke rå lyd.
- Behandle tekst og velge handling via egne verktøy/MCP-servere, for eksempel Home Assistant og Entur.
- Holdes utenfor første leveranse.

## Faser

### Fase 0 – Avklaringer og teknisk spike

- Bekrefte at Otter-kontoen har API-tilgang, dokumentert opplasting, akseptert lydformat og forventet responstype.
- Verifisere Voice PE-firmwareprotokoll, lydformat og hvordan slutt på ytring formidles. Ikke anta at en WebSocket-forbindelse alene gir ferdige lydklipp.
- Velge JSON-kontrakt og håndtering av ukjent taler.
- Avklare lagring, samtykke og sletting av stemmeprofiler.

**Utgang:** Bekreftet integrasjonsvei for Otter og Voice PE, samt definert lydgrense per ytring.

### Fase 1 – Tjenesteskjelett

- Opprette .NET 11-gateway og Python-tjeneste. Gatewayen pakkes med Alpine for senere utrulling i k3s.
- Legge tjenestene i Docker Compose med intern nettverkstrafikk, konfigurasjon og health checks.
- Definere interne API-er mellom gateway og talertjeneste.

### Fase 2 – Talerprofiler og identifikasjon

- Implementere registrering av profiler og identifikasjon via SpeechBrain.
- Forhåndsberegne og lagre embedding-er for registrerte personer.
- Kalibrere terskel for match og teste «ukjent»-utfall med korte opptak og bakgrunnsstøy.

### Fase 3 – Voice PE og Otter

- Implementere WebSocket-mottak og protokollhåndtering i gateway.
- Samle én ytring og bygge Otter-forespørselen i formatet API-et krever.
- Kalle Otter og talertjenesten parallelt.
- Håndtere API-feil, tidsavbrudd, gjentakelser og midlertidige lyddata.

### Fase 4 – Ende-til-ende-test og første leveranse

- Teste med fysisk Voice PE, flere talere og realistisk nettverk/støy.
- Måle responstid separat for lydinnsamling, talermatch og Otter.
- Verifisere at ukjent taler og feilet transkripsjon håndteres tydelig.
- Dokumentere utrulling, profilregistrering, konfigurasjon og feilsøking.

### Senere fase – AI-agent og verktøy

- En agentkomponent i gatewayen bruker OpenAIs .NET SDK og mottar transkripsjonen.
- Home Assistant MCP kan aktiveres for eksponerte enheter; Entur og andre verktøy kommer senere.
- Bruke den eksisterende TTS-tjenesten i Home Assistant til å gjøre agentsvar om til tale. Gatewayen henter lyden og sender den til Voice PE som 24 kHz, 16-bit mono PCM.
- Beholde taleinngang og agent som separate komponenter i samme prosess.

## Første leveranses akseptansekriterier

- Voice PE kan koble til gateway og sende lyd uten Home Assistant voice pipeline.
- En ytring blir samlet korrekt og sendt til Otter.
- Taleresultat kommer fra egen container; usikker match gir «ukjent».
- Gateway returnerer ett resultat med `user` og `text`.
- Rå lyd beholdes ikke permanent.
- Tjenestene kan bygges og startes med Docker Compose, og logger/API-feil er diagnostiserbare.

## Estimat og usikkerhet

Grovt anslag for første leveranse med taleridentifikasjon: **4–7 utviklerdager**, forutsatt tilgjengelig Otter-API og en brukbar integrasjonsvei fra firmware. Estimatet er usikkert fram til fase 0 er gjennomført. Manglende Otter-API-tilgang eller behov for større firmwareendringer kan øke omfanget betydelig.

## Viktige beslutninger

- Start med .NET 11 for gateway; velg Rust bare hvis ressursmålinger eller teamkompetanse gir konkret grunn.
- Bruk Python-container for SpeechBrain-modellen.
- Hold Otter- og talerkall parallelle.
- Ikke bygg agent/MCP-integrasjoner inn i første leveranse.
- Bruk Home Assistants konfigurerte TTS-tjeneste til framtidige talesvar.
- Behandle stemmeprofiler som sensitive data: begrens tilgang, lagre lokalt og støtt sletting.
