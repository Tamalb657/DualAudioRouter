using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace DualAudioRouterWPF
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly AudioEngine engine = new();
        private readonly DispatcherTimer peakTimer;
        private float lastRawPeakL = 0f;
        private float lastRawPeakR = 0f;

        private AudioDeviceInfo? selectedMasterDevice;
        private bool isRouting;
        private string statusText = "Audio Engine Idle";
        private string engineState = "○ STANDBY";
        private int selectedLatencyIndex = 1; // 20ms Balanced
        private int syncDelayMs = 0;
        private bool clockDrift = true;
        private bool exclusiveMode = false;

        private double leftPeakPercent;
        private double rightPeakPercent;
        private string leftPeakDbfs = "-∞ dBFS";
        private string rightPeakDbfs = "-∞ dBFS";
        private string monitorStreamDetails = "Engine standby. Click 'Start Audio Routing' to begin streaming.";

        public ObservableCollection<AudioDeviceInfo> AvailableDevices { get; } = new();
        public ObservableCollection<OutputRowViewModel> OutputRows { get; } = new();

        public AudioDeviceInfo? SelectedMasterDevice
        {
            get => selectedMasterDevice;
            set { if (SetField(ref selectedMasterDevice, value)) OnPropertyChanged(nameof(CanStart)); }
        }

        public bool IsRouting
        {
            get => isRouting;
            set
            {
                if (SetField(ref isRouting, value))
                {
                    OnPropertyChanged(nameof(IsNotRouting));
                    OnPropertyChanged(nameof(StartStopButtonText));
                    OnPropertyChanged(nameof(StartStopButtonAppearance));
                }
            }
        }

        public bool IsNotRouting => !IsRouting;
        public bool CanStart => SelectedMasterDevice != null && OutputRows.Count > 0;

        public string StartStopButtonText => IsRouting ? "Stop Audio Routing" : "Start Audio Routing";
        public string StartStopButtonAppearance => IsRouting ? "Danger" : "Primary";

        public string StatusText
        {
            get => statusText;
            set => SetField(ref statusText, value);
        }

        public string EngineState
        {
            get => engineState;
            set => SetField(ref engineState, value);
        }

        public int SelectedLatencyIndex
        {
            get => selectedLatencyIndex;
            set => SetField(ref selectedLatencyIndex, value);
        }

        public int SyncDelayMs
        {
            get => syncDelayMs;
            set => SetField(ref syncDelayMs, value);
        }

        public bool ClockDrift
        {
            get => clockDrift;
            set => SetField(ref clockDrift, value);
        }

        public bool ExclusiveMode
        {
            get => exclusiveMode;
            set => SetField(ref exclusiveMode, value);
        }

        public double LeftPeakPercent
        {
            get => leftPeakPercent;
            set => SetField(ref leftPeakPercent, value);
        }

        public double RightPeakPercent
        {
            get => rightPeakPercent;
            set => SetField(ref rightPeakPercent, value);
        }

        public string LeftPeakDbfs
        {
            get => leftPeakDbfs;
            set => SetField(ref leftPeakDbfs, value);
        }

        public string RightPeakDbfs
        {
            get => rightPeakDbfs;
            set => SetField(ref rightPeakDbfs, value);
        }

        public string MonitorStreamDetails
        {
            get => monitorStreamDetails;
            set => SetField(ref monitorStreamDetails, value);
        }

        public MainViewModel()
        {
            engine.PeakDataAvailable += (l, r) =>
            {
                lastRawPeakL = Math.Max(lastRawPeakL, l);
                lastRawPeakR = Math.Max(lastRawPeakR, r);
            };

            peakTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            peakTimer.Tick += PeakTimer_Tick;

            RefreshDevices();
            AddInitialOutputs();
        }

        private void PeakTimer_Tick(object? sender, EventArgs e)
        {
            if (!IsRouting)
            {
                LeftPeakPercent = 0;
                RightPeakPercent = 0;
                LeftPeakDbfs = "-∞ dBFS";
                RightPeakDbfs = "-∞ dBFS";
                lastRawPeakL = 0;
                lastRawPeakR = 0;
                return;
            }

            float currentL = lastRawPeakL;
            float currentR = lastRawPeakR;

            // Decay
            lastRawPeakL *= 0.65f;
            lastRawPeakR *= 0.65f;

            LeftPeakPercent = Math.Clamp(currentL * 100, 0, 100);
            RightPeakPercent = Math.Clamp(currentR * 100, 0, 100);

            LeftPeakDbfs = currentL > 0.0001f ? $"{20 * Math.Log10(currentL):F1} dBFS" : "-∞ dBFS";
            RightPeakDbfs = currentR > 0.0001f ? $"{20 * Math.Log10(currentR):F1} dBFS" : "-∞ dBFS";
        }

        public void RefreshDevices()
        {
            var oldSelectedId = SelectedMasterDevice?.Id;
            AvailableDevices.Clear();

            var devs = AudioEngine.GetRenderDevices();
            foreach (var d in devs)
            {
                AvailableDevices.Add(d);
            }

            if (AvailableDevices.Count > 0)
            {
                SelectedMasterDevice = AvailableDevices.FirstOrDefault(d => d.Id == oldSelectedId) ?? AvailableDevices[0];
            }

            foreach (var row in OutputRows)
            {
                row.RefreshDevices(AvailableDevices);
            }
        }

        private void AddInitialOutputs()
        {
            if (AvailableDevices.Count > 1)
            {
                AddOutputRow(AvailableDevices[1]);
            }
            else if (AvailableDevices.Count > 0)
            {
                AddOutputRow(AvailableDevices[0]);
            }
        }

        public void AddOutputRow(AudioDeviceInfo? preferredDevice = null)
        {
            var dev = preferredDevice ?? AvailableDevices.FirstOrDefault(d => d.Id != SelectedMasterDevice?.Id) ?? AvailableDevices.FirstOrDefault();
            if (dev == null) return;

            var row = new OutputRowViewModel(this, OutputRows.Count + 1, AvailableDevices, dev);
            OutputRows.Add(row);
            ReindexOutputRows();
            OnPropertyChanged(nameof(CanStart));
        }

        public void RemoveOutputRow(OutputRowViewModel row)
        {
            OutputRows.Remove(row);
            ReindexOutputRows();
            OnPropertyChanged(nameof(CanStart));
        }

        private void ReindexOutputRows()
        {
            for (int i = 0; i < OutputRows.Count; i++)
            {
                OutputRows[i].Index = i + 1;
            }
        }

        public void ToggleRouting()
        {
            if (IsRouting)
            {
                peakTimer.Stop();
                engine.Stop();
                IsRouting = false;
                LeftPeakPercent = 0;
                RightPeakPercent = 0;
                LeftPeakDbfs = "-∞ dBFS";
                RightPeakDbfs = "-∞ dBFS";
                StatusText = "Routing stopped";
                EngineState = "○ IDLE";
                MonitorStreamDetails = "Audio engine idle. Click 'Start Audio Routing' to begin streaming.";
            }
            else
            {
                if (SelectedMasterDevice == null)
                {
                    StatusText = "Error: Select a capture device";
                    return;
                }

                int latency = SelectedLatencyIndex switch
                {
                    0 => 10,
                    2 => 50,
                    _ => 20
                };

                var configs = OutputRows.Select(r => new OutputConfig
                {
                    DeviceId = r.SelectedDevice.Id,
                    Volume = (float)r.Volume,
                    IsMuted = r.IsMuted,
                    BassBoost = r.BassBoost
                }).ToList();

                var res = engine.Start(SelectedMasterDevice.Id, configs, latency, SyncDelayMs);
                if (res.Success)
                {
                    IsRouting = true;
                    peakTimer.Start();
                    StatusText = $"Active | {res.SampleRate}Hz | {res.OutputCount} Output(s)";
                    EngineState = "● ACTIVE";
                    MonitorStreamDetails = $"Loopback Stream: {res.SampleRate:N0} Hz, {res.Channels} Ch, {res.OutputCount} destination(s) active.";
                }
                else
                {
                    StatusText = $"Error: {res.Error}";
                    EngineState = "⚠ FAILED";
                }
            }
        }

        public void Shutdown()
        {
            peakTimer.Stop();
            engine.Dispose();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    public class OutputRowViewModel : INotifyPropertyChanged
    {
        private readonly MainViewModel parent;
        private int index;
        private AudioDeviceInfo selectedDevice;
        private double volume = 100;
        private bool isMuted = false;
        private bool bassBoost = false;

        public int Index
        {
            get => index;
            set { if (SetField(ref index, value)) OnPropertyChanged(nameof(IndexLabel)); }
        }

        public string IndexLabel => $"OUT {Index}";

        public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();

        public AudioDeviceInfo SelectedDevice
        {
            get => selectedDevice;
            set => SetField(ref selectedDevice, value);
        }

        public double Volume
        {
            get => volume;
            set
            {
                if (SetField(ref volume, value))
                    OnPropertyChanged(nameof(VolumeLabel));
            }
        }

        public string VolumeLabel => $"Vol: {Math.Round(Volume)}%";

        public bool IsMuted
        {
            get => isMuted;
            set
            {
                if (SetField(ref isMuted, value))
                {
                    OnPropertyChanged(nameof(MuteButtonText));
                    OnPropertyChanged(nameof(MuteButtonAppearance));
                }
            }
        }

        public string MuteButtonText => IsMuted ? "Muted" : "Mute";
        public string MuteButtonAppearance => IsMuted ? "Danger" : "Secondary";

        public bool BassBoost
        {
            get => bassBoost;
            set => SetField(ref bassBoost, value);
        }

        public OutputRowViewModel(MainViewModel parent, int index, IEnumerable<AudioDeviceInfo> available, AudioDeviceInfo selected)
        {
            this.parent = parent;
            this.index = index;
            this.selectedDevice = selected;
            RefreshDevices(available);
        }

        public void RefreshDevices(IEnumerable<AudioDeviceInfo> available)
        {
            var curId = selectedDevice?.Id;
            Devices.Clear();
            foreach (var d in available) Devices.Add(d);
            SelectedDevice = (curId != null ? Devices.FirstOrDefault(d => d.Id == curId) : null) ?? Devices.FirstOrDefault() ?? selectedDevice!;
        }

        public void Remove()
        {
            parent.RemoveOutputRow(this);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
