# MicroRecord

Minimal local Windows meeting audio recorder.

- `Ctrl+Alt+R` — start/stop recording
- tray icon with recording state
- captures default microphone and default Windows output; each side falls back through several capture paths:
  - system audio: WASAPI endpoint loopback → process loopback (all apps except MicroRecord, Windows 10 2004+)
  - microphone: WASAPI endpoint capture → WASAPI default-device routing (`ActivateAudioInterfaceAsync`) → WinMM `waveIn` → a tab in the default browser (`getUserMedia` on a page served from `127.0.0.1`, streamed back over a WebSocket; for machines where endpoint security such as Kaspersky blocks audio input for untrusted executables but trusts the browser — keep the tab open while recording)
- if only one side opens, records that side and shows a warning
- tray menu → **Diagnose audio** writes a capture matrix (every endpoint × stream flags × STA/MTA, plus the fallback paths) to `Documents\MicroRecord\microrecord.log`
- writes temporary tracks locally, then mixes them into one WAV
- no network, cloud, transcription, or telemetry

## Build

The GitHub Actions workflow publishes a self-contained single-file `win-x64` executable.

Current implementation uses NAudio for WASAPI capture instead of the earlier hand-written COM/WASAPI prototype.
