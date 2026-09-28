using System;
using System.IO;
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
