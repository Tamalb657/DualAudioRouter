# DualAudioRouter

**DualAudioRouter** is a high-performance, real-time Windows audio routing and mirroring application built with **WPF**, **.NET 8**, and **NAudio**. It captures system audio from any WASAPI loopback playback source and mirrors it simultaneously across multiple audio output destinations (headphones, speakers, Bluetooth devices, capture cards, virtual audio cables) with independent volume controls, clock drift compensation, and bass boost DSP.

---

## ✨ Features

- **⚡ WASAPI Real-Time Loopback:** Zero-copy audio capture directly from Windows Audio Session API with ultra-low latency.
- **🎧 Multi-Destination Mirroring:** Route audio stream simultaneously to two or more playback endpoints.
- **🎚️ Per-Device Volume & Mute:** Independent gain scaling and instant mute control per output destination.
- **🔊 Bass Boost DSP (1.15x):** Optional low-frequency enhancement toggle per destination.
- **⏱️ Clock Drift Compensation:** Dynamic buffer resampling to prevent audio crackles and drift when devices run at slightly mismatched physical DAC clock rates.
- **🔄 Sync Offset Delay:** Adjustable buffer delay (0–300 ms) to synchronize audio across wireless/Bluetooth and wired hardware.
- **📊 Real-Time Peak Metering:** Dual-channel (L/R) VU progress bars with decibel readout (dBFS).
- **🎨 Windows 11 Fluent UI:** Dark theme styling built with `WPF-UI`, Mica window backdrop, and hardware-accelerated ClearType font rendering (`Segoe UI Variable`).
- **📦 Compact Standalone Single-File Executable:** Packaged into an optimized ~8.4 MB standalone executable without external dependencies or installer.

---

## 🚀 Quick Start

### Running the Standalone Executable
You can run the pre-built single-file executable directly without installing anything:
```
Standalone_EXE/DualAudioRouter.exe
```

### Building from Source
Ensure you have the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) installed.

```bash
# Clone the repository
git clone https://github.com/Tamalb657/DualAudioRouter.git
cd DualAudioRouter/DualAudioRouter

# Build Release
dotnet build -c Release

# Publish Standalone Single-File Executable
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../Standalone_EXE
```

---

## 🛠️ Architecture

- **UI Layer (`DualAudioRouter`):**
  - Modern Windows 11 Fluent dark theme utilizing `WPF-UI` and ClearType subpixel rendering.
  - MVVM pattern (`MainViewModel`, `OutputRowViewModel`) for clean separation of concerns.
- **Audio Engine (`AudioEngine.cs`):**
  - Powered by `NAudio.WasapiLoopbackCapture` and `NAudio.Wave.WasapiOut`.
  - Zero-allocation audio buffer processing using `MemoryMarshal.Cast<byte, float>` and reusable memory slices to prevent garbage collector stutter.
  - Independent volume multipliers and biquad-filtered bass boost algorithms.

---

## 📄 License
MIT License.
