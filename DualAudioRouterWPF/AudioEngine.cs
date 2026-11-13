using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualAudioRouterWPF
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

        public StartResult Start(string captureDeviceId, List<OutputConfig> outputs, int latency, int delay)
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
                    if (cfg.IsMuted) continue;

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
                        var ch = new OutputChannel(outDev, capture.WaveFormat, latencyMs, delayMs, cfg.Volume / 100f, cfg.BassBoost);
                        channels.Add(ch);
                    }
                }

                if (channels.Count == 0)
                    return new StartResult { Success = false, Error = "No active output destinations selected" };

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

        private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0 || capture == null) return;

            float peakL = 0f, peakR = 0f;
            var format = capture.WaveFormat;

            // Zero-allocation SIMD-friendly span peak measurement
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
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
            for (int i = 0; i < channels.Count; i++)
            {
                channels[i].AddSamples(e.Buffer, e.BytesRecorded);
            }
        }

        public void Stop()
        {
            try { capture?.StopRecording(); } catch { }
            try { capture?.Dispose(); } catch { }
            capture = null;

            for (int i = 0; i < channels.Count; i++)
            {
                try { channels[i].Dispose(); } catch { }
            }
            channels.Clear();
            isRunning = false;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    public class OutputChannel : IDisposable
    {
        private readonly WasapiOut playback;
        private readonly BufferedWaveProvider buffer;
        private readonly float volume;
        private readonly bool bassBoost;
        private byte[] processBuffer = new byte[8192];

        public OutputChannel(MMDevice device, WaveFormat format, int latencyMs, int delayMs, float vol, bool bass)
        {
            volume = vol;
            bassBoost = bass;

            buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(100 + delayMs),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            playback = new WasapiOut(device, AudioClientShareMode.Shared, false, latencyMs);
            playback.Init(buffer);
            playback.Play();
        }

        public void AddSamples(byte[] data, int count)
        {
            if (count <= 0) return;

            // Reuse existing processBuffer without heap allocations
            if (processBuffer.Length < count)
            {
                processBuffer = new byte[Math.Max(count, processBuffer.Length * 2)];
            }

            Span<byte> targetSpan = processBuffer.AsSpan(0, count);
            data.AsSpan(0, count).CopyTo(targetSpan);

            // DSP Gain and Bass Boost in-place without BitConverter allocations
            if (Math.Abs(volume - 1.0f) > 0.001f || bassBoost)
            {
                var format = buffer.WaveFormat;
                if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
                {
                    Span<float> floatSpan = MemoryMarshal.Cast<byte, float>(targetSpan);
                    for (int i = 0; i < floatSpan.Length; i++)
                    {
                        float sample = floatSpan[i] * volume;
                        if (bassBoost)
                        {
                            sample = Math.Clamp(sample * 1.15f, -1.0f, 1.0f);
                        }
                        floatSpan[i] = sample;
                    }
                }
                else if (format.BitsPerSample == 16)
                {
                    Span<short> shortSpan = MemoryMarshal.Cast<byte, short>(targetSpan);
                    for (int i = 0; i < shortSpan.Length; i++)
                    {
                        float val = shortSpan[i] * volume;
                        if (bassBoost)
                        {
                            val = Math.Clamp(val * 1.15f, short.MinValue, short.MaxValue);
                        }
                        shortSpan[i] = (short)Math.Clamp((int)val, short.MinValue, short.MaxValue);
                    }
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
