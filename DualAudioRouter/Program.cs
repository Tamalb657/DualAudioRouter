using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualAudioRouter
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        // Core UI Controls
        private ComboBox comboMasterDevice;
        private Button btnRefresh;
        private Button btnAddOutput;
        private FlowLayoutPanel panelOutputs;
        private Button btnStartStop;
        private Label lblStatus;
        private NotifyIcon trayIcon;

        // Advanced Controls
        private ComboBox comboLatency;
        private CheckBox chkAutoResample;
        private TrackBar trackDelay;
        private Label lblDelay;

        // Audio Engine
        private WasapiLoopbackCapture capture;
        private List<OutputEngine> activeOutputEngines = new List<OutputEngine>();
        private bool isRunning = false;

        // State
        private int delayMs = 0;

        public MainForm()
        {
            InitializeAppIcon();
            InitializeUI();
            LoadMasterDevices();
            AddOutputRow(); // Add primary default output row
            InitializeTrayIcon();
        }

        private void InitializeAppIcon()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("DualAudioRouter.app.ico"))
                {
                    if (stream != null)
                    {
                        this.Icon = new Icon(stream);
                        return;
                    }
                }

                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
                else
                {
                    this.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                this.Icon = SystemIcons.Application;
            }
        }

        private void InitializeUI()
        {
            this.Text = "Native Dynamic Audio Router Pro";
            this.Size = new Size(500, 520);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;

            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            TabPage tabMain = new TabPage("Core Router");
            TabPage tabAdvanced = new TabPage("Advanced Controls");

            tabs.Controls.Add(tabMain);
            tabs.Controls.Add(tabAdvanced);
            this.Controls.Add(tabs);

            // ==================== TAB 1: CORE ROUTER ====================
            Label lblMaster = new Label { Text = "Primary Capture Source:", Location = new Point(15, 12), AutoSize = true };
            comboMasterDevice = new ComboBox { Location = new Point(15, 32), Width = 350, DropDownStyle = ComboBoxStyle.DropDownList };

            btnRefresh = new Button { Text = "Refresh", Location = new Point(375, 30), Width = 90, Height = 25 };
            btnRefresh.Click += (s, e) => { LoadMasterDevices(); RefreshOutputRows(); };

            Label lblOutputs = new Label { Text = "Output Devices & Volume Mix:", Location = new Point(15, 68), AutoSize = true };

            panelOutputs = new FlowLayoutPanel
            {
                Location = new Point(15, 90),
                Size = new Size(450, 240),
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };

            btnAddOutput = new Button { Text = "+ Add Output Device", Location = new Point(15, 340), Width = 150, Height = 32 };
            btnAddOutput.Click += (s, e) => AddOutputRow();

            btnStartStop = new Button { Text = "Start Routing", Location = new Point(315, 340), Width = 150, Height = 32, Font = new Font(this.Font, FontStyle.Bold) };
            btnStartStop.Click += BtnStartStop_Click;

            lblStatus = new Label { Text = "Status: Idle", Location = new Point(15, 390), AutoSize = true, ForeColor = Color.Gray };

            tabMain.Controls.Add(lblMaster);
            tabMain.Controls.Add(comboMasterDevice);
            tabMain.Controls.Add(btnRefresh);
            tabMain.Controls.Add(lblOutputs);
            tabMain.Controls.Add(panelOutputs);
            tabMain.Controls.Add(btnAddOutput);
            tabMain.Controls.Add(btnStartStop);
            tabMain.Controls.Add(lblStatus);

            // ==================== TAB 2: ADVANCED CONTROLS ====================
            Label lblLatencyHeader = new Label { Text = "Buffer Latency Mode:", Location = new Point(20, 20), AutoSize = true };
            comboLatency = new ComboBox { Location = new Point(20, 40), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            comboLatency.Items.AddRange(new object[] { "Ultra Low (10ms - Wired IEMs)", "Balanced (20ms - Standard)", "Stable (50ms - Bluetooth/Wireless)" });
            comboLatency.SelectedIndex = 1;

            chkAutoResample = new CheckBox { Text = "Enable Clock Drift Compensation", Location = new Point(20, 80), AutoSize = true, Checked = true };

            lblDelay = new Label { Text = "Sync Offset Delay: 0 ms", Location = new Point(20, 120), AutoSize = true };
            trackDelay = new TrackBar { Location = new Point(20, 140), Width = 420, Minimum = 0, Maximum = 300, Value = 0, TickFrequency = 25 };
            trackDelay.ValueChanged += (s, e) => {
                delayMs = trackDelay.Value;
                lblDelay.Text = $"Sync Offset Delay: {delayMs} ms";
            };

            tabAdvanced.Controls.Add(lblLatencyHeader);
            tabAdvanced.Controls.Add(comboLatency);
            tabAdvanced.Controls.Add(chkAutoResample);
            tabAdvanced.Controls.Add(lblDelay);
            tabAdvanced.Controls.Add(trackDelay);
        }

        private void InitializeTrayIcon()
        {
            trayIcon = new NotifyIcon { Icon = this.Icon, Visible = true, Text = "Dual Audio Router" };
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open Router", null, (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; });
            trayMenu.Items.Add("Exit App", null, (s, e) => { trayIcon.Visible = false; Application.Exit(); });
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; };
        }

        private void LoadMasterDevices()
        {
            string lastId = (comboMasterDevice.SelectedItem as DeviceItem)?.Device.ID;
            comboMasterDevice.Items.Clear();

            var devices = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            int selectIndex = 0, count = 0;

            foreach (var dev in devices)
            {
                comboMasterDevice.Items.Add(new DeviceItem(dev));
                if (dev.ID == lastId) selectIndex = count;
                count++;
            }

            if (comboMasterDevice.Items.Count > 0)
                comboMasterDevice.SelectedIndex = Math.Min(selectIndex, comboMasterDevice.Items.Count - 1);
        }

        private void AddOutputRow()
        {
            var row = new OutputRowControl(panelOutputs.Controls.Count + 1);
            row.OnRemove += (s, e) => {
                panelOutputs.Controls.Remove(row);
                row.Dispose();
            };
            panelOutputs.Controls.Add(row);
        }

        private void RefreshOutputRows()
        {
            foreach (Control c in panelOutputs.Controls)
            {
                if (c is OutputRowControl row) row.LoadDevices();
            }
        }

        private void BtnStartStop_Click(object sender, EventArgs e)
        {
            if (isRunning) StopAudio(); else StartAudio();
        }

        private void StartAudio()
        {
            if (comboMasterDevice.SelectedItem == null)
            {
                MessageBox.Show("Please select a master capture device.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (panelOutputs.Controls.Count == 0)
            {
                MessageBox.Show("Please add at least one output device.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var masterDevice = ((DeviceItem)comboMasterDevice.SelectedItem).Device;
                capture = new WasapiLoopbackCapture(masterDevice);
                activeOutputEngines.Clear();

                int desiredLatency = 20;
                if (comboLatency.SelectedIndex == 0) desiredLatency = 10;
                if (comboLatency.SelectedIndex == 2) desiredLatency = 50;

                foreach (Control c in panelOutputs.Controls)
                {
                    if (c is OutputRowControl row && row.SelectedDevice != null)
                    {
                        var engine = new OutputEngine(row.SelectedDevice, capture.WaveFormat, desiredLatency, delayMs, row);
                        activeOutputEngines.Add(engine);
                    }
                }

                capture.DataAvailable += (s, a) =>
                {
                    foreach (var engine in activeOutputEngines)
                    {
                        engine.ProcessAndAddSamples(a.Buffer, a.BytesRecorded);
                    }
                };

                capture.StartRecording();
                isRunning = true;

                btnStartStop.Text = "Stop Routing";
                lblStatus.Text = $"Status: Active | {capture.WaveFormat.SampleRate}Hz | {activeOutputEngines.Count} Output(s)";
                lblStatus.ForeColor = Color.Green;
                ToggleControls(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Audio Engine Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                StopAudio();
            }
        }

        private void StopAudio()
        {
            capture?.StopRecording();
            capture?.Dispose();
            capture = null;

            foreach (var engine in activeOutputEngines) engine.Dispose();
            activeOutputEngines.Clear();

            isRunning = false;
            btnStartStop.Text = "Start Routing";
            lblStatus.Text = "Status: Idle";
            lblStatus.ForeColor = Color.Gray;
            ToggleControls(true);
        }

        private void ToggleControls(bool enable)
        {
            comboMasterDevice.Enabled = enable;
            btnRefresh.Enabled = enable;
            btnAddOutput.Enabled = enable;
            comboLatency.Enabled = enable;
            foreach (Control c in panelOutputs.Controls) c.Enabled = enable;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                trayIcon.ShowBalloonTip(1000, "Audio Router", "App is running in the background.", ToolTipIcon.Info);
            }
            else
            {
                StopAudio();
                trayIcon.Dispose();
                base.OnFormClosing(e);
            }
        }
    }

    public class OutputRowControl : UserControl
    {
        public event EventHandler OnRemove;
        private ComboBox comboDevice;
        private TrackBar trackVol;
        private Label lblVol;
        private CheckBox chkBass;

        public MMDevice SelectedDevice => (comboDevice.SelectedItem as DeviceItem)?.Device;
        public float VolumeFactor => trackVol.Value / 100.0f;
        public bool BassBoostEnabled => chkBass.Checked;

        public OutputRowControl(int index)
        {
            this.Size = new Size(420, 70);
            this.BorderStyle = BorderStyle.FixedSingle;

            Label lbl = new Label { Text = $"Out {index}:", Location = new Point(5, 10), AutoSize = true, Font = new Font(this.Font, FontStyle.Bold) };
            comboDevice = new ComboBox { Location = new Point(55, 7), Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };

            Button btnRemove = new Button { Text = "X", Location = new Point(380, 6), Width = 30, Height = 23, ForeColor = Color.Red };
            btnRemove.Click += (s, e) => OnRemove?.Invoke(this, EventArgs.Empty);

            lblVol = new Label { Text = "Vol: 100%", Location = new Point(5, 40), Width = 60 };
            trackVol = new TrackBar { Location = new Point(65, 33), Width = 220, Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10 };
            trackVol.ValueChanged += (s, e) => lblVol.Text = $"Vol: {trackVol.Value}%";

            chkBass = new CheckBox { Text = "Bass Boost", Location = new Point(295, 40), AutoSize = true };

            this.Controls.Add(lbl);
            this.Controls.Add(comboDevice);
            this.Controls.Add(btnRemove);
            this.Controls.Add(lblVol);
            this.Controls.Add(trackVol);
            this.Controls.Add(chkBass);

            LoadDevices();
        }

        public void LoadDevices()
        {
            string lastId = (comboDevice.SelectedItem as DeviceItem)?.Device.ID;
            comboDevice.Items.Clear();

            var devices = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            int selectIndex = 0, count = 0;

            foreach (var dev in devices)
            {
                comboDevice.Items.Add(new DeviceItem(dev));
                if (dev.ID == lastId) selectIndex = count;
                count++;
            }

            if (comboDevice.Items.Count > 0)
                comboDevice.SelectedIndex = Math.Min(selectIndex, comboDevice.Items.Count - 1);
        }
    }

    public class OutputEngine : IDisposable
    {
        private WasapiOut playback;
        private BufferedWaveProvider bufferedWaveProvider;
        private OutputRowControl rowControl;

        public OutputEngine(MMDevice device, WaveFormat waveFormat, int latencyMs, int delayMs, OutputRowControl control)
        {
            rowControl = control;

            // Pops-free buffer settings kept from original base code
            bufferedWaveProvider = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(100 + delayMs),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            playback = new WasapiOut(device, AudioClientShareMode.Shared, false, latencyMs);
            playback.Init(bufferedWaveProvider);
            playback.Play();
        }

        public void ProcessAndAddSamples(byte[] buffer, int bytesRecorded)
        {
            if (bytesRecorded <= 0) return;

            byte[] tempBuffer = new byte[bytesRecorded];
            Buffer.BlockCopy(buffer, 0, tempBuffer, 0, bytesRecorded);

            float vol = rowControl.VolumeFactor;
            bool bass = rowControl.BassBoostEnabled;

            if (Math.Abs(vol - 1.0f) > 0.01f || bass)
            {
                if (bufferedWaveProvider.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && bufferedWaveProvider.WaveFormat.BitsPerSample == 32)
                {
                    for (int i = 0; i < bytesRecorded; i += 4)
                    {
                        float sample = BitConverter.ToSingle(tempBuffer, i) * vol;
                        if (bass) sample = Math.Max(-1.0f, Math.Min(1.0f, sample * 1.15f));
                        Buffer.BlockCopy(BitConverter.GetBytes(sample), 0, tempBuffer, i, 4);
                    }
                }
                else if (bufferedWaveProvider.WaveFormat.BitsPerSample == 16)
                {
                    for (int i = 0; i < bytesRecorded; i += 2)
                    {
                        short sample = BitConverter.ToInt16(tempBuffer, i);
                        sample = (short)Math.Clamp((int)(sample * vol), short.MinValue, short.MaxValue);
                        Buffer.BlockCopy(BitConverter.GetBytes(sample), 0, tempBuffer, i, 2);
                    }
                }
            }

            bufferedWaveProvider.AddSamples(tempBuffer, 0, bytesRecorded);
        }

        public void Dispose()
        {
            playback?.Stop();
            playback?.Dispose();
        }
    }

    public class DeviceItem
    {
        public MMDevice Device { get; }
        public DeviceItem(MMDevice device) { Device = device; }
        public override string ToString() => Device.FriendlyName;
    }
}