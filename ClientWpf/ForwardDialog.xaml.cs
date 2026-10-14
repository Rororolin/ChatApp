using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace ChatApp.ClientWpf
{
    public partial class ForwardDialog : Window
    {
        public ChatRowVM? SelectedRoom { get; private set; }

        public ForwardDialog(IEnumerable<ChatRowVM> rooms)
        {
            InitializeComponent();
            RoomList.ItemsSource = rooms;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            if (RoomList.SelectedItem is ChatRowVM row)
            {
                SelectedRoom = row;
                DialogResult = true;
                Close();
            }
            else
                new InfoDialog("Переслать", "Выберите чат для пересылки.") { Owner = this }.ShowDialog();
        }

        private void OnDragWindow(object sender, MouseButtonEventArgs e) => DragMove();
        private void OnCloseDialog(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    }
}
