// ============================================================
//  Server/Program.cs — TCP-сервер: комнаты, ЛС, группы, активность
// ============================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using ChatApp.Shared;

namespace ChatApp.Server
{
    class Program
    {
        internal const int Port = 5000;

        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, StreamWriter>> _userSessions
            = new(StringComparer.Ordinal);

        private static readonly object _historyLock = new object();
        private static readonly Dictionary<string, List<StoredMsg>> _roomHistory = new(StringComparer.Ordinal);

        private sealed class StoredMsg
        {
            public string Sender { get; init; } = "";
            public string Cipher { get; init; } = "";
            public long UtcTicks { get; init; }
        }

        private const int MaxMessagesPerRoom = 2000;

        // Per-user send locks so a slow/dead connection can't block writes to other users.
        private static readonly ConcurrentDictionary<string, object> _userSendLocks =
            new(StringComparer.Ordinal);

        private static object GetUserLock(string username) =>
            _userSendLocks.GetOrAdd(username, _ => new object());

        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _roomMembers
            = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();

        private static readonly ConcurrentDictionary<string, string> _roomTitles
            = new ConcurrentDictionary<string, string>();

        /// <summary>username → отображаемое имя.</summary>
        private static readonly ConcurrentDictionary<string, string> _displayNames
            = new ConcurrentDictionary<string, string>();

        /// <summary>roomId → UtcTicks последней активности (сообщение / создание).</summary>
        private static readonly ConcurrentDictionary<string, long> _roomActivity
            = new ConcurrentDictionary<string, long>();

        private sealed class UserAccount
        {
            public string DisplayName { get; }
            public byte[] Salt { get; }
            public byte[] Hash { get; }

            public UserAccount(string displayName, byte[] salt, byte[] hash)
            {
                DisplayName = displayName;
                Salt        = salt;
                Hash        = hash;
            }
        }

        private static readonly ConcurrentDictionary<string, UserAccount> _accounts
            = new ConcurrentDictionary<string, UserAccount>();

        private const int PasswordMaxLen = 128;

        // --- Персистентность аккаунтов ---
        private const string AccountsFilePath = "users.json";
        private static readonly object _accountsFileLock = new object();

        private sealed class UserAccountDto
        {
            public string Username    { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string SaltBase64  { get; set; } = "";
            public string HashBase64  { get; set; } = "";
        }

        private static void LoadAccounts()
        {
            try
            {
                if (!File.Exists(AccountsFilePath)) return;
                string json = File.ReadAllText(AccountsFilePath, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<UserAccountDto>>(json);
                if (list == null) return;
                foreach (var dto in list)
                {
                    if (string.IsNullOrEmpty(dto.Username)) continue;
                    var salt = Convert.FromBase64String(dto.SaltBase64);
                    var hash = Convert.FromBase64String(dto.HashBase64);
                    _accounts.TryAdd(dto.Username, new UserAccount(dto.DisplayName, salt, hash));
                }
                Log($"Загружено {_accounts.Count} аккаунтов из {AccountsFilePath}.");
            }
            catch (Exception ex) { Log($"Не удалось загрузить аккаунты: {ex.Message}"); }
        }

        private static void SaveAccounts()
        {
            try
            {
                var list = _accounts.Select(kv => new UserAccountDto
                {
                    Username    = kv.Key,
                    DisplayName = kv.Value.DisplayName,
                    SaltBase64  = Convert.ToBase64String(kv.Value.Salt),
                    HashBase64  = Convert.ToBase64String(kv.Value.Hash),
                }).ToList();
                string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                lock (_accountsFileLock)
                    File.WriteAllText(AccountsFilePath, json, Encoding.UTF8);
            }
            catch (Exception ex) { Log($"Не удалось сохранить аккаунты: {ex.Message}"); }
        }

        // --- Персистентность чатов и истории ---
        private const string RoomsFilePath   = "rooms.json";
        private const string HistoryFilePath = "history.json";
        private static readonly object _roomsFileLock   = new object();
        private static readonly object _historyFileLock = new object();

        private sealed class RoomDto
        {
            public string       RoomId        { get; set; } = "";
            public string       Title         { get; set; } = "";
            public List<string> Members       { get; set; } = new();
            public long         ActivityTicks { get; set; }
        }

        private sealed class StoredMsgDto
        {
            public string Sender   { get; set; } = "";
            public string Cipher   { get; set; } = "";
            public long   UtcTicks { get; set; }
        }

        private static void LoadRooms()
        {
            try
            {
                if (!File.Exists(RoomsFilePath)) return;
                string json = File.ReadAllText(RoomsFilePath, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<RoomDto>>(json);
                if (list == null) return;
                foreach (var dto in list)
                {
                    if (string.IsNullOrEmpty(dto.RoomId)) continue;
                    var members = new ConcurrentDictionary<string, byte>();
                    foreach (var m in dto.Members ?? new List<string>())
                        if (!string.IsNullOrEmpty(m)) members[m] = 1;
                    if (members.IsEmpty) continue;
                    _roomMembers[dto.RoomId] = members;
                    if (!string.IsNullOrEmpty(dto.Title))
                        _roomTitles[dto.RoomId] = dto.Title;
                    if (dto.ActivityTicks > 0)
                        _roomActivity[dto.RoomId] = dto.ActivityTicks;
                }
                Log($"Загружено {_roomMembers.Count} чатов из {RoomsFilePath}.");
            }
            catch (Exception ex) { Log($"Не удалось загрузить чаты: {ex.Message}"); }
        }

        private static void SaveRooms()
        {
            try
            {
                var list = _roomMembers.Select(kv =>
                {
                    _roomTitles.TryGetValue(kv.Key, out var title);
                    return new RoomDto
                    {
                        RoomId        = kv.Key,
                        Title         = title ?? "",
                        Members       = kv.Value.Keys.ToList(),
                        ActivityTicks = GetActivityTicks(kv.Key),
                    };
                }).ToList();
                string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                lock (_roomsFileLock)
                    File.WriteAllText(RoomsFilePath, json, Encoding.UTF8);
            }
            catch (Exception ex) { Log($"Не удалось сохранить чаты: {ex.Message}"); }
        }

        private static void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryFilePath)) return;
                string json = File.ReadAllText(HistoryFilePath, Encoding.UTF8);
                var dict = JsonSerializer.Deserialize<Dictionary<string, List<StoredMsgDto>>>(json);
                if (dict == null) return;
                lock (_historyLock)
                {
                    foreach (var kv in dict)
                    {
                        if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                        _roomHistory[kv.Key] = kv.Value
                            .Select(m => new StoredMsg { Sender = m.Sender, Cipher = m.Cipher, UtcTicks = m.UtcTicks })
                            .ToList();
                    }
                }
                int total = _roomHistory.Values.Sum(l => l.Count);
                Log($"Загружено {total} сообщений из {HistoryFilePath}.");
            }
            catch (Exception ex) { Log($"Не удалось загрузить историю: {ex.Message}"); }
        }

        private static void SaveHistory()
        {
            try
            {
                Dictionary<string, List<StoredMsgDto>> snapshot;
                lock (_historyLock)
                {
                    snapshot = _roomHistory.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.Select(m => new StoredMsgDto
                        {
                            Sender   = m.Sender,
                            Cipher   = m.Cipher,
                            UtcTicks = m.UtcTicks
                        }).ToList()
                    );
                }
                string json = JsonSerializer.Serialize(snapshot);
                lock (_historyFileLock)
                    File.WriteAllText(HistoryFilePath, json, Encoding.UTF8);
            }
            catch (Exception ex) { Log($"Не удалось сохранить историю: {ex.Message}"); }
        }

        private static void AutosaveLoop()
        {
            while (true)
            {
                Thread.Sleep(10_000);
                if (!_serverRunning) continue;
                SaveRooms();
                SaveHistory();
            }
        }

        internal static Action<string>? UILogger;
        private static TcpListener?   _listener;
        private static volatile bool  _serverRunning;

        private static readonly ConcurrentDictionary<string, TcpClient> _activeTcpClients
            = new(StringComparer.Ordinal);

        internal static void Stop()
        {
            _serverRunning = false;
            SaveRooms();
            SaveHistory();
            try { _listener?.Stop(); } catch { }
            foreach (var kv in _activeTcpClients.ToArray())
                try { kv.Value.Close(); } catch { }
            _activeTcpClients.Clear();
        }

        internal static void Run()
        {
            _serverRunning = true;

            LoadAccounts();
            LoadRooms();
            LoadHistory();

            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
            Log($"Сервер запущен на порту {Port}.");
            LogRemoteAccessHints(Port);

            if (!_loopsStarted)
            {
                _loopsStarted = true;
                new Thread(PingLoop)     { IsBackground = true, Name = "PingLoop"     }.Start();
                new Thread(AutosaveLoop) { IsBackground = true, Name = "AutosaveLoop" }.Start();
            }

            try
            {
                while (_serverRunning)
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    if (!_serverRunning) { try { client.Close(); } catch { } break; }
                    try
                    {
                        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
                        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                    } catch { }
                    client.SendTimeout = 8_000;
                    new Thread(() => HandleClient(client)) { IsBackground = true }.Start();
                }
            }
            catch (Exception) { /* listener stopped or disposed */ }
            finally { _serverRunning = false; }
        }

        private static bool _loopsStarted;

        private static void HandleClient(TcpClient tcp)
        {
            string tcpId = Guid.NewGuid().ToString("N");
            _activeTcpClients[tcpId] = tcp;

            string? joinedUsername  = null;
            string? joinedSessionId = null;
            StreamReader? reader = null;
            StreamWriter? writer = null;

            try
            {
                var stream = tcp.GetStream();
                reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
                writer = new StreamWriter(stream, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };

                string? line = reader.ReadLine();
                if (line == null) return;

                var pkt = ChatPacket.FromJson(line);
                if (pkt == null || pkt.Type != "join") return;

                string candidate = UsernameRules.Normalize(pkt.Sender);
                string? userErr = UsernameRules.GetValidationError(candidate);
                if (userErr != null)
                {
                    WriteJoinError(writer, userErr);
                    return;
                }

                string mode = (pkt.AuthMode ?? "").Trim().ToLowerInvariant();
                if (mode != "register" && mode != "login")
                {
                    WriteJoinError(writer, "Выберите режим: регистрация нового профиля или вход в существующий.");
                    return;
                }

                string password = pkt.Password ?? "";

                if (mode == "register")
                {
                    string? pwErr = PasswordRules.GetRegistrationError(password);
                    if (pwErr != null)
                    {
                        WriteJoinError(writer, pwErr);
                        return;
                    }

                    string display = string.IsNullOrWhiteSpace(pkt.DisplayName) ? "" : pkt.DisplayName.Trim();
                    if (string.IsNullOrEmpty(display))
                    {
                        WriteJoinError(writer, "При регистрации введите имя (как вас будут видеть другие).");
                        return;
                    }

                    if (display.Length > 60) display = display[..60];

                    if (_accounts.ContainsKey(candidate))
                    {
                        WriteJoinError(writer, "Этот username уже занят. Попробуйте другой.");
                        return;
                    }

                    var (hash, salt) = CreatePasswordHash(password);
                    if (!_accounts.TryAdd(candidate, new UserAccount(display, salt, hash)))
                    {
                        WriteJoinError(writer, "Этот username уже занят. Попробуйте другой.");
                        return;
                    }

                    SaveAccounts();

                    var sessions = _userSessions.GetOrAdd(candidate, _ => new ConcurrentDictionary<string, StreamWriter>());
                    joinedSessionId = Guid.NewGuid().ToString("N");
                    if (!sessions.TryAdd(joinedSessionId, writer))
                    {
                        _accounts.TryRemove(candidate, out _);
                        WriteJoinError(writer, "Не удалось создать сессию. Попробуйте снова.");
                        return;
                    }

                    joinedUsername = candidate;
                    _displayNames[joinedUsername] = display;
                }
                else // login
                {
                    if (string.IsNullOrEmpty(password))
                    {
                        WriteJoinError(writer, "Введите пароль.");
                        return;
                    }

                    if (password.Length > PasswordMaxLen)
                    {
                        WriteJoinError(writer, "Неверный username или пароль.");
                        return;
                    }

                    if (!_accounts.TryGetValue(candidate, out var acc))
                    {
                        WriteJoinError(writer, "Неверный username или пароль.");
                        return;
                    }

                    if (!VerifyPassword(password, acc))
                    {
                        WriteJoinError(writer, "Неверный username или пароль.");
                        return;
                    }

                    var sessions = _userSessions.GetOrAdd(candidate, _ => new ConcurrentDictionary<string, StreamWriter>());
                    joinedSessionId = Guid.NewGuid().ToString("N");
                    if (!sessions.TryAdd(joinedSessionId, writer))
                    {
                        WriteJoinError(writer, "Не удалось создать сессию. Попробуйте снова.");
                        return;
                    }

                    joinedUsername = candidate;
                    string display = string.IsNullOrWhiteSpace(acc.DisplayName) ? joinedUsername : acc.DisplayName.Trim();
                    if (display.Length > 60) display = display[..60];
                    _displayNames[joinedUsername] = display;
                }

                int sessionsTotal = _userSessions.Values.Sum(s => s.Count);
                Log($"[+] Пользователь подключился. Онлайн: {_userSessions.Count}, сессий: {sessionsTotal}");
                BroadcastUserList();
                NotifyRoomsSync(joinedUsername);

                while ((line = reader.ReadLine()) != null)
                {
                    var msg = ChatPacket.FromJson(line);
                    if (msg == null)
                    {
                        Log("Пакет JSON не разобран, строка пропущена.");
                        continue;
                    }

                    switch (msg.Type)
                    {
                        case "msg":
                            HandleChatMessage(joinedUsername, msg);
                            break;
                        case "create_dm":
                            HandleCreateDm(joinedUsername, msg);
                            break;
                        case "create_group":
                            HandleCreateGroup(joinedUsername, msg);
                            break;
                        case "group_invite":
                            HandleGroupInvite(joinedUsername, msg);
                            break;
                        case "delete_chat":
                            HandleDeleteChat(joinedUsername, msg);
                            break;
                        case "leave_room":
                            HandleLeaveGroup(joinedUsername, msg);
                            break;
                        case "fetch_history":
                            HandleFetchHistory(joinedUsername, msg);
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (IOException) { /* отключение */ }
            catch (Exception ex) { Log($"Ошибка: {ex.Message}"); }
            finally
            {
                if (joinedUsername != null && joinedSessionId != null)
                {
                    lock (GetUserLock(joinedUsername))
                    {
                        if (_userSessions.TryGetValue(joinedUsername, out var inner))
                        {
                            inner.TryRemove(joinedSessionId, out _);
                            if (inner.IsEmpty)
                            {
                                _userSessions.TryRemove(joinedUsername, out _);
                                _displayNames.TryRemove(joinedUsername, out _);
                                _userSendLocks.TryRemove(joinedUsername, out _);
                            }
                        }
                    }

                    Log($"[-] Пользователь отключился. Онлайн: {_userSessions.Count}");
                    BroadcastUserList();
                }

                try { reader?.Dispose(); } catch { }
                try { writer?.Dispose(); } catch { }
                try { tcp.Close(); } catch { }
                _activeTcpClients.TryRemove(tcpId, out _);
            }
        }

        private static void WriteJoinError(StreamWriter writer, string plain)
        {
            try
            {
                writer.WriteLine(new ChatPacket
                {
                    Type    = "error",
                    Sender  = "SERVER",
                    Message = CryptoHelper.Encrypt(plain)
                }.ToJson());
            }
            catch { /* сокет */ }
        }

        private static byte[] HashPassword(string password, byte[] salt)
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 120_000, HashAlgorithmName.SHA256);
            return pbkdf2.GetBytes(32);
        }

        private static (byte[] hash, byte[] salt) CreatePasswordHash(string password)
        {
            var salt = new byte[16];
            RandomNumberGenerator.Fill(salt);
            return (HashPassword(password, salt), salt);
        }

        private static bool VerifyPassword(string password, UserAccount acc)
        {
            byte[] test = HashPassword(password, acc.Salt);
            return test.Length == acc.Hash.Length && CryptographicOperations.FixedTimeEquals(test, acc.Hash);
        }

        private static bool IsUserOnline(string username) =>
            _userSessions.TryGetValue(username, out var s) && !s.IsEmpty;

        private static void AppendRoomHistory(string roomId, string sender, string cipher, long utcTicks)
        {
            lock (_historyLock)
            {
                if (!_roomHistory.TryGetValue(roomId, out var list))
                {
                    list = new List<StoredMsg>();
                    _roomHistory[roomId] = list;
                }

                list.Add(new StoredMsg { Sender = sender, Cipher = cipher, UtcTicks = utcTicks });
                if (list.Count > MaxMessagesPerRoom)
                    list.RemoveRange(0, list.Count - MaxMessagesPerRoom);
            }
        }

        private static List<HistoryLineDto> CloneHistoryForClient(string roomId)
        {
            lock (_historyLock)
            {
                if (!_roomHistory.TryGetValue(roomId, out var list) || list.Count == 0)
                    return new List<HistoryLineDto>();

                return list.Select(m => new HistoryLineDto
                {
                    Sender  = m.Sender,
                    Message = m.Cipher,
                    Ts      = m.UtcTicks
                }).ToList();
            }
        }

        private static void ClearRoomHistory(string roomId)
        {
            lock (_historyLock)
            {
                _roomHistory.Remove(roomId);
            }
        }

        private static void HandleFetchHistory(string user, ChatPacket msg)
        {
            string room = (msg.Room ?? "").Trim();
            if (string.IsNullOrEmpty(room))
            {
                SendRoomError(user, "Не указан чат (Room).");
                return;
            }

            if (!_roomMembers.TryGetValue(room, out var members) || !members.ContainsKey(user))
            {
                SendRoomError(user, "Нет доступа к этому чату.");
                return;
            }

            var dtos = CloneHistoryForClient(room);
            string json = ChatHistoryJson.SerializeLines(dtos);
            SendPacket(user, new ChatPacket
            {
                Type    = "room_history",
                Room    = room,
                Payload = json
            });
        }

        private static void BumpActivity(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return;
            _roomActivity[roomId] = DateTime.UtcNow.Ticks;
        }

        private static long GetActivityTicks(string roomId) =>
            _roomActivity.TryGetValue(roomId, out var t) ? t : 0L;

        private static string GetDisplay(string username)
        {
            if (_displayNames.TryGetValue(username, out var d) && !string.IsNullOrEmpty(d))
                return d;
            if (_accounts.TryGetValue(username, out var acc) && !string.IsNullOrEmpty(acc.DisplayName))
                return acc.DisplayName.Trim();
            return username;
        }

        // --------------------------------------------------------
        private static void SendSystemMessage(string roomId, string text)
        {
            long ts = DateTime.UtcNow.Ticks;
            string cipher = CryptoHelper.Encrypt(text);
            AppendRoomHistory(roomId, "SYSTEM", cipher, ts);
            SendToRoom(roomId, new ChatPacket
            {
                Type    = "msg",
                Sender  = "SYSTEM",
                Room    = roomId,
                Message = cipher,
                Ts      = ts
            });
        }

        private static void HandleChatMessage(string user, ChatPacket msg)
        {
            if (string.IsNullOrWhiteSpace(msg.Room))
            {
                SendRoomError(user, "Не указан чат (Room).");
                return;
            }

            if (!_roomMembers.TryGetValue(msg.Room, out var members) || !members.ContainsKey(user))
            {
                SendRoomError(user, "Нет доступа к этому чату.");
                return;
            }


            BumpActivity(msg.Room);

            long ts = DateTime.UtcNow.Ticks;
            AppendRoomHistory(msg.Room, user, msg.Message, ts);

            var forward = new ChatPacket
            {
                Type    = "msg",
                Sender  = user,
                Room    = msg.Room,
                Message = msg.Message,
                Ts      = ts
            };

            SendToRoom(msg.Room, forward);
            NotifyRoomsOrderForRoom(msg.Room);
        }

        /// <summary>После сообщения поднимаем чат в списке у всех участников.</summary>
        private static void NotifyRoomsOrderForRoom(string roomId)
        {
            if (!_roomMembers.TryGetValue(roomId, out var mem)) return;
            foreach (var name in mem.Keys.ToArray())
                NotifyRoomsSync(name);
        }

        private static void HandleCreateDm(string user, ChatPacket msg)
        {
            string target = NormalizeName(msg.Target);
            if (string.IsNullOrEmpty(target))
            {
                SendRoomError(user, "Укажите username (например @nick).");
                return;
            }

            if (target == user)
            {
                SendRoomError(user, "Нельзя открыть личный чат с самим собой.");
                return;
            }

            if (!_accounts.ContainsKey(target))
            {
                SendRoomError(user, $"Пользователь «@{target}» не найден.");
                return;
            }

            string roomId = MakeDmRoomId(user, target);
            var members = _roomMembers.GetOrAdd(roomId, _ => new ConcurrentDictionary<string, byte>());
            members[user]    = 1;
            members[target]  = 1;

            BumpActivity(roomId);
            SaveRooms();
            NotifyRoomsSync(user);
            NotifyRoomsSync(target);
        }

        private static void HandleCreateGroup(string user, ChatPacket msg)
        {
            string roomId = "grp:" + Guid.NewGuid().ToString("N");
            var members = new ConcurrentDictionary<string, byte>();
            members[user] = 1;
            _roomMembers[roomId] = members;

            string title = string.IsNullOrWhiteSpace(msg.Title) ? "Группа" : msg.Title.Trim();
            if (title.Length > 40) title = title[..40];
            _roomTitles[roomId] = title;

            BumpActivity(roomId);
            SaveRooms();
            NotifyRoomsSync(user);
        }

        private static void HandleGroupInvite(string user, ChatPacket msg)
        {
            if (string.IsNullOrWhiteSpace(msg.Room) || !msg.Room.StartsWith("grp:", StringComparison.Ordinal))
            {
                SendRoomError(user, "Некорректная группа.");
                return;
            }

            if (!_roomMembers.TryGetValue(msg.Room, out var members) || !members.ContainsKey(user))
            {
                SendRoomError(user, "Вы не участник этой группы.");
                return;
            }

            string target = NormalizeName(msg.Target);
            if (string.IsNullOrEmpty(target))
            {
                SendRoomError(user, "Укажите @username для приглашения.");
                return;
            }

            if (target == user)
            {
                SendRoomError(user, "Нельзя пригласить самого себя.");
                return;
            }

            if (!_accounts.ContainsKey(target))
            {
                SendRoomError(user, $"Пользователь «@{target}» не существует.");
                return;
            }

            if (!IsUserOnline(target))
            {
                SendRoomError(user, $"Пользователь «@{target}» сейчас не в сети. Пригласить можно только онлайн-пользователей.");
                return;
            }

            members[target] = 1;
            BumpActivity(msg.Room);
            SaveRooms();

            SendSystemMessage(msg.Room, $"👤 {GetDisplay(user)} добавил(а) {GetDisplay(target)} в группу");

            foreach (var name in members.Keys.ToArray())
                NotifyRoomsSync(name);
        }

        private static void HandleDeleteChat(string user, ChatPacket msg)
        {
            string roomId = (msg.Room ?? "").Trim();
            if (string.IsNullOrEmpty(roomId) || !roomId.StartsWith("dm:", StringComparison.Ordinal))
            {
                SendRoomError(user, "Удаление доступно только для личных чатов.");
                return;
            }

            if (!_roomMembers.TryGetValue(roomId, out var members) || !members.ContainsKey(user))
            {
                SendRoomError(user, "Этого чата нет в списке.");
                return;
            }

            var snapshot = members.Keys.ToArray();
            _roomMembers.TryRemove(roomId, out _);
            _roomActivity.TryRemove(roomId, out _);
            ClearRoomHistory(roomId);
            SaveRooms();
            SaveHistory();

            foreach (var n in snapshot)
                NotifyRoomsSync(n);
        }

        private static void HandleLeaveGroup(string user, ChatPacket msg)
        {
            string roomId = (msg.Room ?? "").Trim();
            if (string.IsNullOrEmpty(roomId) || !roomId.StartsWith("grp:", StringComparison.Ordinal))
            {
                SendRoomError(user, "Покинуть можно только групповой чат.");
                return;
            }

            if (!_roomMembers.TryGetValue(roomId, out var members) || !members.ContainsKey(user))
            {
                SendRoomError(user, "Вы не в этой группе.");
                return;
            }

            var snapshot = members.Keys.ToArray();

            SendSystemMessage(roomId, $"👤 {GetDisplay(user)} покинул(а) группу");

            members.TryRemove(user, out _);

            if (members.IsEmpty)
            {
                _roomMembers.TryRemove(roomId, out _);
                _roomTitles.TryRemove(roomId, out _);
                _roomActivity.TryRemove(roomId, out _);
                ClearRoomHistory(roomId);
                SaveHistory();
            }
            else
                BumpActivity(roomId);

            SaveRooms();

            foreach (var n in snapshot)
                NotifyRoomsSync(n);
        }

        // --------------------------------------------------------
        private static List<RoomInfo> BuildRoomsList(string viewer)
        {
            var list = new List<RoomInfo>();
            foreach (var kv in _roomMembers)
            {
                if (!kv.Value.ContainsKey(viewer)) continue;

                string roomId = kv.Key;
                string kind;
                string title;

                if (roomId.StartsWith("dm:", StringComparison.Ordinal))
                {
                    kind  = "dm";
                    title = DmTitleForViewer(roomId, viewer);
                }
                else
                {
                    kind  = "grp";
                    title = _roomTitles.TryGetValue(roomId, out var t) ? t : "Группа";
                }

                list.Add(new RoomInfo { Room = roomId, Title = title, Kind = kind });
            }

            list.Sort((a, b) =>
            {
                long ta = GetActivityTicks(a.Room);
                long tb = GetActivityTicks(b.Room);
                int c = tb.CompareTo(ta);
                return c != 0 ? c : string.CompareOrdinal(a.Room, b.Room);
            });

            return list;
        }

        private static void NotifyRoomsSync(string username)
        {
            if (string.IsNullOrEmpty(username)) return;

            var rooms = BuildRoomsList(username);
            var pkt = new ChatPacket
            {
                Type    = "rooms_sync",
                Payload = RoomJson.SerializeRooms(rooms)
            };

            SendPacket(username, pkt);
        }

        private static void SendRoomError(string username, string text)
        {
            SendPacket(username, new ChatPacket
            {
                Type    = "room_error",
                Sender  = "SERVER",
                Payload = text
            });
        }

        private static void SendPacket(string username, ChatPacket pkt)
        {
            string json = pkt.ToJson();
            lock (GetUserLock(username))
            {
                WriteJsonToAllSessions(username, json);
            }
        }

        private static void WriteJsonToAllSessions(string username, string json)
        {
            if (!_userSessions.TryGetValue(username, out var sessions)) return;
            foreach (var kvp in sessions.ToArray())
            {
                try { kvp.Value.WriteLine(json); }
                catch
                {
                    // Write failed (dead socket or send timeout): remove session and close
                    // the underlying stream so the HandleClient reader throws immediately.
                    sessions.TryRemove(kvp.Key, out _);
                    try { kvp.Value.BaseStream?.Close(); } catch { }
                }
            }
            if (sessions.IsEmpty)
                _userSessions.TryRemove(username, out _);
        }

        private static void SendToRoom(string roomId, ChatPacket pkt)
        {
            if (!_roomMembers.TryGetValue(roomId, out var members)) return;

            string json = pkt.ToJson();
            // Acquire per-user locks one at a time — never hold two simultaneously,
            // so there is no lock-ordering deadlock risk.
            foreach (var name in members.Keys.ToArray())
            {
                lock (GetUserLock(name))
                {
                    WriteJsonToAllSessions(name, json);
                }
            }
        }

        private static void BroadcastUserList()
        {
            var infos = _userSessions.Keys
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(u => new OnlineUserInfo
                {
                    Username    = u,
                    DisplayName = GetDisplay(u)
                })
                .ToList();

            string users = string.Join(",", infos.Select(i => i.Username));
            var pkt = new ChatPacket
            {
                Type    = "users",
                Users   = users,
                Payload = OnlineUsersJson.Serialize(infos)
            };
            string json = pkt.ToJson();

            // Per-user lock: a slow/dead user can't block the broadcast to others.
            foreach (var username in _userSessions.Keys.ToArray())
            {
                lock (GetUserLock(username))
                {
                    WriteJsonToAllSessions(username, json);
                }
            }
        }

        private static string MakeDmRoomId(string a, string b)
        {
            if (string.CompareOrdinal(a, b) <= 0)
                return "dm:" + a + "|" + b;
            return "dm:" + b + "|" + a;
        }

        private static string DmTitleForViewer(string roomId, string viewer)
        {
            if (roomId.Length < 4 || !roomId.StartsWith("dm:", StringComparison.Ordinal))
                return roomId;

            string pair = roomId[3..];
            int sep = pair.IndexOf('|');
            if (sep <= 0 || sep >= pair.Length - 1) return roomId;

            string u1 = pair[..sep];
            string u2 = pair[(sep + 1)..];
            string other = u1 == viewer ? u2 : u2 == viewer ? u1 : pair;
            string label = GetDisplay(other);
            return label + " (@" + other + ")";
        }

        private static string NormalizeName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim();
            if (s.StartsWith("@", StringComparison.Ordinal)) s = s[1..].Trim();
            return s.ToLowerInvariant();
        }

        /// <summary>Подсказка: с какого IP подключаться с других устройств в LAN / что нужно для интернета.</summary>
        private static void LogRemoteAccessHints(int port)
        {
            try
            {
                var v4 = new List<string>();
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;
                    foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        if (IPAddress.IsLoopback(u.Address))
                            continue;
                        string s = u.Address.ToString();
                        if (!v4.Contains(s, StringComparer.Ordinal))
                            v4.Add(s);
                    }
                }

                if (v4.Count > 0)
                {
                    Log("С других устройств в одной сети (Wi‑Fi/LAN): в клиенте укажите IP этого компьютера — "
                        + string.Join("  или  ", v4) + "  и порт " + port + ". Разрешите входящий TCP " + port + " в брандмауэре.");
                }
                else
                    Log("С других ПК в сети: в клиенте укажите IPv4 этого компьютера (команда ipconfig) и порт " + port + ".");

                Log("Через интернет «на расстояние»: поднимите сервер на VPS/облаке или пробросьте порт " + port
                    + " на роутере; в клиенте введите белый IP или домен (с чужого компьютера не используйте 127.0.0.1).");
            }
            catch
            {
                Log("Для чата между разными устройствами все клиенты должны подключаться к одному доступному IP/домену сервера.");
            }
        }

        private static void PingLoop()
        {
            var pkt = new ChatPacket { Type = "ping", Sender = "SERVER" };
            string json = pkt.ToJson();
            while (true)
            {
                Thread.Sleep(25_000);
                if (!_serverRunning) continue;
                foreach (var username in _userSessions.Keys.ToArray())
                {
                    lock (GetUserLock(username))
                    {
                        WriteJsonToAllSessions(username, json);
                    }
                }
            }
        }

        private static void Log(string text)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}]  {text}";
            UILogger?.Invoke(line);
        }
    }
}
