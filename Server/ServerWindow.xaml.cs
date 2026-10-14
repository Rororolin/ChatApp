// ============================================================
//  ServerWindow.xaml.cs
// ============================================================
using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace ChatApp.Server
{
    public partial class ServerWindow : Window
    {
        private int _connections;

        public ServerWindow()
        {
            InitializeComponent();

            PortLabel.Text = $"порт {Program.Port}";

            Program.UILogger = AppendLog;

            Loaded += (_, _) => StartServer();
            Closed += (_, _) => Program.Stop();
        }

        private void StartServer()
        {
            _connections = 0;
            ConnLabel.Text = "";
            BtnStart.Visibility = Visibility.Collapsed;
            BtnStop.IsEnabled   = true;
            StatusLabel.Text       = $"Работает · порт {Program.Port}";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0xAA, 0x45));

            new Thread(Program.Run) { IsBackground = true, Name = "ServerRun" }.Start();
        }

        private void AppendLog(string line)
        {
            if (!CheckAccess()) { Dispatcher.Invoke(() => AppendLog(line)); return; }

            if (line.Contains("[+]"))
                ConnLabel.Text = $"онлайн: {++_connections}";
            else if (line.Contains("[-]") && _connections > 0)
                ConnLabel.Text = $"онлайн: {--_connections}";

            LogBlock.Text += line + "\n";
            LogScroll.ScrollToEnd();

            if (LogBlock.Text.Length > 200_000)
                LogBlock.Text = LogBlock.Text[^100_000..];
        }

        private void OnStop(object sender, RoutedEventArgs e)
        {
            Program.Stop();
            _connections = 0;
            ConnLabel.Text = "";
            StatusLabel.Text       = "Остановлен";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x3A, 0x2E));
            BtnStop.IsEnabled   = false;
            BtnStart.Visibility = Visibility.Visible;
            AppendLog($"[{DateTime.Now:HH:mm:ss}]  Сервер остановлен.");
        }

        private void OnStart(object sender, RoutedEventArgs e)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}]  Перезапуск сервера…");
            StartServer();
        }
    }
}
