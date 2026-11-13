using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualAudioRouterPro
{
    public class AudioEngine : IDisposable
    {
        private WasapiLoopbackCapture capture;
        private List<OutputChannel> channels = new List<OutputChannel>();
        private bool isRunning;
        private int latencyMs = 20;
        private int delayMs = 0;

        public bool IsRunning => isRunning;

        public event Action<float, float> PeakDataAvailable;

        public List<DeviceInfo> GetRenderDevices()
        {
            var result = new List<DeviceInfo>();
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var dev in devices)
                {
                    result.Add(new DeviceInfo { Id = dev.ID, Name = dev.FriendlyName });
                }
            }
            catch { }
            return result;
        }

        public void SetLatency(int ms)
        {
            latencyMs = ms;
        }

        public void SetDelay(int ms)
        {
            delayMs = ms;
        }

        public StartResult Start(string captureDeviceId, List<OutputConfig> outputs)
        {
            if (isRunning) return new StartResult { Success = false, Error = "Already running" };

            try
            {
                var enumerator = new MMDeviceEnumerator();

                // Find capture device
                MMDevice captureDevice = null;
                foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (dev.ID == captureDeviceId)
                    {
                        captureDevice = dev;
                        break;
                    }
                }

                if (captureDevice == null)
                    return new StartResult { Success = false, Error = "Capture device not found" };

                capture = new WasapiLoopbackCapture(captureDevice);
                channels.Clear();

                foreach (var cfg in outputs)
                {
                    if (cfg.IsMuted) continue;

                    MMDevice outDev = null;
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
                    return new StartResult { Success = false, Error = "No valid output devices" };

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

        private void OnCaptureDataAvailable(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;

            // Calculate peak levels for the UI meter
            float peakL = 0, peakR = 0;
            if (capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && capture.WaveFormat.BitsPerSample == 32)
            {
                int channels = capture.WaveFormat.Channels;
                for (int i = 0; i < e.BytesRecorded; i += 4 * channels)
                {
                    if (i + 4 <= e.BytesRecorded)
                    {
                        float l = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
                        if (l > peakL) peakL = l;
                    }
                    if (channels > 1 && i + 8 <= e.BytesRecorded)
                    {
                        float r = Math.Abs(BitConverter.ToSingle(e.Buffer, i + 4));
                        if (r > peakR) peakR = r;
                    }
                }
            }
            PeakDataAvailable?.Invoke(peakL, peakR);

            // Fan out to all output channels
            foreach (var ch in channels)
            {
                ch.AddSamples(e.Buffer, e.BytesRecorded);
            }
        }

        public void Stop()
        {
            try { capture?.StopRecording(); } catch { }
            try { capture?.Dispose(); } catch { }
            capture = null;

            foreach (var ch in channels)
            {
                try { ch.Dispose(); } catch { }
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
        private WasapiOut playback;
        private BufferedWaveProvider buffer;
        private float volume;
        private bool bassBoost;

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

            byte[] temp = new byte[count];
            Buffer.BlockCopy(data, 0, temp, 0, count);

            if (Math.Abs(volume - 1.0f) > 0.01f || bassBoost)
            {
                for (int i = 0; i < count; i += 4)
                {
                    if (i + 4 > count) break;
                    float sample = BitConverter.ToSingle(temp, i) * volume;
                    if (bassBoost) sample = Math.Max(-1f, Math.Min(1f, sample * 1.15f));
                    Buffer.BlockCopy(BitConverter.GetBytes(sample), 0, temp, i, 4);
                }
            }

            buffer.AddSamples(temp, 0, count);
        }

        public void Dispose()
        {
            try { playback?.Stop(); } catch { }
            try { playback?.Dispose(); } catch { }
        }
    }

    public class DeviceInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class OutputConfig
    {
        public string DeviceId { get; set; }
        public float Volume { get; set; } = 100;
        public bool IsMuted { get; set; }
        public bool BassBoost { get; set; }
    }

    public class StartResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int OutputCount { get; set; }
    }
}
