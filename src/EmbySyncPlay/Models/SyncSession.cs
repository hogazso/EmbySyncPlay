using System;
using System.Collections.Generic;

namespace EmbySyncPlay.Models
{
    public enum MediaKind
    {
        Video,
        Audio
    }

    public enum SessionVisibility
    {
        Public,
        InviteOnly
    }

    public enum PlaybackState
    {
        Playing,
        Paused
    }

    /// <summary>
    /// A "Party". Lásd ARCHITECTURE.md 1. pont. Kizárólag a lejátszási ÁLLAPOT és időbélyeg
    /// szinkronizálódik ezen az objektumon keresztül — sosem maga a média-adatfolyam.
    /// </summary>
    public class SyncSession
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string HostUserId { get; set; }

        public MediaKind MediaKind { get; set; }

        /// <summary>Az éppen lejátszott Emby Item azonosítója.</summary>
        public string ItemId { get; set; }

        /// <summary>Audio party esetén a teljes lejátszási lista; Video esetén üres/1 elemű.</summary>
        public List<string> PlayQueue { get; set; } = new List<string>();

        /// <summary>Melyik index fut a PlayQueue-ból (Audio esetén).</summary>
        public int CurrentQueueIndex { get; set; }

        public SessionVisibility Visibility { get; set; } = SessionVisibility.Public;

        public PlaybackState State { get; set; } = PlaybackState.Paused;

        /// <summary>A party "hivatalos" lejátszási pozíciója, Emby PositionTicks egységben.</summary>
        public long PositionTicks { get; set; }

        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

        public List<Participant> Participants { get; set; } = new List<Participant>();

        public List<ChatMessage> ChatLog { get; set; } = new List<ChatMessage>();

        /// <summary>Meghívásos party esetén a látni engedett felhasználók listája.</summary>
        public List<string> InvitedUserIds { get; set; } = new List<string>();
    }
}
