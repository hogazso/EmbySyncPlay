using System;

namespace EmbySyncPlay.Models
{
    /// <summary>ARCHITECTURE.md 6. és 3.4 pont — monogramos, tömör chat megjelenítés.</summary>
    public class ChatMessage
    {
        public Guid SyncSessionId { get; set; }

        public string UserId { get; set; }

        /// <summary>Előre kiszámított, a party-n belül ütközésmentesített monogram (pl. "P").</summary>
        public string Monogram { get; set; }

        public string Text { get; set; }

        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }
}
