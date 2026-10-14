// ============================================================
//  Models.cs — view-model items bound by the UI
// ============================================================
using System;
using System.ComponentModel;

namespace ChatApp.ClientWpf
{
    public sealed class ChatRowVM : INotifyPropertyChanged
    {
        public string RoomId { get; init; } = "";
        public string Kind   { get; init; } = "dm";
        public bool   IsGroup => Kind == "grp" || RoomId.StartsWith("grp:", StringComparison.Ordinal);

        private string _title = "";
        public string Title { get => _title; set { _title = value; Raise(nameof(Title)); } }

        private string _preview = "";
        public string Preview { get => _preview; set { _preview = value; Raise(nameof(Preview)); Raise(nameof(HasPreview)); } }
        public bool HasPreview => !string.IsNullOrEmpty(_preview);

        private int _unreadCount;
        public int UnreadCount { get => _unreadCount; set { _unreadCount = value; Raise(nameof(UnreadCount)); Raise(nameof(HasUnread)); Raise(nameof(UnreadLabel)); } }
        public bool HasUnread => _unreadCount > 0;
        public string UnreadLabel => _unreadCount > 99 ? "99+" : _unreadCount.ToString();

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public sealed class BubbleVM
    {
        public string Sender { get; init; } = "";
        public string SenderLabel { get; init; } = "";
        public string Text { get; init; } = "";
        public bool Own { get; init; }
        public bool IsSystem { get; init; }
        public DateTime? Time { get; init; }
        public bool Latest { get; init; }
        public bool ShowSenderLabel { get; init; }  // for incoming in groups
        public bool ShowAvatar { get; init; } = true;  // last message in incoming streak
    }
}
