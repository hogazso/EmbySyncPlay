using System;

namespace EmbySyncPlay.Models
{
    public enum ParticipantRole
    {
        Host,
        Guest
    }

    /// <summary>
    /// Egy user egy eszközzel egy adott party-ban. ARCHITECTURE.md 3.3 — egy UserId egyszerre
    /// több Participant bejegyzést is kaphat (pl. TV-n néz, telefonon csak chatel).
    /// </summary>
    public enum ParticipantMode
    {
        Watching,
        ChatOnly
    }

    public class Participant
    {
        public string UserId { get; set; }

        /// <summary>A felhasználó saját maga elnevezte Emby eszköz azonosítója. ARCHITECTURE.md 3.2.</summary>
        public string DeviceId { get; set; }

        /// <summary>Az Emby session, amin keresztül a Playstate parancsokat kapja. Watching módban
        /// kötelező, ChatOnly módban nincs hozzá lejátszási session.</summary>
        public string EmbySessionId { get; set; }

        public ParticipantRole Role { get; set; } = ParticipantRole.Guest;

        public ParticipantMode Mode { get; set; } = ParticipantMode.Watching;

        public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Utolsó ismert saját lejátszási pozíció — a drift-ellenőrzéshez.
        /// ARCHITECTURE.md 4. pont.</summary>
        public long LastReportedPositionTicks { get; set; }

        public DateTime LastReportedAtUtc { get; set; }

        /// <summary>A legutóbbi kiküldött korrekciós Seek időpontja — a cooldown számításához.
        /// ARCHITECTURE.md 4.1.</summary>
        public DateTime? LastSeekSentUtc { get; set; }

        /// <summary>Igaz, ha a kliens jelenleg pufferel/transzkódra vár — eddig nem küldünk
        /// neki újabb korrekciós parancsot. ARCHITECTURE.md 4.1.</summary>
        public bool IsBuffering { get; set; }
    }
}
