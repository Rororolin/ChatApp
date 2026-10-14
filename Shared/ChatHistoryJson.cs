// ============================================================
//  Shared/ChatHistoryJson.cs — история сообщений (сервер ↔ клиент)
// ============================================================
using System.Collections.Generic;
using System.Text.Json;

namespace ChatApp.Shared
{
    public sealed class HistoryLineDto
    {
        public string Sender  { get; set; } = "";
        public string Message { get; set; } = "";
        public long   Ts      { get; set; }
    }

    public static class ChatHistoryJson
    {
        public static string SerializeLines(List<HistoryLineDto> lines) =>
            JsonSerializer.Serialize(lines, ChatPacket.PacketJson);

        public static List<HistoryLineDto> DeserializeLines(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<HistoryLineDto>();
            try
            {
                return JsonSerializer.Deserialize<List<HistoryLineDto>>(json, ChatPacket.PacketJson)
                       ?? new List<HistoryLineDto>();
            }
            catch
            {
                return new List<HistoryLineDto>();
            }
        }
    }
}
