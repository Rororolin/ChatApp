// ============================================================
//  Shared/ChatPacket.cs
//  Студент 3 — Формат JSON-пакета
// ============================================================
using System;
using System.Text.Json;

namespace ChatApp.Shared
{
    /// <summary>
    /// JSON-пакет, передаваемый между клиентом и сервером.
    /// Поле Message хранит ЗАШИФРОВАННЫЙ Base64-текст.
    /// </summary>
    public class ChatPacket
    {
        public string Sender  { get; set; } = "";
        public string Message { get; set; } = "";   // зашифровано AES-128 (для msg)
        public string Type    { get; set; } = "msg"; // msg | join | leave | users | create_dm | create_group | group_invite | rooms_sync | room_error | delete_chat | leave_room | fetch_history | room_history

        /// <summary>Время события (UtcTicks), для msg и записей истории.</summary>
        public long Ts { get; set; }
        public string Users   { get; set; } = "";   // список пользователей (тип "users")

        /// <summary>Идентификатор чата: dm:userA|userB (сорт.) или grp:guid.</summary>
        public string Room { get; set; } = "";

        /// <summary>Имя пользователя для create_dm / group_invite (можно с @).</summary>
        public string Target { get; set; } = "";

        /// <summary>Название группы (create_group).</summary>
        public string Title { get; set; } = "";

        /// <summary>JSON или текст системных пакетов (rooms_sync, room_error, users).</summary>
        public string Payload { get; set; } = "";

        /// <summary>Отображаемое имя при join; может совпадать у разных пользователей.</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>При первом пакете join: register | login.</summary>
        public string AuthMode { get; set; } = "";

        /// <summary>Пароль при join (регистрация или вход).</summary>
        public string Password { get; set; } = "";

        // --- Сериализация (camelCase, чтобы клиент и сервер совпадали) ---
        public static readonly JsonSerializerOptions PacketJson = new()
        {
            PropertyNamingPolicy            = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive     = true,
            WriteIndented                   = false
        };

        public string ToJson() => JsonSerializer.Serialize(this, PacketJson);

        public static ChatPacket? FromJson(string json)
        {
            try { return JsonSerializer.Deserialize<ChatPacket>(json, PacketJson); }
            catch { return null; }
        }
    }
}
