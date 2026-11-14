using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualAudioRouter
{
    public class AudioEngine : IDisposable
    {
        private WasapiLoopbackCapture? capture;
        private readonly List<OutputChannel> channels = new();
        private bool isRunning;
        private int latencyMs = 20;
        private int delayMs = 0;

        public bool IsRunning => isRunning;
        public WaveFormat? WaveFormat => capture?.WaveFormat;

        public event Action<float, float>? PeakDataAvailable;

        public static List<AudioDeviceInfo> GetRenderDevices()
        {
            var result = new List<AudioDeviceInfo>();
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var dev in devices)
                {
                    result.Add(new AudioDeviceInfo { Id = dev.ID, Name = dev.FriendlyName });
                }
            }
            catch { }
            return result;
        }

        public StartResult Start(string captureDeviceId, List<OutputConfig> outputs, int latency, int delay, bool exclusiveMode = false)
        {
            if (isRunning) return new StartResult { Success = false, Error = "Engine is already running" };

            try
            {
                latencyMs = latency;
                delayMs = delay;

                var enumerator = new MMDeviceEnumerator();
                MMDevice? captureDevice = null;
                foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (dev.ID == captureDeviceId)
                    {
                        captureDevice = dev;
                        break;
                    }
                }

                if (captureDevice == null)
                    return new StartResult { Success = false, Error = "Primary capture device not found" };

                capture = new WasapiLoopbackCapture(captureDevice);
                channels.Clear();

                foreach (var cfg in outputs)
                {
                    MMDevice? outDev = null;
                    foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        if (dev.ID == cfg.DeviceId)
                        {
                            outDev = dev;
                            break;
                        }
                    }

                    if (outDev != null)
                    {
                        var ch = new OutputChannel(
                            outDev,
                            capture.WaveFormat,
                            latencyMs,
                            delayMs,
                            cfg.Volume / 100f,
                            cfg.IsMuted,
                            cfg.BassBoost,
                            exclusiveMode);
                        channels.Add(ch);
                    }
                }

                if (channels.Count == 0)
                    return new StartResult { Success = false, Error = "No output destinations found" };

                capture.DataAvailable += OnCaptureDataAvailable;
                capture.StartRecording();
                isRunning = true;

                return new StartResult
                {
                    Success = true,
                    SampleRate = capture.WaveFormat.SampleRate,
                    Channels = capture.WaveFormat.Channels,
                    OutputCount = channels.Count
                };
            }
            catch (Exception ex)
            {
                Stop();
                return new StartResult { Success = false, Error = ex.Message };
            }
        }

        public void SetOutputMuted(string deviceId, bool isMuted)
        {
            lock (channels)
            {
                var ch = channels.FirstOrDefault(c => c.DeviceId == deviceId);
                if (ch != null) ch.IsMuted = isMuted;
            }
        }

        public void SetOutputVolume(string deviceId, float volume)
        {
            lock (channels)
            {
                var ch = channels.FirstOrDefault(c => c.DeviceId == deviceId);
                if (ch != null) ch.Volume = Math.Clamp(volume, 0f, 1f);
            }
        }

        public void SetOutputBassBoost(string deviceId, bool bassBoost)
        {
            lock (channels)
            {
                var ch = channels.FirstOrDefault(c => c.DeviceId == deviceId);
                if (ch != null) ch.BassBoost = bassBoost;
            }
        }

        private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0 || capture == null) return;

            float peakL = 0f, peakR = 0f;
            var format = capture.WaveFormat;

            // Zero-allocation SIMD-friendly span peak measurement
            if (format.BitsPerSample == 32)
            {
                ReadOnlySpan<float> floatSpan = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
                int chCount = format.Channels;
                for (int i = 0; i < floatSpan.Length; i += chCount)
                {
                    float l = Math.Abs(floatSpan[i]);
                    if (l > peakL) peakL = l;

                    if (chCount > 1 && i + 1 < floatSpan.Length)
                    {
                        float r = Math.Abs(floatSpan[i + 1]);
                        if (r > peakR) peakR = r;
                    }
                }
            }
            else if (format.BitsPerSample == 16)
            {
                ReadOnlySpan<short> shortSpan = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
                int chCount = format.Channels;
                for (int i = 0; i < shortSpan.Length; i += chCount)
                {
                    float l = Math.Abs(shortSpan[i]) / 32768f;
                    if (l > peakL) peakL = l;

                    if (chCount > 1 && i + 1 < shortSpan.Length)
                    {
                        float r = Math.Abs(shortSpan[i + 1]) / 32768f;
                        if (r > peakR) peakR = r;
                    }
                }
            }

            PeakDataAvailable?.Invoke(peakL, peakR);

            // Fan out to active channels
            lock (channels)
            {
                for (int i = 0; i < channels.Count; i++)
                {
                    channels[i].AddSamples(e.Buffer, e.BytesRecorded);
                }
            }
        }

        public void Stop()
        {
            try { capture?.StopRecording(); } catch { }
            try { capture?.Dispose(); } catch { }
            capture = null;

            lock (channels)
            {
                for (int i = 0; i < channels.Count; i++)
                {
                    try { channels[i].Dispose(); } catch { }
                }
                channels.Clear();
            }
            isRunning = false;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    public class OutputChannel : IDisposable
    {
        public string DeviceId { get; }
        private readonly WasapiOut playback;
        private readonly BufferedWaveProvider buffer;
        public volatile bool IsMuted;
        public volatile float Volume = 1.0f;
        public volatile bool BassBoost;

        // Biquad low-shelf filter parameters (+3dB below 120Hz)
        private float b0, b1, b2, a1, a2;
        private float x1_L, x2_L, y1_L, y2_L;
        private float x1_R, x2_R, y1_R, y2_R;
        private bool biquadInitialized;

        private byte[] processBuffer = new byte[8192];

        public OutputChannel(MMDevice device, WaveFormat format, int latencyMs, int delayMs, float vol, bool isMuted, bool bass, bool exclusiveMode)
        {
            DeviceId = device.ID;
            Volume = vol;
            IsMuted = isMuted;
            BassBoost = bass;

            InitBiquad(format.SampleRate, 120.0f, 3.0f);

            // 2-second buffer capacity prevents packet discard on CPU/scheduling spikes
            buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            // Event-Sync mode provides lowest jitter and aligns with hardware audio clock
            var shareMode = exclusiveMode ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
            try
            {
                playback = new WasapiOut(device, shareMode, true, latencyMs);
                playback.Init(buffer);
            }
            catch
            {
                // Graceful fallback to shared timer mode if driver doesn't support EventSync or Exclusive
                playback = new WasapiOut(device, AudioClientShareMode.Shared, false, latencyMs);
                playback.Init(buffer);
            }
            playback.Play();
        }

        private void InitBiquad(int sampleRate, float f0, float gainDb)
        {
            float A = MathF.Pow(10f, gainDb / 40f);
            float omega = 2f * MathF.PI * f0 / sampleRate;
            float sn = MathF.Sin(omega);
            float cs = MathF.Cos(omega);
            float alpha = sn / 2f * MathF.Sqrt(2f);
            float beta = 2f * MathF.Sqrt(A) * alpha;

            float a0 = (A + 1f) + (A - 1f) * cs + beta;
            b0 = (A * ((A + 1f) - (A - 1f) * cs + beta)) / a0;
            b1 = (2f * A * ((A - 1f) - (A + 1f) * cs)) / a0;
            b2 = (A * ((A + 1f) - (A - 1f) * cs - beta)) / a0;
            a1 = (-2f * ((A - 1f) + (A + 1f) * cs)) / a0;
            a2 = ((A + 1f) + (A - 1f) * cs - beta) / a0;
            biquadInitialized = true;
        }

        public void AddSamples(byte[] data, int count)
        {
            if (count <= 0) return;

            if (processBuffer.Length < count)
            {
                processBuffer = new byte[Math.Max(count, processBuffer.Length * 2)];
            }

            // If muted, deliver silence to keep buffer streaming without playing sound
            if (IsMuted)
            {
                Array.Clear(processBuffer, 0, count);
                buffer.AddSamples(processBuffer, 0, count);
                return;
            }

            float curVol = Volume;
            bool curBass = BassBoost;

            // BIT-PERFECT BYPASS: If volume is 100% and BassBoost is off,
            // stream original captured bits directly without any math or modification!
            if (Math.Abs(curVol - 1.0f) <= 0.001f && !curBass)
            {
                buffer.AddSamples(data, 0, count);
                return;
            }

            Span<byte> targetSpan = processBuffer.AsSpan(0, count);
            data.AsSpan(0, count).CopyTo(targetSpan);

            var format = buffer.WaveFormat;
            if (format.BitsPerSample == 32)
            {
                Span<float> floatSpan = MemoryMarshal.Cast<byte, float>(targetSpan);
                int chCount = format.Channels;

                for (int i = 0; i < floatSpan.Length; i += chCount)
                {
                    // Left Channel
                    float l = floatSpan[i] * curVol;
                    if (curBass && biquadInitialized)
                    {
                        float y = b0 * l + b1 * x1_L + b2 * x2_L - a1 * y1_L - a2 * y2_L;
                        x2_L = x1_L; x1_L = l;
                        y2_L = y1_L; y1_L = y;
                        l = Math.Clamp(y, -1.0f, 1.0f);
                    }
                    floatSpan[i] = l;

                    // Right Channel
                    if (chCount > 1 && i + 1 < floatSpan.Length)
                    {
                        float r = floatSpan[i + 1] * curVol;
                        if (curBass && biquadInitialized)
                        {
                            float y = b0 * r + b1 * x1_R + b2 * x2_R - a1 * y1_R - a2 * y2_R;
                            x2_R = x1_R; x1_R = r;
                            y2_R = y1_R; y1_R = y;
                            r = Math.Clamp(y, -1.0f, 1.0f);
                        }
                        floatSpan[i + 1] = r;
                    }
                }
            }
            else if (format.BitsPerSample == 16)
            {
                Span<short> shortSpan = MemoryMarshal.Cast<byte, short>(targetSpan);
                for (int i = 0; i < shortSpan.Length; i++)
                {
                    shortSpan[i] = (short)Math.Clamp((int)(shortSpan[i] * curVol), short.MinValue, short.MaxValue);
                }
            }

            buffer.AddSamples(processBuffer, 0, count);
        }

        public void Dispose()
        {
            try { playback.Stop(); } catch { }
            try { playback.Dispose(); } catch { }
        }
    }

    public class AudioDeviceInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public override string ToString() => Name;
    }

    public class OutputConfig
    {
        public string DeviceId { get; set; } = "";
        public float Volume { get; set; } = 100f;
        public bool IsMuted { get; set; }
        public bool BassBoost { get; set; }
    }

    public class StartResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int OutputCount { get; set; }
    }
}
