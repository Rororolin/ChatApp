// ============================================================
//  LoginWindow.xaml.cs
// ============================================================
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ChatApp.Client;
using ChatApp.Shared;

namespace ChatApp.ClientWpf
{
    public partial class LoginWindow : Window
    {
        private string ServerIp   => TbIp.Text.Trim();
        private int    ServerPort => int.TryParse(TbPort.Text, out int p) ? p : 5000;
        private string UserName   => UsernameRules.Normalize(TbUser.Text);
        private string DisplayName => TbDisplay.Text.Trim();
        private string AuthMode   => RbRegister.IsChecked == true ? "register" : "login";
        private string Password   => PbPassword.Password;

        public LoginWindow()
        {
            InitializeComponent();
            PassRules.Text = PasswordRules.RequirementsSummaryRu;
            Loaded += (_, _) => DrawSpeckles();
            SizeChanged += (_, _) => DrawSpeckles();
        }

        private void DrawSpeckles()
        {
            SpeckleCanvas.Children.Clear();
            var rnd = new Random(7);
            var brush = new SolidColorBrush(Color.FromArgb(60, 0x9C, 0xC9, 0x7A));
            brush.Freeze();
            for (int i = 0; i < 90; i++)
            {
                double size = rnd.NextDouble() * 2 + 2;
                var dot = new Ellipse
                {
                    Width = size, Height = size, Fill = brush,
                };
                Canvas.SetLeft(dot, rnd.NextDouble() * SpeckleCanvas.ActualWidth);
                Canvas.SetTop(dot, rnd.NextDouble() * SpeckleCanvas.ActualHeight);
                SpeckleCanvas.Children.Add(dot);
            }
        }

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (DisplayPanel == null) return;
            bool reg = RbRegister.IsChecked == true;
            DisplayPanel.Visibility = reg ? Visibility.Visible : Visibility.Collapsed;
            PassRules.Visibility    = reg ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnConnect(object sender, RoutedEventArgs e)
        {
            StatusLabel.Text = "";

            string? userErr = UsernameRules.GetValidationError(UserName);
            if (userErr != null) { StatusLabel.Text = userErr; TbUser.Focus(); return; }

            bool reg = RbRegister.IsChecked == true;
            if (reg)
            {
                if (string.IsNullOrWhiteSpace(DisplayName))
                {
                    StatusLabel.Text = "При регистрации введите имя.";
                    TbDisplay.Focus(); return;
                }
                if (DisplayName.Length > 60)
                {
                    StatusLabel.Text = "Имя слишком длинное (максимум 60 символов).";
                    TbDisplay.Focus(); return;
                }
            }

            string pw = Password;
            if (string.IsNullOrEmpty(pw))
            {
                StatusLabel.Text = "Введите пароль.";
                PbPassword.Focus(); return;
            }
            if (pw.Length > PasswordRules.MaxLength)
            {
                StatusLabel.Text = $"Пароль слишком длинный: не больше {PasswordRules.MaxLength} символов.";
                PbPassword.Focus(); return;
            }
            if (reg)
            {
                string? pwErr = PasswordRules.GetRegistrationError(pw);
                if (pwErr != null) { StatusLabel.Text = pwErr; PbPassword.Focus(); return; }
            }
            if (!int.TryParse(TbPort.Text, out int port) || port < 1 || port > 65535)
            {
                StatusLabel.Text = "Некорректный порт (1–65535).";
                TbPort.Focus(); return;
            }

            BtnConnect.IsEnabled = false;
            BtnConnect.Content   = "Подключение…";

            var session = new SessionInfo
            {
                ServerIp   = ServerIp,
                ServerPort = ServerPort,
                UserName   = UserName,
                DisplayName = DisplayName,
                AuthMode   = AuthMode,
                Password   = Password,
            };
            new Thread(() => DoConnect(session)) { IsBackground = true }.Start();
        }

        private void DoConnect(SessionInfo session)
        {
            var client = new ChatClient(
                session.ServerIp, session.ServerPort,
                session.UserName, session.DisplayName,
                session.AuthMode, session.Password);

            using var gate = new ManualResetEventSlim(false);
            string? joinFail = null;

            client.OnSessionReady += () => { client.HoldReceiving(); gate.Set(); };
            client.OnJoinFailed   += m  => { joinFail = m; gate.Set(); };

            try
            {
                client.Connect();
                client.StartReceiving();
                gate.Wait(15_000);
            }
            catch (Exception ex)
            {
                client.Disconnect();
                Dispatcher.Invoke(() =>
                {
                    StatusLabel.Text     = $"Не удалось подключиться: {ex.Message}";
                    BtnConnect.IsEnabled = true;
                    BtnConnect.Content   = "Подключиться  →";
                });
                return;
            }

            if (joinFail != null || !gate.IsSet)
            {
                client.Disconnect();
                string msg = joinFail ?? "Время ожидания истекло. Проверьте адрес сервера.";
                Dispatcher.Invoke(() =>
                {
                    StatusLabel.Text     = msg;
                    BtnConnect.IsEnabled = true;
                    BtnConnect.Content   = "Подключиться  →";
                });
                return;
            }

            Dispatcher.Invoke(() =>
            {
                App.Session = session;
                var chat = new ChatWindow(session, client);
                Application.Current.MainWindow = chat;
                chat.Show();
                Close();
            });
        }
    }
}
