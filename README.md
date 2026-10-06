# MicroRecord

Minimal local Windows meeting audio recorder.

- `Ctrl+Alt+R` — start/stop recording
- tray icon with recording state
- captures default microphone and default Windows output (WASAPI loopback)
- writes temporary tracks locally, then mixes them into one WAV
- no network, cloud, transcription, or telemetry

## Build

The GitHub Actions workflow publishes a self-contained single-file `win-x64` executable.

Current implementation uses NAudio for WASAPI capture instead of the earlier hand-written COM/WASAPI prototype.
