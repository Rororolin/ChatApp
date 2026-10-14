using System.Windows;

namespace ChatApp.ClientWpf
{
    public partial class App : Application
    {
        public static SessionInfo? Session { get; set; }

        private void OnStartup(object sender, StartupEventArgs e)
        {
            var login = new LoginWindow();
            MainWindow = login;
            login.Show();
        }
    }

    public sealed class SessionInfo
    {
        public string ServerIp = "";
        public int ServerPort;
        public string UserName = "";
        public string DisplayName = "";
        public string AuthMode = "";
        public string Password = "";
    }
}
