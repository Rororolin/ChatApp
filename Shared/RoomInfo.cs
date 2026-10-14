// ============================================================
//  Shared/RoomInfo.cs — метаданные чата для списка бесед
// ============================================================
using System.Collections.Generic;
using System.Text.Json;

namespace ChatApp.Shared
{
    public class RoomInfo
    {
        public string Room  { get; set; } = "";
        public string Title { get; set; } = "";
        public string Kind  { get; set; } = ""; // "dm" | "grp"
    }

    public static class RoomJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented       = false
        };

        public static string SerializeRooms(List<RoomInfo> rooms) =>
            JsonSerializer.Serialize(rooms, Options);

        public static List<RoomInfo>? DeserializeRooms(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<RoomInfo>();
            try { return JsonSerializer.Deserialize<List<RoomInfo>>(json, Options); }
            catch { return null; }
        }
    }

    public class OnlineUserInfo
    {
        public string Username    { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    public static class OnlineUsersJson
    {
        public static string Serialize(List<OnlineUserInfo> users) =>
            JsonSerializer.Serialize(users, RoomJson.Options);

        public static List<OnlineUserInfo>? Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<List<OnlineUserInfo>>(json, RoomJson.Options); }
            catch { return null; }
        }
    }
}
