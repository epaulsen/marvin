---
name: esp-home-logger
description: Connect to ESPHome Device Builder and stream Voice PE logs to correlate wake-word, phase, and WebSocket events with Marvin logs. Use when debugging ESPHome or Voice PE behavior.
---

# Stream ESPHome device logs

Use the ESPHome Device Builder API to capture device logs while reproducing an issue. This is a read-only log subscription; it does not change firmware or device state.

## Find the Builder and device configuration

- Use the Builder URL already provided by the user or present in the current task context. Do not scan the LAN or guess hosts. The direct Builder commonly uses port `6052`; preserve any HTTPS, authentication, or ingress path from the actual URL.
- Query `GET <Builder URL>/devices` and select the target device from `.configured[]`. Use its `configuration` value for the log request; device name and configuration filename can differ.
- If the Builder URL or target device is ambiguous, ask for it rather than probing unrelated hosts.

Example lookup:

```sh
curl -fsS "$ESPHOME_BUILDER_URL/devices" |
  jq --arg name "$DEVICE_NAME" \
    '.configured[] | select(.name == $name) | {name, configuration, address}'
```

## Open the live log stream

The Dashboard WebSocket is at `ws://<Builder host>:6052/ws` (use `wss://` for HTTPS). If the dashboard is served under a path prefix, preserve that prefix before `/ws`.

The server sends a greeting containing its versions and `requires_auth`. If authentication is required, stop and use an already-authorized session; do not bypass authentication or put passwords or tokens in chat or command history.

For a network-connected device, send this JSON frame after the greeting, substituting the configuration filename from `/devices`:

```json
{
  "command": "devices/logs",
  "message_id": "1",
  "args": {
    "configuration": "<configuration>",
    "port": "OTA",
    "no_states": true
  }
}
```

In Node.js with its built-in `WebSocket`, consume frames where `event` is `output` and print their `data` lines. Stop on `result`, an error, or when the bounded capture window ends. Keep the stream open while the user reproduces the issue; reconnecting does not provide a reliable history of earlier events. `OTA` is for network log access; use the Builder UI's serial options only when logs must come over USB.

## Correlate with Marvin

- Capture the Voice PE logs and Marvin/Aspire logs over the same reproduction window.
- Look for device-side wake-word/VAD activity, `va_client` WebSocket messages (`wake`, `interrupt`, `flush`), and voice-phase changes around the server timestamp.
- Compare time zones and clock offsets before matching timestamps. Report device-side evidence separately from what Marvin received; do not infer why an interrupt was sent unless the device logs show its trigger.
- If no relevant lines arrive, report that plainly. A live stream started after an incident cannot establish what happened earlier.

ESPHome logs can include local IPs, MAC addresses, Wi-Fi details, and device identifiers. Keep raw logs private and omit or redact those values in summaries, commits, or other shared output.
