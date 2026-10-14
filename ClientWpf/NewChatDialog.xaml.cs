// ============================================================
//  NewChatDialog.xaml.cs
// ============================================================
using System.Windows;

namespace ChatApp.ClientWpf
{
    public enum NewChatKind { Direct, Group }

    public partial class NewChatDialog : Window
    {
        public NewChatKind Kind => RbDm.IsChecked == true ? NewChatKind.Direct : NewChatKind.Group;
        public string DirectUserInput => TbDm.Text.Trim();
        public string GroupTitle => TbName.Text.Trim();

        public NewChatDialog() { InitializeComponent(); }

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (DmPanel == null) return;
            bool dm = RbDm.IsChecked == true;
            DmPanel.Visibility  = dm ? Visibility.Visible : Visibility.Collapsed;
            GrpPanel.Visibility = dm ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnCreate(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
