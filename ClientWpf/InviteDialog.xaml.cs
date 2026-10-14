// ============================================================
//  InviteDialog.xaml.cs
// ============================================================
using System.Windows;

namespace ChatApp.ClientWpf
{
    public partial class InviteDialog : Window
    {
        public string UserInput => Tb.Text.Trim();
        public InviteDialog() { InitializeComponent(); }
        private void OnOk(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    }
}
