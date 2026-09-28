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

        public ConcurrentList<Participant> Participants { get; set; } = new ConcurrentList<Participant>();

        public ConcurrentList<ChatMessage> ChatLog { get; set; } = new ConcurrentList<ChatMessage>();

        /// <summary>Meghívásos party esetén a látni engedett felhasználók listája.</summary>
        public ConcurrentList<string> InvitedUserIds { get; set; } = new ConcurrentList<string>();
    }
}
