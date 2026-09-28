using System;
using System.IO;
using System.Threading;
using EmbySyncPlay.Core;
using EmbySyncPlay.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace EmbySyncPlay.EntryPoints
{
    /// <summary>
    /// ARCHITECTURE.md 2.3 — életciklus-vezérlő. Feliratkozik a natív Emby lejátszási
    /// eseményekre, és ezekből táplálja a SyncSessionManager-t / SseBroadcaster-t.
    /// </summary>
    public class EmbySyncPlayEntryPoint : IServerEntryPoint
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger _logger;
        private Timer _cleanupTimer;

        public static SyncSessionManager SyncManager { get; private set; }
        public static SseBroadcaster Broadcaster { get; private set; }

        public EmbySyncPlayEntryPoint(ISessionManager sessionManager, ILogManager logManager,
            MediaBrowser.Common.Configuration.IApplicationPaths applicationPaths,
            ILibraryManager libraryManager, IUserManager userManager)
        {
            _sessionManager = sessionManager;
            _logger = logManager.GetLogger("EmbySyncPlay");

            var dataFolder = Path.Combine(applicationPaths.PluginConfigurationsPath, "EmbySyncPlay");
            // Az ILibraryManager kell a session.ItemId (Guid) -> Emby belső Int64 InternalId
            // feloldásához, mert a PlayRequest.ItemIds ebben az SDK-verzióban Int64[]-t vár,
            // nem a publikus Guid-eket. Lásd SendPlayCommandToParticipant.
            SyncManager = new SyncSessionManager(sessionManager, libraryManager, userManager, _logger, dataFolder);
            Broadcaster = new SseBroadcaster();
        }

        public void Run()
        {
            _logger.Info("EmbySyncPlay entry point indul.");

            // Szerverindításkor eltávolítjuk azokat a visszaállított résztvevőket, akiknek már
            // nem él az Emby session-je. ARCHITECTURE.md 2.2.
            SyncManager.ValidateRestoredSessions(embySessionId =>
                System.Linq.Enumerable.Any(_sessionManager.Sessions, s => s.Id == embySessionId));

            SyncManager.SessionChanged += OnSessionChanged;
            SyncManager.SessionRemoved += OnSessionRemoved;
            SyncManager.ChatMessageAdded += OnChatMessageAdded;

            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _sessionManager.SessionEnded += OnSessionEnded;

            // ÉLŐ NAS-teszten (2026-09-28) kiderült: a SyncSessionCleanupTask (IScheduledTask,
            // lásd EntryPoints/SyncSessionCleanupTask.cs) helyesen regisztrálódik az Emby
            // /ScheduledTasks listájában, és kézi indításra ("ScheduledTasks/Running/{id}")
            // hibátlanul lefut — DE a 10 mp-es IntervalTrigger-je SOHA nem tüzel el magától
            // (a szerver 10+ perce fut, a task State mindvégig "Idle" maradt, LastExecutionResult
            // egyáltalán nem is szerepelt a listázásban, amíg kézzel el nem indítottuk). Emiatt a
            // drift-korrekció és az inaktív party-takarítás GYAKORLATBAN eddig soha nem futott le
            // automatikusan. Mivel ennek az Emby-oldali trigger-hibának a pontos okát a plugin
            // SDK-ból nem lehet kideríteni/javítani, egy saját, az Emby scheduler-től teljesen
            // független .NET Timer-t használunk megbízható helyettesítőként — ez ugyanazt a két
            // SyncSessionManager metódust hívja, amit a SyncSessionCleanupTask is hívna. A
            // SyncSessionCleanupTask regisztrációját meghagyjuk (ártalmatlan, idempotens
            // műveletek), hátha egy jövőbeli Emby-verzióban mégis working lesz a trigger.
            _cleanupTimer = new Timer(RunPeriodicCleanup, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        private void RunPeriodicCleanup(object state)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return;

                SyncManager.RunDriftCorrectionPass(config.DriftToleranceSeconds, config.SeekCooldownSeconds);
                SyncManager.RemoveIdleSessions(config.SessionIdleTimeoutMinutes);

                // Kiegészítő, ground-truth ellenőrzés: kidobja azokat a Watching résztvevőket,
                // akiknek az Emby session-je már nem létezik — ez GYORSABBAN takaríthat, mint a
                // fenti idő-alapú RemoveIdleSessions (nem kell megvárni a teljes idle timeoutot),
                // bár — élő teszten megfigyelve — ugyanazon az eszközön való újracsatlakozás
                // esetén az Emby újrahasznosíthatja a SessionInfo.Id-t, ilyenkor ez a konkrét
                // ellenőrzés hamis pozitívot ad (élőnek látja), és az idő-alapú takarítás marad
                // az elsődleges védőháló.
                SyncManager.ValidateRestoredSessions(embySessionId =>
                    System.Linq.Enumerable.Any(_sessionManager.Sessions, s => s.Id == embySessionId));
            }
            catch (Exception ex)
            {
                _logger.ErrorException("EmbySyncPlay: periodikus party-takarítás sikertelen", ex);
            }
        }

        // -----------------------------------------------------------------
        // Kimenő: belső állapotváltozás -> SSE push a Web kliens felé
        // ARCHITECTURE.md 2.5
        // -----------------------------------------------------------------

        private void OnSessionChanged(SyncSession session)
        {
            Broadcaster.Publish("party.playstate_changed", session);
        }

        private void OnSessionRemoved(Guid sessionId)
        {
            Broadcaster.Publish("party.removed", new { sessionId });
        }

        private void OnChatMessageAdded(ChatMessage message)
        {
            Broadcaster.Publish("party.chat_message", message);
        }

        // -----------------------------------------------------------------
        // Bejövő: natív Emby lejátszási események -> SyncSessionManager
        // ARCHITECTURE.md 4. pont — csak akkor reagálunk, ha a küldő egy aktív party HOST-ja.
        //
        // MEGJEGYZÉS: a PlaybackProgressEventArgs / PlaybackStopEventArgs pontos property-nevei
        // (Item, PlaySessionId, PositionTicks, IsPaused, stb.) SDK-verziónként eltérhetnek —
        // build előtt egyeztetni kell a ténylegesen telepített csomaggal.
        // -----------------------------------------------------------------

        /// <summary>Ha a host ÚJ elemre vált (pl. a sorozat következő epizódja automatikusan
        /// indul, vagy a host manuálisan másik filmet/számot választ), a többi Watching
        /// résztvevőnek is követnie kell — a party ItemId-je frissül, és mindenki megkapja
        /// az új PlayCommand-ot. Erre külön esemény kell (PlaybackStart), mert a
        /// PlaybackProgress csak a MÁR futó elem pozícióváltozásait jelenti.</summary>
        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            var session = FindSessionForEmbySessionId(e.Session.Id);
            if (session == null || e.Item == null) return;

            var participant = FindParticipant(session, e.Session.Id);
            if (participant == null || participant.Role != ParticipantRole.Host) return;

            var newItemId = e.Item.InternalId.ToString();
            if (newItemId == session.ItemId) return;

            SyncManager.ChangeHostItem(session.Id, newItemId);
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            var session = FindSessionForEmbySessionId(e.Session.Id);
            if (session == null) return;

            var participant = FindParticipant(session, e.Session.Id);
            if (participant == null) return;

            if (participant.Role == ParticipantRole.Host)
            {
                var newState = e.IsPaused ? PlaybackState.Paused : PlaybackState.Playing;
                var positionTicks = e.PlaybackPositionTicks ?? 0;

                // A tényleges eldöntés (rutin progress vs. valódi ugrás/state-váltás) a
                // SyncSessionManager-ben történik — lásd ReportHostProgress komment.
                SyncManager.ReportHostProgress(session.Id, newState, positionTicks);
            }
            else
            {
                SyncManager.ReportParticipantProgress(
                    session.Id, participant.UserId, participant.DeviceId,
                    e.PlaybackPositionTicks ?? 0,
                    isBuffering: false);
            }
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            var session = FindSessionForEmbySessionId(e.Session.Id);
            if (session == null) return;

            var participant = FindParticipant(session, e.Session.Id);
            if (participant == null) return;

            // FONTOS: a hostot NEM léptetjük ki automatikusan Stop eseményre — a "Stop" a
            // natív Emby kliensben epizódváltáskor (pl. a sorozat következő része) is
            // lefut, közvetlenül egy PlaybackStart előtt az új elemre. Ha itt kiléptetnénk
            // a hostot, minden epizódváltás szétverné a party-t, mielőtt az OnPlaybackStart
            // (ChangeHostItem) egyáltalán lefutna. Csak a vendégek lépnek ki automatikusan,
            // ha a saját lejátszásukat leállítják.
            if (participant.Role == ParticipantRole.Guest)
            {
                SyncManager.Leave(session.Id, participant.UserId, participant.DeviceId);
            }
        }

        private void OnSessionEnded(object sender, SessionEventArgs e)
        {
            var session = FindSessionForEmbySessionId(e.SessionInfo.Id);
            if (session == null) return;

            var participant = FindParticipant(session, e.SessionInfo.Id);
            if (participant == null) return;

            SyncManager.Leave(session.Id, participant.UserId, participant.DeviceId);
        }

        private static SyncSession FindSessionForEmbySessionId(string embySessionId)
        {
            foreach (var session in SyncManager.GetAllSessions())
            {
                foreach (var p in session.Participants)
                {
                    if (p.EmbySessionId == embySessionId) return session;
                }
            }
            return null;
        }

        private static Participant FindParticipant(SyncSession session, string embySessionId)
        {
            return session.Participants.Find(p => p.EmbySessionId == embySessionId);
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();

            SyncManager.SessionChanged -= OnSessionChanged;
            SyncManager.SessionRemoved -= OnSessionRemoved;
            SyncManager.ChatMessageAdded -= OnChatMessageAdded;

            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _sessionManager.SessionEnded -= OnSessionEnded;
        }
    }
}
