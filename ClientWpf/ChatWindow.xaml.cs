// ============================================================
//  ChatWindow.xaml.cs — Sprout messenger, wired to ChatClient
// ============================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ChatApp.Client;        // ChatClient lives here
using ChatApp.Shared;

namespace ChatApp.ClientWpf
{
    public partial class ChatWindow : Window, INotifyPropertyChanged
    {
        // --- session/networking ---
        private ChatClient _client;
        private readonly SessionInfo _session;
        private volatile bool _reconnecting = false;
        private readonly Dictionary<string, List<MsgLine>> _history = new();
        private readonly Dictionary<string, string> _userLabels = new();
        private BubbleVM? _replyBubble;
        private readonly Queue<ChatPacket> _offlineQueue = new();
        private readonly ManualResetEventSlim _reconnectNow = new(false);

        // --- VM state ---
        public ObservableCollection<ChatRowVM> Rooms { get; } = new();
        public ObservableCollection<BubbleVM> Bubbles { get; } = new();

        private string _currentRoom = "";

        // --- dependency props observed by XAML ---
        public static readonly DependencyProperty HasPeerProperty =
            DependencyProperty.Register(nameof(HasPeer), typeof(bool), typeof(ChatWindow), new PropertyMetadata(false));
        public bool HasPeer
        {
            get => (bool)GetValue(HasPeerProperty);
            set => SetValue(HasPeerProperty, value);
        }

        public static readonly DependencyProperty PeerIsGroupProperty =
            DependencyProperty.Register(nameof(PeerIsGroup), typeof(bool), typeof(ChatWindow), new PropertyMetadata(false));
        public bool PeerIsGroup
        {
            get => (bool)GetValue(PeerIsGroupProperty);
            set => SetValue(PeerIsGroupProperty, value);
        }

        public ChatWindow(SessionInfo session, ChatClient connectedClient)
        {
            _session = session;
            _client  = connectedClient;

            InitializeComponent();

            ChatList.ItemsSource     = Rooms;
            MessagesList.ItemsSource = Bubbles;

            Title = $"Sprout — {session.DisplayName} (@{session.UserName})";
            OnlineLabel.Text = $"@{session.UserName} · в сети";

            WireClientEvents();
            _client.ResumeReceiving();

            Loaded += (_, _) =>
            {
                SetStatus("E2EE · ключ активен", (Brush)FindResource("Online"));
                BtnNewChat.IsEnabled = true;
            };
            Closed += (_, _) => { _reconnecting = false; _client.Disconnect(); };
        }

        private sealed class MsgLine
        {
            public string Sender = "";
            public string Text = "";
            public bool Own;
            public bool System;
            public long? UtcTicks;
        }

        private void ReopenLogin()
        {
            var login = new LoginWindow();
            Application.Current.MainWindow = login;
            login.Show();
            Close();
        }

        private void OnLogout(object sender, RoutedEventArgs e)
        {
            var dlg = new ConfirmDialog(
                "Выйти из аккаунта?",
                "Вы выйдете из профиля. Чаты и история сохранятся на сервере.",
                "Выйти") { Owner = this };
            if (dlg.ShowDialog() != true) return;

            _reconnecting = false;
            _client.Disconnect();
            ReopenLogin();
        }

        private void OnReconnectNow(object sender, RoutedEventArgs e) => _reconnectNow.Set();

        private void SetStatus(string text, Brush brush)
        {
            StatusFooter.Foreground = brush;
            StatusFooter.Text = text;
        }

        private void WireClientEvents()
        {
            _client.OnMessage      += OnMessage;
            _client.OnUsersUpdate  += OnUsersUpdate;
            _client.OnRoomsSync    += OnRoomsSync;
            _client.OnRoomError    += OnRoomError;
            _client.OnRoomHistory  += OnRoomHistory;
            _client.OnDisconnected += OnDisconnected;
        }

        private void UnwireClientEvents()
        {
            _client.OnMessage      -= OnMessage;
            _client.OnUsersUpdate  -= OnUsersUpdate;
            _client.OnRoomsSync    -= OnRoomsSync;
            _client.OnRoomError    -= OnRoomError;
            _client.OnRoomHistory  -= OnRoomHistory;
            _client.OnDisconnected -= OnDisconnected;
        }

        private void ReconnectLoop()
        {
            for (int attempt = 1; _reconnecting; attempt++)
            {
                int delaySec = Math.Min(attempt * 2, 15);
                Dispatcher.Invoke(() =>
                    SetStatus($"Соединение разорвано · попытка {attempt}", (Brush)FindResource("Danger")));
                _reconnectNow.Reset();
                _reconnectNow.Wait(delaySec * 1000);  // прерывается кнопкой «Обновить»
                if (!_reconnecting) return;

                // Try login first; if the server restarted and lost the account, fall back to register.
                foreach (string mode in new[] { "login", "register" })
                {
                    if (!_reconnecting) return;
                    if (!TryConnectOnce(mode, out var newClient)) continue;

                    Dispatcher.Invoke(() =>
                    {
                        UnwireClientEvents();
                        _client.Disconnect();
                        _client = newClient!;
                        WireClientEvents();
                        _reconnecting = false;
                        // Drain messages typed while offline
                        lock (_offlineQueue)
                        {
                            while (_offlineQueue.Count > 0)
                                _client.Send(_offlineQueue.Dequeue());
                        }
                        SetStatus("E2EE · ключ активен", (Brush)FindResource("Online"));
                        OnlineLabel.Text = $"@{_session.UserName} · в сети";
                        BtnNewChat.IsEnabled = true;
                        BtnReconnectNow.Visibility = Visibility.Collapsed;
                        if (!string.IsNullOrEmpty(_currentRoom))
                            RequestRoomHistory(_currentRoom);
                    });
                    return;
                }
                // Both modes failed — wait longer and retry.
            }
        }

        private bool TryConnectOnce(string authMode, out ChatClient? newClient)
        {
            newClient = null;
            try
            {
                var c = new ChatClient(
                    _session.ServerIp, _session.ServerPort,
                    _session.UserName, _session.DisplayName,
                    authMode, _session.Password);

                using var gate = new ManualResetEventSlim(false);
                string? fail = null;
                c.OnSessionReady += () => gate.Set();
                c.OnJoinFailed   += m  => { fail = m; gate.Set(); };

                c.Connect();
                c.StartReceiving();
                gate.Wait(10_000);

                if (fail != null || !gate.IsSet) { c.Disconnect(); return false; }
                newClient = c;
                return true;
            }
            catch { return false; }
        }

        // ============================================================
        //   Sidebar / chat list interactions
        // ============================================================
        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            string q = TbSearch.Text.Trim().ToLowerInvariant();
            // Filter Rooms by Title or RoomId. Use ListBox ItemsSource filtering.
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Rooms);
            if (string.IsNullOrEmpty(q)) { view.Filter = null; }
            else
            {
                view.Filter = o =>
                {
                    if (o is ChatRowVM r)
                        return r.Title.ToLowerInvariant().Contains(q) ||
                               r.RoomId.ToLowerInvariant().Contains(q);
                    return true;
                };
            }
            EmptyChats.Visibility = view.Cast<object>().Any() ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnChatSelected(object sender, SelectionChangedEventArgs e)
        {
            if (ChatList.SelectedItem is not ChatRowVM row)
            {
                ClearActiveChat();
                return;
            }
            _currentRoom = row.RoomId;
            row.UnreadCount = 0;
            HasPeer = true;
            PeerIsGroup = row.IsGroup;
            PeerNameLabel.Text = row.Title;
            ProfileName.Text  = row.Title;
            ProfileHandle.Text = row.IsGroup ? "групповой чат" : row.RoomId;
            PeerStatusLabel.Text = row.IsGroup ? "групповой чат · защищён E2EE" : "личный чат · защищён E2EE";
            PeerKeyLabel.Text = FormatKeyForRoom(row.RoomId);
            EmptyState.Visibility = Visibility.Collapsed;
            BtnRoomAction.Visibility = Visibility.Visible;
            BtnRoomAction.IsEnabled = true;
            BtnRoomAction.Content = row.IsGroup ? "Покинуть" : "Удалить";
            BtnInvite.Visibility = row.IsGroup ? Visibility.Visible : Visibility.Collapsed;
            TbInput.IsEnabled = true;
            BtnSend.IsEnabled = true;

            RequestRoomHistory(row.RoomId);
            RenderMessages();
        }

        private void ClearActiveChat()
        {
            _currentRoom = "";
            HasPeer = false;
            PeerIsGroup = false;
            PeerNameLabel.Text = "Выберите чат";
            ProfileName.Text = "";
            ProfileHandle.Text = "";
            PeerStatusLabel.Text = "";
            PeerKeyLabel.Text = "· · · ·   · · · ·   · · · ·   · · · ·";
            EmptyState.Visibility = Visibility.Visible;
            BtnRoomAction.Visibility = Visibility.Collapsed;
            BtnInvite.Visibility = Visibility.Collapsed;
            TbInput.IsEnabled = false;
            BtnSend.IsEnabled = false;
            Bubbles.Clear();
            SetReplyBubble(null);
        }

        // ============================================================
        //   Actions
        // ============================================================
        private void OnNewChat(object sender, RoutedEventArgs e)
        {
            var dlg = new NewChatDialog { Owner = this };
            if (dlg.ShowDialog() != true) return;

            if (dlg.Kind == NewChatKind.Direct)
            {
                string raw = dlg.DirectUserInput;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    new InfoDialog("Личный чат", "Введите username собеседника (@nick).") { Owner = this }.ShowDialog();
                    return;
                }
                string target = raw.Trim();
                if (target.StartsWith("@")) target = target.Substring(1).Trim();
                target = target.ToLowerInvariant();
                if (string.IsNullOrEmpty(target))
                {
                    new InfoDialog("Личный чат", "Укажите username после @.") { Owner = this }.ShowDialog();
                    return;
                }
                _client.Send(new ChatPacket { Type = "create_dm", Sender = _session.UserName, Target = target });
            }
            else
            {
                _client.Send(new ChatPacket { Type = "create_group", Sender = _session.UserName, Title = dlg.GroupTitle });
            }
        }

        private void OnInviteToGroup(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentRoom) || !_currentRoom.StartsWith("grp:")) return;
            var dlg = new InviteDialog { Owner = this };
            if (dlg.ShowDialog() != true) return;

            string inv = dlg.UserInput.Trim();
            if (string.IsNullOrWhiteSpace(inv))
            {
                new InfoDialog("Группа", "Введите @username.") { Owner = this }.ShowDialog();
                return;
            }
            if (inv.StartsWith("@")) inv = inv.Substring(1).Trim();
            inv = inv.ToLowerInvariant();
            if (string.IsNullOrEmpty(inv)) return;

            _client.Send(new ChatPacket
            {
                Type = "group_invite",
                Sender = _session.UserName,
                Room = _currentRoom,
                Target = inv,
            });
        }

        private void OnRoomAction(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentRoom)) return;
            bool grp = _currentRoom.StartsWith("grp:");
            string header = grp ? "Покинуть группу?" : "Удалить чат?";
            string body = grp
                ? "Вы покинете группу и сможете вернуться только по новому приглашению."
                : "Чат исчезнет у вас и у собеседника. Это действие нельзя отменить.";
            var dlg = new ConfirmDialog(header, body, grp ? "Покинуть" : "Удалить") { Owner = this };
            if (dlg.ShowDialog() != true) return;

            _client.Send(new ChatPacket
            {
                Type = grp ? "leave_room" : "delete_chat",
                Sender = _session.UserName,
                Room = _currentRoom,
            });
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                SendMessage();
            }
        }

        private void OnSendClick(object sender, RoutedEventArgs e) => SendMessage();

        private void SendMessage()
        {
            if (string.IsNullOrEmpty(_currentRoom)) return;
            string text = TbInput.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;
            TbInput.Clear();

            if (_replyBubble != null)
            {
                string quote = _replyBubble.Text.Length > 50 ? _replyBubble.Text[..50] + "…" : _replyBubble.Text;
                text = $"↩ {_replyBubble.SenderLabel}: {quote}\n{text}";
                SetReplyBubble(null);
            }

            string cipher = CryptoHelper.Encrypt(text);
            var pkt = new ChatPacket
            {
                Type = "msg",
                Sender = _session.UserName,
                Room = _currentRoom,
                Message = cipher,
            };

            if (_reconnecting)
                lock (_offlineQueue) _offlineQueue.Enqueue(pkt);
            else
                _client.Send(pkt);
        }

        // ============================================================
        //   Network event handlers (marshaled to UI thread)
        // ============================================================
        private void OnMessage(ChatPacket pkt)
        {
            if (!CheckAccess()) { Dispatcher.Invoke(() => OnMessage(pkt)); return; }
            if (string.IsNullOrEmpty(pkt.Room)) return;

            string plain = "[ошибка расшифровки]";
            try { plain = CryptoHelper.Decrypt(pkt.Message); } catch { }

            bool isSystem = pkt.Sender == "SYSTEM";
            long? ts = pkt.Ts != 0 ? pkt.Ts : null;
            AddHistory(pkt.Room, new MsgLine
            {
                Sender = pkt.Sender, Text = plain,
                Own = !isSystem && pkt.Sender == _session.UserName,
                System = isSystem, UtcTicks = ts,
            });

            var row = Rooms.FirstOrDefault(r => r.RoomId == pkt.Room);
            if (row != null)
            {
                if (!isSystem)
                    row.Preview = (pkt.Sender == _session.UserName ? "Вы: " : ResolveShort(pkt.Sender) + ": ") + plain;
                if (pkt.Room != _currentRoom && !isSystem && pkt.Sender != _session.UserName)
                    row.UnreadCount++;
            }

            if (pkt.Room == _currentRoom)
            {
                AppendBubble(new BubbleVM
                {
                    Sender = pkt.Sender,
                    SenderLabel = isSystem ? "" : ResolveShort(pkt.Sender),
                    Text = plain,
                    Own = !isSystem && pkt.Sender == _session.UserName,
                    IsSystem = isSystem,
                    Time = ts.HasValue ? new DateTime(ts.Value, DateTimeKind.Utc) : DateTime.UtcNow,
                    Latest = !isSystem && pkt.Sender == _session.UserName,
                    ShowSenderLabel = !isSystem && PeerIsGroup && pkt.Sender != _session.UserName,
                    ShowAvatar = !isSystem && pkt.Sender != _session.UserName,
                });
                MessageScroll.ScrollToEnd();
            }
        }

        private void OnUsersUpdate(string[] users, string? payloadJson)
        {
            if (!CheckAccess()) { Dispatcher.Invoke(() => OnUsersUpdate(users, payloadJson)); return; }
            _userLabels.Clear();
            var rich = OnlineUsersJson.Deserialize(payloadJson);
            if (rich != null)
            {
                foreach (var u in rich)
                {
                    if (!string.IsNullOrEmpty(u.Username))
                        _userLabels[u.Username] = string.IsNullOrEmpty(u.DisplayName) ? u.Username : u.DisplayName;
                }
            }
            foreach (var u in users)
                if (!_userLabels.ContainsKey(u)) _userLabels[u] = u;

            StatusFooter.Text = users.Length == 0
                ? "E2EE · никого онлайн"
                : $"E2EE · онлайн: {users.Length}";
        }

        private void OnRoomsSync(string json)
        {
            if (!CheckAccess()) { Dispatcher.Invoke(() => OnRoomsSync(json)); return; }
            var list = RoomJson.DeserializeRooms(json) ?? new List<RoomInfo>();
            var newIds = new HashSet<string>(list.Select(r => r.Room));

            foreach (var k in _history.Keys.ToList())
                if (!newIds.Contains(k)) _history.Remove(k);

            string keep = _currentRoom;

            // Preserve unread counts before clearing — they'd reset to 0 on rebuild otherwise
            var prevUnread = Rooms.ToDictionary(r => r.RoomId, r => r.UnreadCount);

            // Detach handler during rebuild so Rooms.Clear() doesn't cascade into ClearActiveChat
            ChatList.SelectionChanged -= OnChatSelected;
            Rooms.Clear();
            foreach (var r in list)
            {
                prevUnread.TryGetValue(r.Room, out int unread);
                var row = new ChatRowVM
                {
                    RoomId = r.Room,
                    Title = string.IsNullOrEmpty(r.Title) ? r.Room : r.Title,
                    Kind = r.Kind,
                    Preview = LastPreviewFor(r.Room),
                };
                // Don't restore unread for the currently open room
                if (r.Room != keep) row.UnreadCount = unread;
                Rooms.Add(row);
            }
            ChatList.SelectionChanged += OnChatSelected;

            EmptyChats.Visibility = Rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Pre-fetch history for rooms we haven't loaded yet so sidebar previews are populated
            foreach (var r in list)
                if (!_history.ContainsKey(r.Room))
                    _client.Send(new ChatPacket { Type = "fetch_history", Sender = _session.UserName, Room = r.Room });

            if (!string.IsNullOrEmpty(keep))
            {
                var idx = Rooms.FirstOrDefault(r => r.RoomId == keep);
                if (idx != null)
                {
                    // Restore visual selection without re-triggering the full select flow
                    ChatList.SelectionChanged -= OnChatSelected;
                    ChatList.SelectedItem = idx;
                    ChatList.SelectionChanged += OnChatSelected;
                    // Re-enable input controls that OnDisconnected may have disabled
                    TbInput.IsEnabled = true;
                    BtnSend.IsEnabled = true;
                    BtnRoomAction.IsEnabled = true;
                    BtnInvite.IsEnabled = true;
                    return;
                }
                // Room was removed (deleted / left)
                ClearActiveChat();
            }
        }

        private void OnRoomError(string text)
        {
            if (!CheckAccess()) { Dispatcher.Invoke(() => OnRoomError(text)); return; }
            new InfoDialog("Чат", text) { Owner = this }.ShowDialog();
        }

        private void OnRoomHistory(string room, string json)
        {
            if (!CheckAccess()) { Dispatcher.Invoke(() => OnRoomHistory(room, json)); return; }
            var dtos = ChatHistoryJson.DeserializeLines(json);
            var list = new List<MsgLine>(dtos.Count);
            foreach (var dto in dtos)
            {
                string plain = "[ошибка расшифровки]";
                try { plain = CryptoHelper.Decrypt(dto.Message); } catch { }
                bool isSystem = dto.Sender == "SYSTEM";
                list.Add(new MsgLine
                {
                    Sender = dto.Sender,
                    Text = plain,
                    Own = !isSystem && dto.Sender == _session.UserName,
                    System = isSystem,
                    UtcTicks = dto.Ts != 0 ? dto.Ts : null,
                });
            }
            _history[room] = list;

            // Update sidebar preview for this room even if it's not currently open
            if (list.Count > 0)
            {
                var row = Rooms.FirstOrDefault(r => r.RoomId == room);
                if (row != null) row.Preview = LastPreviewFor(room);
            }

            if (room == _currentRoom)
                RenderMessages();
        }

        private void OnDisconnected()
        {
            if (!CheckAccess()) { Dispatcher.Invoke(OnDisconnected); return; }
            if (_reconnecting) return; // reconnect loop already running

            _reconnecting = true;
            SetStatus("Соединение разорвано · переподключение…", (Brush)FindResource("Danger"));
            BtnNewChat.IsEnabled = false;
            BtnRoomAction.IsEnabled = false;
            BtnInvite.IsEnabled = false;
            BtnReconnectNow.Visibility = Visibility.Visible;

            new Thread(ReconnectLoop) { IsBackground = true }.Start();
        }

        // ============================================================
        //   Helpers
        // ============================================================
        private void RequestRoomHistory(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return;
            _history[roomId] = new List<MsgLine>();
            _client.Send(new ChatPacket { Type = "fetch_history", Sender = _session.UserName, Room = roomId });
        }

        private void RenderMessages()
        {
            Bubbles.Clear();
            if (string.IsNullOrEmpty(_currentRoom)) return;
            if (!_history.TryGetValue(_currentRoom, out var lines) || lines.Count == 0) return;

            int lastOwn = -1;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Own) lastOwn = i;

            for (int i = 0; i < lines.Count; i++)
            {
                var m = lines[i];
                bool nextSameSender = (i + 1 < lines.Count) && lines[i + 1].Sender == m.Sender && !m.Own && !m.System;
                Bubbles.Add(new BubbleVM
                {
                    Sender = m.Sender,
                    SenderLabel = m.System ? "" : ResolveShort(m.Sender),
                    Text = m.Text,
                    Own = m.Own,
                    IsSystem = m.System,
                    Time = m.UtcTicks.HasValue ? new DateTime(m.UtcTicks.Value, DateTimeKind.Utc) : (DateTime?)null,
                    Latest = i == lastOwn,
                    ShowSenderLabel = !m.System && PeerIsGroup && !m.Own &&
                                      (i == 0 || lines[i - 1].Sender != m.Sender),
                    ShowAvatar = !m.System && !m.Own && !nextSameSender,
                });
            }
            Dispatcher.BeginInvoke(new Action(() => MessageScroll.ScrollToEnd()),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void AppendBubble(BubbleVM b) => Bubbles.Add(b);

        // ============================================================
        //   Context menu handlers
        // ============================================================
        private BubbleVM? GetBubbleFromContextMenu(object sender)
        {
            if (sender is MenuItem mi && mi.Parent is ContextMenu cm)
                return (cm.PlacementTarget as FrameworkElement)?.Tag as BubbleVM;
            return null;
        }

        private void OnBubbleReply(object sender, RoutedEventArgs e)
        {
            var bubble = GetBubbleFromContextMenu(sender);
            if (bubble != null) SetReplyBubble(bubble);
        }

        private void OnBubbleForward(object sender, RoutedEventArgs e)
        {
            var bubble = GetBubbleFromContextMenu(sender);
            if (bubble == null) return;

            if (Rooms.Count == 0) { new InfoDialog("Переслать", "Нет чатов для пересылки.") { Owner = this }.ShowDialog(); return; }

            var dlg = new ForwardDialog(Rooms) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.SelectedRoom == null) return;

            string author = !string.IsNullOrEmpty(bubble.SenderLabel)
                ? bubble.SenderLabel
                : (!string.IsNullOrEmpty(bubble.Sender) ? "@" + bubble.Sender : "?");
            string forwardText = $"⤷ {author}: {bubble.Text}";
            _client.Send(new ChatPacket
            {
                Type = "msg",
                Sender = _session.UserName,
                Room = dlg.SelectedRoom.RoomId,
                Message = CryptoHelper.Encrypt(forwardText),
            });
        }

        private void OnBubbleDelete(object sender, RoutedEventArgs e)
        {
            var bubble = GetBubbleFromContextMenu(sender);
            if (bubble == null) return;

            Bubbles.Remove(bubble);
            if (_history.TryGetValue(_currentRoom, out var hist))
            {
                var line = hist.FirstOrDefault(l => l.Sender == bubble.Sender && l.Text == bubble.Text);
                if (line != null) hist.Remove(line);
            }
        }

        private void SetReplyBubble(BubbleVM? bubble)
        {
            _replyBubble = bubble;
            if (bubble == null)
            {
                ReplyBar.Visibility = Visibility.Collapsed;
            }
            else
            {
                ReplyPreviewText.Text = bubble.Text.Length > 60 ? bubble.Text[..60] + "…" : bubble.Text;
                ReplyBar.Visibility = Visibility.Visible;
                TbInput.Focus();
            }
        }

        private void OnCancelReply(object sender, RoutedEventArgs e) => SetReplyBubble(null);

        private void AddHistory(string room, MsgLine line)
        {
            if (!_history.TryGetValue(room, out var list)) { list = new(); _history[room] = list; }
            list.Add(line);
        }

        private string LastPreviewFor(string roomId)
        {
            if (!_history.TryGetValue(roomId, out var ls) || ls.Count == 0) return "";
            var last = ls.FindLast(l => !l.System);
            if (last == null) return "";
            return (last.Own ? "Вы: " : ResolveShort(last.Sender) + ": ") + last.Text;
        }

        private string ResolveShort(string username)
        {
            if (username == _session.UserName) return _session.DisplayName;
            if (_userLabels.TryGetValue(username, out var d) && !string.IsNullOrEmpty(d)) return d;
            return "@" + username;
        }

        private static string FormatKeyForRoom(string roomId)
        {
            unchecked
            {
                int h1 = 17, h2 = 31;
                foreach (var c in roomId) { h1 = h1 * 7919 + c; h2 = h2 * 5779 + c; }
                string g(int hh, int shift) => ((hh >> shift) & 0xFFFF).ToString("X4");
                return $"{g(h1,0)}  {g(h1,8)}  {g(h2,0)}  {g(h2,8)}\n{g(h1,4)}  {g(h2,4)}  {g(h1,12)}  {g(h2,12)}";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise([CallerMemberName] string n = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
