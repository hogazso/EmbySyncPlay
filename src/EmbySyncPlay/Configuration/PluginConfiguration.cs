using MediaBrowser.Model.Plugins;

namespace EmbySyncPlay.Configuration
{
    /// <summary>
    /// Szerver-globális (admin) beállítások. Lásd ARCHITECTURE.md 10. pont — a drift-tolerancia
    /// szándékosan admin-szintű, nem felhasználónkénti.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Max ennyi másodperc eltérés engedett a party hivatalos pozíciójától, mielőtt a
        /// szerver korrekciós Seek parancsot küld egy résztvevőnek. ARCHITECTURE.md 4. pont.</summary>
        public int DriftToleranceSeconds { get; set; } = 3;

        /// <summary>Egy kiküldött Seek után ennyi másodpercig nem értékeljük újra az adott
        /// résztvevő driftjét, hogy a puffer stabilizálódhasson. ARCHITECTURE.md 4.1 pont.</summary>
        public int SeekCooldownSeconds { get; set; } = 6;

        /// <summary>Ennyi percnyi inaktivitás után a SyncSessionCleanupTask megszünteti a
        /// party-t. ARCHITECTURE.md 2.6 pont.</summary>
        public int SessionIdleTimeoutMinutes { get; set; } = 30;

        /// <summary>Milyen gyakran (mp) ír a SyncSessionManager JSON pillanatképet a
        /// perzisztenciához. ARCHITECTURE.md 2.2 pont.</summary>
        public int PersistenceSnapshotIntervalSeconds { get; set; } = 30;

        public bool ChatEnabled { get; set; } = true;

        public int MaxParticipantsPerSession { get; set; } = 25;
    }
}
