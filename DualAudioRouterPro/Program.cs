using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DualAudioRouterPro
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
        private WebView2 webView;
        private AudioEngine engine;
        private NotifyIcon trayIcon;
        private System.Windows.Forms.Timer peakTimer;
        private float lastPeakL, lastPeakR;

        public MainForm()
        {
            engine = new AudioEngine();
            engine.PeakDataAvailable += (l, r) => { lastPeakL = l; lastPeakR = r; };

            InitializeAppIcon();
            InitializeUI();
            InitializeTrayIcon();
            InitializePeakTimer();
        }

        private void InitializeAppIcon()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("DualAudioRouterPro.app.ico"))
                {
                    if (stream != null) { this.Icon = new Icon(stream); return; }
                }
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(iconPath)) this.Icon = new Icon(iconPath);
                else this.Icon = SystemIcons.Application;
            }
            catch { this.Icon = SystemIcons.Application; }
        }

        private async void InitializeUI()
        {
            this.Text = "Dual Audio Router Pro";
            this.Size = new Size(720, 680);
            this.MinimumSize = new Size(640, 520);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(24, 24, 28);

            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(24, 24, 28)
            };

            this.Controls.Add(webView);

            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Path.GetTempPath(), "DualAudioRouterPro_wv2"));

            await webView.EnsureCoreWebView2Async(env);

            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // Map the wwwroot folder as a virtual host so the React app loads
            string wwwrootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local", wwwrootPath,
                CoreWebView2HostResourceAccessKind.Allow);

            webView.CoreWebView2.Navigate("https://app.local/index.html");

            // Hide default WebView2 context menu
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        }

        private void InitializeTrayIcon()
        {
            trayIcon = new NotifyIcon { Icon = this.Icon, Visible = true, Text = "Dual Audio Router Pro" };
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open Router", null, (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; });
            trayMenu.Items.Add("Exit App", null, (s, e) => { trayIcon.Visible = false; Application.Exit(); });
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; };
        }

        private void InitializePeakTimer()
        {
            peakTimer = new System.Windows.Forms.Timer { Interval = 100 };
            peakTimer.Tick += (s, e) =>
            {
                if (engine.IsRunning && webView?.CoreWebView2 != null)
                {
                    try
                    {
                        var msg = JsonSerializer.Serialize(new
                        {
                            type = "peakData",
                            left = Math.Round(lastPeakL * 100, 1),
                            right = Math.Round(lastPeakR * 100, 1)
                        });
                        webView.CoreWebView2.PostWebMessageAsString(msg);
                    }
                    catch { }
                }
            };
            peakTimer.Start();
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(raw)) return;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                string msgType = root.GetProperty("type").GetString();

                switch (msgType)
                {
                    case "getDevices":
                        HandleGetDevices();
                        break;
                    case "startRouting":
                        HandleStartRouting(root);
                        break;
                    case "stopRouting":
                        HandleStopRouting();
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                SendToWeb("error", new { message = ex.Message });
            }
        }

        private void HandleGetDevices()
        {
            var devices = engine.GetRenderDevices();
            var list = new List<object>();
            foreach (var d in devices)
            {
                list.Add(new { id = d.Id, name = d.Name });
            }
            SendToWeb("devices", new { devices = list });
        }

        private void HandleStartRouting(JsonElement root)
        {
            string captureId = root.GetProperty("captureDeviceId").GetString();
            int latency = root.GetProperty("latencyMs").GetInt32();
            int delay = root.GetProperty("delayMs").GetInt32();

            var outputs = new List<OutputConfig>();
            foreach (var item in root.GetProperty("outputs").EnumerateArray())
            {
                outputs.Add(new OutputConfig
                {
                    DeviceId = item.GetProperty("deviceId").GetString(),
                    Volume = (float)item.GetProperty("volume").GetDouble(),
                    IsMuted = item.GetProperty("isMuted").GetBoolean(),
                    BassBoost = item.GetProperty("bassBoost").GetBoolean()
                });
            }

            engine.SetLatency(latency);
            engine.SetDelay(delay);
            var result = engine.Start(captureId, outputs);

            if (result.Success)
            {
                SendToWeb("routingStarted", new
                {
                    sampleRate = result.SampleRate,
                    channels = result.Channels,
                    outputCount = result.OutputCount
                });
            }
            else
            {
                SendToWeb("routingError", new { error = result.Error });
            }
        }

        private void HandleStopRouting()
        {
            engine.Stop();
            SendToWeb("routingStopped", null);
        }

        private void SendToWeb(string type, object data)
        {
            if (webView?.CoreWebView2 == null) return;
            try
            {
                string json;
                if (data != null)
                    json = JsonSerializer.Serialize(new { type, data });
                else
                    json = JsonSerializer.Serialize(new { type });

                webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                trayIcon.ShowBalloonTip(1000, "Audio Router", "Running in background.", ToolTipIcon.Info);
            }
            else
            {
                peakTimer?.Stop();
                engine?.Dispose();
                trayIcon?.Dispose();
                base.OnFormClosing(e);
            }
        }
    }
}
