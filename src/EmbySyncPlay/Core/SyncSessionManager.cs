using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbySyncPlay.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using Newtonsoft.Json;

namespace EmbySyncPlay.Core
{
    /// <summary>
    /// A rendszer agya — ARCHITECTURE.md 2.2. Memóriában tartja az aktív SyncSession-öket,
    /// JSON pillanatképpel perzisztál, propagálja a Play/Pause/Seek parancsokat a résztvevők
    /// felé a natív Emby remote-control API-n (ISessionManager), és eseményeket bocsát ki,
    /// amiket az EmbySyncPlayEntryPoint SSE-n keresztül továbbít a Web kliensnek.
    /// </summary>
    public class SyncSessionManager
    {
        private readonly ConcurrentDictionary<Guid, SyncSession> _sessions = new ConcurrentDictionary<Guid, SyncSession>();
        private readonly ConcurrentDictionary<string, UserDeviceProfile> _deviceProfiles = new ConcurrentDictionary<string, UserDeviceProfile>();

        private readonly ISessionManager _embySessionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly ILogger _logger;
        private readonly string _dataFolderPath;
        private readonly object _persistLock = new object();

        public event Action<SyncSession> SessionChanged;
        public event Action<Guid> SessionRemoved;
        public event Action<ChatMessage> ChatMessageAdded;

        public SyncSessionManager(ISessionManager embySessionManager, ILibraryManager libraryManager,
            IUserManager userManager, ILogger logger, string pluginDataFolderPath)
        {
            _embySessionManager = embySessionManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _logger = logger;
            _dataFolderPath = pluginDataFolderPath;

            Directory.CreateDirectory(_dataFolderPath);
            LoadFromDisk();
        }

        private string SessionsSnapshotPath => Path.Combine(_dataFolderPath, "sessions.json");
        private string DeviceProfilesPath => Path.Combine(_dataFolderPath, "devices.json");

        // ---------------------------------------------------------------
        // Party élet-ciklus — ARCHITECTURE.md 7. pont
        // ---------------------------------------------------------------

        public SyncSession CreateSession(string hostUserId, string hostDeviceId, string hostEmbySessionId,
            MediaKind mediaKind, string itemId, IEnumerable<string> playQueue, SessionVisibility visibility)
        {
            var session = new SyncSession
            {
                HostUserId = hostUserId,
                MediaKind = mediaKind,
                ItemId = itemId,
                PlayQueue = playQueue?.ToList() ?? new List<string> { itemId },
                Visibility = visibility,
                State = PlaybackState.Playing,
                PositionTicks = 0,
                LastUpdatedUtc = DateTime.UtcNow
            };

            session.Participants.Add(new Participant
            {
                UserId = hostUserId,
                DeviceId = hostDeviceId,
                EmbySessionId = hostEmbySessionId,
                Role = ParticipantRole.Host,
                Mode = ParticipantMode.Watching
            });

            _sessions[session.Id] = session;
            PersistAsync();
            SessionChanged?.Invoke(session);
            return session;
        }

        public IEnumerable<SyncSession> GetPublicSessions()
        {
            return _sessions.Values.Where(s => s.Visibility == SessionVisibility.Public);
        }

        /// <summary>Csak azok a meghívásos party-k, amikhez a userId hozzáférhet.</summary>
        public IEnumerable<SyncSession> GetVisibleSessions(string userId)
        {
            return _sessions.Values.Where(s =>
                s.Visibility == SessionVisibility.Public ||
                s.HostUserId == userId ||
                s.InvitedUserIds.Contains(userId) ||
                s.Participants.Any(p => p.UserId == userId));
        }

        public SyncSession GetSession(Guid id)
        {
            _sessions.TryGetValue(id, out var session);
            return session;
        }

        public IEnumerable<SyncSession> GetAllSessions()
        {
            return _sessions.Values;
        }

        public Participant Join(Guid sessionId, string userId, string deviceId, string embySessionId, ParticipantMode mode)
        {
            var session = GetSession(sessionId);
            if (session == null)
            {
                throw new InvalidOperationException($"Nincs ilyen SyncSession: {sessionId}");
            }

            if (session.Visibility == SessionVisibility.InviteOnly &&
                session.HostUserId != userId &&
                !session.InvitedUserIds.Contains(userId))
            {
                throw new UnauthorizedAccessException("Ehhez a party-hoz nincs meghívásod.");
            }

            var participant = new Participant
            {
                UserId = userId,
                DeviceId = deviceId,
                EmbySessionId = mode == ParticipantMode.Watching ? embySessionId : null,
                Role = ParticipantRole.Guest,
                Mode = mode
            };
            session.Participants.Add(participant);
            session.LastUpdatedUtc = DateTime.UtcNow;

            if (mode == ParticipantMode.Watching)
            {
                // Élő NAS-teszten (2026-09-28) kiderült: a session.PositionTicks csak a host
                // periodikus (kb. 10 mp-enkénti) PlaybackProgress jelentésekor frissül, ezért
                // egy gyors csatlakozásnál még elavult (0) lehetett — az új résztvevő elölről
                // kezdte a filmet. Csatlakozáskor ezért frissen lekérdezzük a host ÉLŐ
                // pozícióját az Emby ISessionManager.Sessions-ből, nem várunk a következő
                // periodikus jelentésre. ARCHITECTURE.md 5. pont.
                RefreshPositionFromHostLiveState(session);
                SendPlayCommandToParticipant(session, participant);
            }

            PersistAsync();
            SessionChanged?.Invoke(session);
            return participant;
        }

        public void Leave(Guid sessionId, string userId, string deviceId)
        {
            var session = GetSession(sessionId);
            if (session == null) return;

            var participant = session.Participants.FirstOrDefault(p => p.UserId == userId && p.DeviceId == deviceId);
            if (participant == null) return;

            session.Participants.Remove(participant);

            if (participant.Role == ParticipantRole.Host)
            {
                PromoteNextHost(session);
            }

            if (!session.Participants.Any())
            {
                RemoveSession(session.Id);
                return;
            }

            session.LastUpdatedUtc = DateTime.UtcNow;
            PersistAsync();
            SessionChanged?.Invoke(session);
        }

        private void PromoteNextHost(SyncSession session)
        {
            // A legrégebb óta csatlakozott guest válik host-tá. ARCHITECTURE.md 7. pont.
            var next = session.Participants
                .Where(p => p.Mode == ParticipantMode.Watching)
                .OrderBy(p => p.JoinedAtUtc)
                .FirstOrDefault();

            if (next != null)
            {
                next.Role = ParticipantRole.Host;
                session.HostUserId = next.UserId;
            }
        }

        public void RemoveSession(Guid sessionId)
        {
            if (_sessions.TryRemove(sessionId, out _))
            {
                PersistAsync();
                SessionRemoved?.Invoke(sessionId);
            }
        }

        // ---------------------------------------------------------------
        // Szinkronizációs protokoll — ARCHITECTURE.md 4. és 4.1 pont
        // ---------------------------------------------------------------

        /// <summary>A host lejátszási állapotváltozását propagálja minden más résztvevőnek.</summary>
        public void ApplyHostPlaystate(Guid sessionId, PlaybackState state, long positionTicks)
        {
            var session = GetSession(sessionId);
            if (session == null) return;

            session.State = state;
            session.PositionTicks = positionTicks;
            session.LastUpdatedUtc = DateTime.UtcNow;

            foreach (var participant in session.Participants
                .Where(p => p.Mode == ParticipantMode.Watching && p.Role != ParticipantRole.Host))
            {
                SendPlaystateCommandToParticipant(session, participant, state, positionTicks);
            }

            PersistAsync();
            SessionChanged?.Invoke(session);
        }

        /// <summary>Élő NAS-teszten (2026-09-28) kiderült: a host kliens kb. 10 másodpercenként
        /// küld rutinszerű progress-jelentést AKKOR IS, ha semmi nem változott (nem állt meg,
        /// nem seekelt) — ha ilyenkor is teljes Seek+Playstate parancsot küldtünk minden
        /// résztvevőnek (mint az ApplyHostPlaystate csinálja), az folyamatosan megszakította a
        /// csatlakozó lejátszását, ami "az első pár másodperc ismétlődéseként" jelentkezett.
        /// Ez a metódus CSAK a session book-keeping mezőit frissíti (a drift-ellenőrzéshez),
        /// résztvevőnek NEM küld parancsot — azt a RunDriftCorrectionPass végzi, küszöbérték
        /// és cooldown mellett, csak akkor, ha tényleg szétcsúszott valaki.</summary>
        public void UpdateHostPositionSilently(Guid sessionId, PlaybackState state, long positionTicks)
        {
            var session = GetSession(sessionId);
            if (session == null) return;

            session.State = state;
            session.PositionTicks = positionTicks;
            session.LastUpdatedUtc = DateTime.UtcNow;
        }

        /// <summary>A host minden progress-jelentésének belépési pontja. Eldönti, hogy
        /// rutinszerű "telik az idő" jelentésről van-e szó (csendben frissítünk), vagy
        /// tényleges változásról — Play&lt;-&gt;Pause váltás VAGY explicit seek (a jelentett
        /// pozíció jelentősen eltér attól, amit az eltelt idő alapján várnánk) —, amit
        /// AZONNAL, push-szal kell terjeszteni mindenkinek. Élő NAS-teszten (2026-09-28)
        /// derült ki, hogy a puszta state-változásra szűkített push nem elég: egy seek
        /// (akár csak 10 mp-es ugrás a gombbal) NEM jár állapotváltással, ezért anélkül
        /// a résztvevők csak a következő (akár 10+ mp-es késésű) drift-korrekciós körben
        /// kapták volna meg a javítást, vagy egyáltalán nem, ha a drift a küszöb alatt
        /// maradt egy köztes állapotban.</summary>
        public void ReportHostProgress(Guid sessionId, PlaybackState newState, long reportedPositionTicks)
        {
            var session = GetSession(sessionId);
            if (session == null) return;

            var elapsed = DateTime.UtcNow - session.LastUpdatedUtc;
            var expectedPositionTicks = session.PositionTicks +
                (session.State == PlaybackState.Playing ? elapsed.Ticks : 0);

            // Bőkezű tolerancia (2 mp) a hálózati/jelentési késleltetésnek — ennél nagyobb
            // eltérés csak explicit seekkel magyarázható, nem a "telik az idő" normál esettel.
            var seekToleranceTicks = TimeSpan.FromSeconds(2).Ticks;
            var looksLikeSeek = Math.Abs(reportedPositionTicks - expectedPositionTicks) > seekToleranceTicks;

            if (session.State != newState || looksLikeSeek)
            {
                ApplyHostPlaystate(sessionId, newState, reportedPositionTicks);
            }
            else
            {
                UpdateHostPositionSilently(sessionId, newState, reportedPositionTicks);
            }
        }

        /// <summary>A host új elemre vált (pl. a sorozat következő epizódja automatikusan
        /// elindul) — a party ItemId-je frissül, a pozíció nullázódik, és minden Watching
        /// résztvevő megkapja az új PlayCommand-ot, hogy kövesse a hostot. Nem "Join"-ként
        /// kezeljük (nem hozunk létre új Participant-et), csak új PlayCommand-ot küldünk a
        /// már meglévőknek. ARCHITECTURE.md — a felhasználó explicit kérésére hozzáadva.</summary>
        public void ChangeHostItem(Guid sessionId, string newItemId)
        {
            var session = GetSession(sessionId);
            if (session == null) return;

            session.ItemId = newItemId;
            session.PlayQueue = new List<string> { newItemId };
            session.CurrentQueueIndex = 0;
            session.PositionTicks = 0;
            session.State = PlaybackState.Playing;
            session.LastUpdatedUtc = DateTime.UtcNow;

            foreach (var participant in session.Participants
                .Where(p => p.Mode == ParticipantMode.Watching && p.Role != ParticipantRole.Host))
            {
                SendPlayCommandToParticipant(session, participant);
            }

            PersistAsync();
            SessionChanged?.Invoke(session);
        }

        /// <summary>Egy résztvevő progress-jelentése — a drift-ellenőrzés belépési pontja.
        /// A tényleges kiértékelést a SyncSessionCleanupTask / EntryPoint periodikusan hívja meg
        /// minden aktív party minden résztvevőjére.</summary>
        public void ReportParticipantProgress(Guid sessionId, string userId, string deviceId,
            long positionTicks, bool isBuffering)
        {
            var session = GetSession(sessionId);
            var participant = session?.Participants.FirstOrDefault(p => p.UserId == userId && p.DeviceId == deviceId);
            if (participant == null) return;

            participant.LastReportedPositionTicks = positionTicks;
            participant.LastReportedAtUtc = DateTime.UtcNow;
            participant.IsBuffering = isBuffering;
        }

        /// <summary>Minden aktív party minden Watching résztvevőjét kiértékeli, és szükség esetén
        /// célzott Seek-et küld. ARCHITECTURE.md 4. és 4.1 pont — cooldown + buffering kizárás.</summary>
        public void RunDriftCorrectionPass(int driftToleranceSeconds, int seekCooldownSeconds)
        {
            var driftToleranceTicks = TimeSpan.FromSeconds(driftToleranceSeconds).Ticks;

            foreach (var session in _sessions.Values)
            {
                foreach (var participant in session.Participants
                    .Where(p => p.Mode == ParticipantMode.Watching && p.Role != ParticipantRole.Host))
                {
                    if (participant.IsBuffering) continue;

                    if (participant.LastSeekSentUtc.HasValue &&
                        (DateTime.UtcNow - participant.LastSeekSentUtc.Value).TotalSeconds < seekCooldownSeconds)
                    {
                        continue;
                    }

                    var expectedPosition = session.PositionTicks +
                        (session.State == PlaybackState.Playing
                            ? (DateTime.UtcNow - session.LastUpdatedUtc).Ticks
                            : 0);

                    var drift = Math.Abs(expectedPosition - participant.LastReportedPositionTicks);
                    if (drift > driftToleranceTicks)
                    {
                        SendPlaystateCommandToParticipant(session, participant, PlaybackState.Playing, expectedPosition, seekOnly: true);
                        participant.LastSeekSentUtc = DateTime.UtcNow;
                    }
                }
            }
        }

        /// <summary>A host EmbySessionId-jéhez tartozó élő SessionInfo.PlayState.PositionTicks
        /// alapján frissíti a session pozícióját, ha az elérhető — enélkül egy gyors
        /// csatlakozás a host utolsó (esetleg percekkel korábbi) periodikus jelentésének
        /// pozíciójára, akár 0-ra ugorna. ARCHITECTURE.md 5. pont.</summary>
        private void RefreshPositionFromHostLiveState(SyncSession session)
        {
            var host = session.Participants.FirstOrDefault(p => p.Role == ParticipantRole.Host && p.Mode == ParticipantMode.Watching);
            if (host == null || string.IsNullOrEmpty(host.EmbySessionId)) return;

            var liveSession = _embySessionManager.Sessions.FirstOrDefault(s => s.Id == host.EmbySessionId);
            var positionTicks = liveSession?.PlayState?.PositionTicks;
            if (!positionTicks.HasValue) return;

            session.PositionTicks = positionTicks.Value;
            session.State = liveSession.PlayState.IsPaused ? PlaybackState.Paused : PlaybackState.Playing;
            session.LastUpdatedUtc = DateTime.UtcNow;
        }

        /// <summary>A PlayRequest.ItemIds ebben az SDK-verzióban a belső Int64 InternalId-eket
        /// várja, nem a publikus Guid-eket — ezért fel kell oldani az ILibraryManager-en
        /// keresztül. Audio party esetén a teljes PlayQueue-t adjuk át, hogy a kliens natív
        /// "következő szám" navigációja is működjön; StartIndex jelzi, honnan induljon.</summary>
        /// <summary>Élő NAS-teszten (2026-09-28) kiderült: a natív SendPlayCommand/
        /// SendPlaystateCommand hívások async Task-ot adnak vissza, de a korábbi kód nem
        /// várta be (nincs await) — emiatt egy menet közbeni hiba CSENDBEN elveszett,
        /// try/catch nélkül, mert az csak a Task LÉTREHOZÁSAKOR dobott kivételt fogta
        /// volna el, nem a végrehajtás közbenit. Ez az egyik oka volt annak, hogy a
        /// "kövesse a hostot új elemre váltáskor" funkció nem volt megbízható. Most
        /// szinkron módon (.GetAwaiter().GetResult()) megvárjuk, hogy a try/catch
        /// ténylegesen elkapja és logolja a hibát — és a kritikus, elem-váltó parancsnál
        /// egy rövid újrapróbálkozást is beiktatunk tranziens hibák ellen.</summary>
        private void SendPlayCommandToParticipant(SyncSession session, Participant participant)
        {
            if (string.IsNullOrEmpty(participant.EmbySessionId)) return;

            var itemIds = ResolveInternalIds(session);
            if (itemIds.Length == 0)
            {
                _logger.Warn("EmbySyncPlay: nem sikerült feloldani a session {0} ItemId(k)-jét, PlayCommand kihagyva", session.Id);
                return;
            }

            var request = new PlayRequest
            {
                ItemIds = itemIds,
                StartPositionTicks = session.PositionTicks,
                StartIndex = session.MediaKind == MediaKind.Audio ? session.CurrentQueueIndex : (int?)null,
                PlayCommand = PlayCommand.PlayNow
            };

            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    _embySessionManager.SendPlayCommand(
                        controllingSessionId: null,
                        sessionId: participant.EmbySessionId,
                        command: request,
                        cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

                    _logger.Info("EmbySyncPlay: PlayCommand elküldve {0} résztvevőnek (session={1}, item={2}, kísérlet={3})",
                        participant.UserId, session.Id, session.ItemId, attempt);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("EmbySyncPlay: PlayCommand küldése sikertelen {0} résztvevőnek ({1}. kísérlet)", ex, participant.UserId, attempt);
                    if (attempt < maxAttempts) Thread.Sleep(300);
                }
            }
        }

        /// <summary>Élő NAS-teszten (2026-09-28) kiderült: a REST API "Id" mezője ezen a
        /// szerververzión (4.10.0.40) egyszerű Int64 InternalId-t ad vissza decimális
        /// szövegként (pl. "597723"), NEM Guid-et — annak ellenére, hogy a BaseItem.Id
        /// property maga Guid típusú. A Web kliens az "Id" mezőt küldi tovább változatlanul,
        /// ezért itt mindkét formátumot kezelni kell: előbb Int64-ként próbáljuk (ez a gyakori
        /// eset), csak utána esünk vissza Guid-alapú feloldásra.</summary>
        private long[] ResolveInternalIds(SyncSession session)
        {
            var itemIdStrings = session.MediaKind == MediaKind.Audio && session.PlayQueue.Any()
                ? session.PlayQueue
                : new List<string> { session.ItemId };

            var ids = new List<long>();
            foreach (var itemIdString in itemIdStrings)
            {
                if (long.TryParse(itemIdString, out var internalId))
                {
                    ids.Add(internalId);
                }
                else if (Guid.TryParse(itemIdString, out var guid))
                {
                    var item = _libraryManager.GetItemById(guid);
                    if (item != null) ids.Add(item.InternalId);
                }
            }
            return ids.ToArray();
        }

        /// <summary>Első lépésben a pozícióra ugrat mindenkit (SeekPositionTicks), majd —
        /// ha nem csak drift-korrekcióról van szó — egy második paranccsal állítja be a
        /// Play/Pause állapotot is. ARCHITECTURE.md 4. pont.</summary>
        private void SendPlaystateCommandToParticipant(SyncSession session, Participant participant,
            PlaybackState state, long positionTicks, bool seekOnly = false)
        {
            if (string.IsNullOrEmpty(participant.EmbySessionId)) return;

            var seekOk = SendPlaystateCommandWithRetry(participant, new PlaystateRequest
            {
                Command = PlaystateCommand.Seek,
                SeekPositionTicks = positionTicks
            });

            if (seekOk && !seekOnly)
            {
                SendPlaystateCommandWithRetry(participant, new PlaystateRequest
                {
                    Command = state == PlaybackState.Playing ? PlaystateCommand.Unpause : PlaystateCommand.Pause
                });
            }
        }

        /// <summary>Lásd SendPlayCommandToParticipant komment — itt is szinkron várakozás +
        /// újrapróbálkozás kell, mert az await nélküli hívás elnyelte a hibákat.</summary>
        private bool SendPlaystateCommandWithRetry(Participant participant, PlaystateRequest request)
        {
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    _embySessionManager.SendPlaystateCommand(
                        controllingSessionId: null,
                        sessionId: participant.EmbySessionId,
                        command: request,
                        cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("EmbySyncPlay: Playstate parancs ({0}) küldése sikertelen {1} résztvevőnek ({2}. kísérlet)",
                        ex, request.Command, participant.UserId, attempt);
                    if (attempt < maxAttempts) Thread.Sleep(300);
                }
            }
            return false;
        }

        // ---------------------------------------------------------------
        // Chat — ARCHITECTURE.md 6. és 3.4 pont
        // ---------------------------------------------------------------

        public ChatMessage AddChatMessage(Guid sessionId, string userId, string text)
        {
            var session = GetSession(sessionId);
            if (session == null)
            {
                throw new InvalidOperationException($"Nincs ilyen SyncSession: {sessionId}");
            }

            var displayName = Guid.TryParse(userId, out var userGuid)
                ? _userManager.GetUserById(userGuid)?.Name ?? userId
                : userId;

            var message = new ChatMessage
            {
                SyncSessionId = sessionId,
                UserId = userId,
                Monogram = ComputeMonogram(session, userId, displayName),
                Text = text
            };

            session.ChatLog.Add(message);
            PersistAsync();
            ChatMessageAdded?.Invoke(message);
            return message;
        }

        /// <summary>Party-n belül ütközésmentesített monogram. ARCHITECTURE.md 3.4 —
        /// "Péter" -> "P", ütközés esetén "Pé" / "Pe".</summary>
        private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, string>> _monogramCache =
            new ConcurrentDictionary<Guid, ConcurrentDictionary<string, string>>();

        private string ComputeMonogram(SyncSession session, string userId, string displayName)
        {
            var cache = _monogramCache.GetOrAdd(session.Id, _ => new ConcurrentDictionary<string, string>());
            if (cache.TryGetValue(userId, out var existing)) return existing;

            var baseLetter = string.IsNullOrEmpty(displayName) ? "?" : displayName.Substring(0, 1).ToUpperInvariant();
            var candidate = baseLetter;
            var takenByOther = cache.Values.Contains(candidate);

            if (takenByOther && displayName.Length > 1)
            {
                candidate = displayName.Substring(0, 2);
            }

            cache[userId] = candidate;
            return candidate;
        }

        // ---------------------------------------------------------------
        // Eszköz-elnevezés — ARCHITECTURE.md 3.2
        // ---------------------------------------------------------------

        public UserDeviceProfile GetOrCreateDeviceProfile(string userId)
        {
            return _deviceProfiles.GetOrAdd(userId, id => new UserDeviceProfile { UserId = id });
        }

        public void SetDeviceLabel(string userId, string embyDeviceId, string label)
        {
            var profile = GetOrCreateDeviceProfile(userId);
            var existing = profile.Devices.FirstOrDefault(d => d.EmbyDeviceId == embyDeviceId);
            if (existing != null)
            {
                existing.Label = label;
            }
            else
            {
                profile.Devices.Add(new DeviceLabel { EmbyDeviceId = embyDeviceId, Label = label });
            }
            PersistDeviceProfilesAsync();
        }

        // ---------------------------------------------------------------
        // Takarítás — ARCHITECTURE.md 2.6
        // ---------------------------------------------------------------

        public void RemoveIdleSessions(int idleTimeoutMinutes)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-idleTimeoutMinutes);
            var idleIds = _sessions.Values
                .Where(s => s.LastUpdatedUtc < cutoff)
                .Select(s => s.Id)
                .ToList();

            foreach (var id in idleIds)
            {
                _logger.Info("EmbySyncPlay: inaktív party takarítása: {0}", id);
                RemoveSession(id);
            }
        }

        // ---------------------------------------------------------------
        // Perzisztencia — ARCHITECTURE.md 2.2, best-effort
        // ---------------------------------------------------------------

        private void PersistAsync()
        {
            Task.Run(() =>
            {
                lock (_persistLock)
                {
                    try
                    {
                        var json = JsonConvert.SerializeObject(_sessions.Values.ToList(), Formatting.None);
                        File.WriteAllText(SessionsSnapshotPath, json);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("EmbySyncPlay: sessions.json mentése sikertelen", ex);
                    }
                }
            });
        }

        private void PersistDeviceProfilesAsync()
        {
            Task.Run(() =>
            {
                lock (_persistLock)
                {
                    try
                    {
                        var json = JsonConvert.SerializeObject(_deviceProfiles.Values.ToList(), Formatting.None);
                        File.WriteAllText(DeviceProfilesPath, json);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("EmbySyncPlay: devices.json mentése sikertelen", ex);
                    }
                }
            });
        }

        /// <summary>Szerverindításkor visszaolvassa a legutóbbi pillanatképet. Ha bármi hibázik,
        /// egyszerűen üresen indulunk — ez best-effort funkció, nem kritikus. ARCHITECTURE.md 2.2.</summary>
        private void LoadFromDisk()
        {
            try
            {
                if (File.Exists(SessionsSnapshotPath))
                {
                    var json = File.ReadAllText(SessionsSnapshotPath);
                    var sessions = JsonConvert.DeserializeObject<List<SyncSession>>(json) ?? new List<SyncSession>();
                    foreach (var session in sessions)
                    {
                        // A résztvevők Emby session-jei valószínűleg megszűntek egy újraindítás
                        // után — ezeket a hívó (EntryPoint.Run) validálja és tisztítja tovább.
                        _sessions[session.Id] = session;
                    }
                    _logger.Info("EmbySyncPlay: {0} party visszaállítva a pillanatképből", sessions.Count);
                }

                if (File.Exists(DeviceProfilesPath))
                {
                    var json = File.ReadAllText(DeviceProfilesPath);
                    var profiles = JsonConvert.DeserializeObject<List<UserDeviceProfile>>(json) ?? new List<UserDeviceProfile>();
                    foreach (var profile in profiles)
                    {
                        _deviceProfiles[profile.UserId] = profile;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("EmbySyncPlay: pillanatkép visszaolvasása sikertelen, üres állapotból indulunk", ex);
            }
        }

        /// <summary>Felhasználónév feloldása megjelenítéshez — ARCHITECTURE.md 10. pont,
        /// felhasználó explicit kérésére (a nyers UserId GUID nem értelmezhető emberi
        /// felhasználó számára).</summary>
        public string ResolveDisplayName(string userId)
        {
            if (Guid.TryParse(userId, out var guid))
            {
                return _userManager.GetUserById(guid)?.Name ?? userId;
            }
            return userId;
        }

        /// <summary>Item cím feloldása megjelenítéshez — ugyanaz az Int64/Guid kétértelműség
        /// vonatkozik rá, mint a ResolveInternalIds-re (lásd ott a komment).</summary>
        public string ResolveItemName(string itemId)
        {
            if (long.TryParse(itemId, out var internalId))
            {
                return _libraryManager.GetItemById(internalId)?.Name ?? itemId;
            }
            if (Guid.TryParse(itemId, out var guid))
            {
                return _libraryManager.GetItemById(guid)?.Name ?? itemId;
            }
            return itemId;
        }

        /// <summary>Az EntryPoint hívja szerverindításkor — eltávolítja azokat a résztvevőket,
        /// akiknek már nem él az Emby session-je.</summary>
        public void ValidateRestoredSessions(Func<string, bool> isEmbySessionAlive)
        {
            foreach (var session in _sessions.Values.ToList())
            {
                session.Participants.RemoveAll(p =>
                    p.Mode == ParticipantMode.Watching &&
                    !string.IsNullOrEmpty(p.EmbySessionId) &&
                    !isEmbySessionAlive(p.EmbySessionId));

                if (!session.Participants.Any())
                {
                    RemoveSession(session.Id);
                }
            }
        }
    }
}
