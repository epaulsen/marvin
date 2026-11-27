---
name: aspire-logs
description: Read Marvin's structured logs from its Aspire Dashboard when troubleshooting the service or correlating server events with Voice PE logs.
---

# Read Marvin logs from Aspire

Use this skill when the user asks to inspect Marvin/Aspire logs or correlate them with a Voice PE reproduction.

## Open the dashboard

- The dashboard is at `http://glumserver.localdomain:18888/structuredlogs`. The root URL redirects there.
- The dashboard currently responds without authentication. Do not treat this as public access: only connect when the user asks, and do not probe other hosts or guessed endpoints.
- In **Structured logs**, select the `marvin` resource and narrow the time range to the incident or reproduction window.
- If dashboard access fails, report the error; do not silently fall back to another host or endpoint.

The repository README documents `GET /api/telemetry/logs?resource=marvin`, but that endpoint returned HTTP 404 on the current dashboard. Prefer the Structured logs page; do not guess alternate API routes.

## Handle logs carefully

- Capture Aspire and Voice PE logs over the same reproduction window when correlation is requested.
- Compare timestamps and time zones before matching events. Distinguish what Marvin received from what the device reported.
- Share only relevant excerpts. Redact secrets, tokens, personal data, and network/device identifiers from summaries or shared output.
- Dashboard access is unauthenticated for anyone who can reach port `18888`; keep that port restricted to a trusted network and never expose it publicly.
