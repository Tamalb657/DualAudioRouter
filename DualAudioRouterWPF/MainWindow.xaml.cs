using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Wpf.Ui.Controls;

namespace DualAudioRouterWPF
{
    public partial class MainWindow : FluentWindow
    {
        private readonly MainViewModel viewModel = new();
        private NotifyIcon? trayIcon;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = viewModel;
            InitializeTrayIcon();
        }

        private void InitializeTrayIcon()
        {
            try
            {
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                Icon winIcon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;

                trayIcon = new NotifyIcon
                {
                    Icon = winIcon,
                    Visible = true,
                    Text = "Dual Audio Router Pro"
                };

                var contextMenu = new ContextMenuStrip();
                contextMenu.Items.Add("Open Router", null, (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        Show();
                        WindowState = WindowState.Normal;
                        Activate();
                    });
                });
                contextMenu.Items.Add("Exit", null, (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        trayIcon.Visible = false;
                        trayIcon.Dispose();
                        trayIcon = null;
                        viewModel.Shutdown();
                        System.Windows.Application.Current.Shutdown();
                    });
                });

                trayIcon.ContextMenuStrip = contextMenu;
                trayIcon.DoubleClick += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        Show();
                        WindowState = WindowState.Normal;
                        Activate();
                    });
                };
            }
            catch
            {
            }
        }

        private void BtnStartStop_Click(object sender, RoutedEventArgs e)
        {
            viewModel.ToggleRouting();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            viewModel.RefreshDevices();
        }

        private void BtnAddOutput_Click(object sender, RoutedEventArgs e)
        {
            viewModel.AddOutputRow();
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is OutputRowViewModel row)
            {
                row.IsMuted = !row.IsMuted;
            }
        }

        private void BtnRemoveOutput_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is OutputRowViewModel row)
            {
                row.Remove();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            trayIcon?.Dispose();
            trayIcon = null;
            viewModel.Shutdown();
            base.OnClosing(e);
        }
    }
}
