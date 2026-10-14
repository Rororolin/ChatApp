// ============================================================
//  ChatClient.cs — TCP network layer
// ============================================================
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ChatApp.Shared;

namespace ChatApp.Client
{
    public class ChatClient
    {
        private readonly string _ip;
        private readonly int    _port;
        private readonly string _userName;
        private readonly string _displayName;
        private readonly string _authMode;
        private readonly string _password;

        private TcpClient?    _tcp;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private bool          _running;
        private bool          _sessionEstablished;
        private readonly ManualResetEventSlim _receiveResume = new(true);

        public event Action<ChatPacket>? OnMessage;
        public event Action<ChatPacket>? OnUserJoin;
        public event Action<ChatPacket>? OnUserLeave;
        public event Action<string[], string?>? OnUsersUpdate;
        public event Action<string>?     OnRoomsSync;
        public event Action<string>?     OnRoomError;
        public event Action<string, string>? OnRoomHistory;
        public event Action?             OnDisconnected;
        public event Action?             OnSessionReady;
        public event Action<string>?     OnJoinFailed;

        public ChatClient(string ip, int port, string userName, string displayName, string authMode, string password)
        {
            _ip          = ip;
            _port        = port;
            _userName    = userName;
            _displayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName.Trim();
            _authMode    = authMode.Trim().ToLowerInvariant();
            _password    = password ?? "";
        }

        public void Connect()
        {
            _tcp    = new TcpClient();
            _tcp.Connect(_ip, _port);

            var stream = _tcp.GetStream();
            _reader = new StreamReader(stream, Encoding.UTF8);
            _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            _writer.WriteLine(new ChatPacket
            {
                Type        = "join",
                Sender      = _userName,
                DisplayName = _displayName,
                AuthMode    = _authMode,
                Password    = _password
            }.ToJson());

            _running = true;
        }

        public void StartReceiving()
        {
            var t = new Thread(ReceiveLoop) { IsBackground = true };
            t.Start();
        }

        private void ReceiveLoop()
        {
            try
            {
                string? line = _reader!.ReadLine();
                if (line == null)
                {
                    OnJoinFailed?.Invoke("Сервер закрыл соединение до ответа.");
                    return;
                }

                var first = ChatPacket.FromJson(line);
                if (first == null)
                {
                    OnJoinFailed?.Invoke("Некорректный ответ сервера.");
                    return;
                }

                if (first.Type == "error")
                {
                    string reason = "Сервер отклонил подключение.";
                    try
                    {
                        if (!string.IsNullOrEmpty(first.Message))
                            reason = CryptoHelper.Decrypt(first.Message);
                    }
                    catch { }

                    OnJoinFailed?.Invoke(reason);
                    return;
                }

                ProcessPacket(first);
                _sessionEstablished = true;
                OnSessionReady?.Invoke();
                _receiveResume.Wait();

                while (_running)
                {
                    line = _reader.ReadLine();
                    if (line == null) break;

                    var pkt = ChatPacket.FromJson(line);
                    if (pkt == null) continue;

                    if (pkt.Type == "error")
                    {
                        string reason = "Ошибка сервера.";
                        try
                        {
                            if (!string.IsNullOrEmpty(pkt.Message))
                                reason = CryptoHelper.Decrypt(pkt.Message);
                        }
                        catch { }
                        OnRoomError?.Invoke(reason);
                        continue;
                    }

                    ProcessPacket(pkt);
                }
            }
            catch (Exception) { }
            finally
            {
                _running = false;
                try { _tcp?.Close(); } catch { }
                if (_sessionEstablished)
                    OnDisconnected?.Invoke();
            }
        }

        private void ProcessPacket(ChatPacket pkt)
        {
            switch (pkt.Type)
            {
                case "msg":
                    OnMessage?.Invoke(pkt);
                    break;
                case "join":
                    OnUserJoin?.Invoke(pkt);
                    break;
                case "leave":
                    OnUserLeave?.Invoke(pkt);
                    break;
                case "users":
                {
                    var users = string.IsNullOrEmpty(pkt.Users)
                        ? Array.Empty<string>()
                        : pkt.Users.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    OnUsersUpdate?.Invoke(users, string.IsNullOrWhiteSpace(pkt.Payload) ? null : pkt.Payload);
                    break;
                }
                case "rooms_sync":
                    OnRoomsSync?.Invoke(pkt.Payload ?? "[]");
                    break;
                case "room_history":
                    OnRoomHistory?.Invoke(pkt.Room ?? "", pkt.Payload ?? "[]");
                    break;
                case "room_error":
                    OnRoomError?.Invoke(string.IsNullOrEmpty(pkt.Payload) ? pkt.Message : pkt.Payload);
                    break;
            }
        }

        public void HoldReceiving()    => _receiveResume.Reset();
        public void ResumeReceiving()  => _receiveResume.Set();

        public void Send(ChatPacket pkt)
        {
            try { _writer?.WriteLine(pkt.ToJson()); }
            catch { }
        }

        public void Disconnect()
        {
            _running = false;
            try { _tcp?.Close(); } catch { }
        }
    }
}
