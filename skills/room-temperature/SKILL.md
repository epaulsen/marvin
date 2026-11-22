---
name: room-temperature
description: Answers questions about the measured temperature in a room or a climate device's set point. Use when someone asks how warm a room is, what temperature a room is currently at, or what a thermostat is set to.
---

## Choose the right temperature

- For the room's measured temperature, use the climate entity's `current_temperature` attribute. Never substitute its `temperature` set point.
- For the requested thermostat set point, use the climate entity's `temperature` attribute.
- Resolve the room from its Home Assistant area and entity names. If several climate entities match, use the one associated with the requested room.
- If the requested value is unavailable or the room cannot be identified confidently, say so or ask which room/device the user means. Never guess.
