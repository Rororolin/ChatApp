using System.Windows;
using System.Windows.Input;

namespace ChatApp.ClientWpf
{
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog(string header, string body, string confirmLabel = "Подтвердить")
        {
            InitializeComponent();
            TbHeader.Text = header;
            TbBody.Text = body;
            BtnConfirm.Content = confirmLabel;
            TbTitle.Text = header;
        }

        private void OnConfirm(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
        private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
        private void OnDragWindow(object sender, MouseButtonEventArgs e) => DragMove();
    }
}
