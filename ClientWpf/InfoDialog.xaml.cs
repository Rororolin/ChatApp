using System.Windows;
using System.Windows.Input;

namespace ChatApp.ClientWpf
{
    public partial class InfoDialog : Window
    {
        public InfoDialog(string title, string body)
        {
            InitializeComponent();
            TbTitle.Text = title;
            TbBody.Text  = body;
        }

        private void OnOk(object sender, RoutedEventArgs e) => Close();
        private void OnDragWindow(object sender, MouseButtonEventArgs e) => DragMove();
    }
}
