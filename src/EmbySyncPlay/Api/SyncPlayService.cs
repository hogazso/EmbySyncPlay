using System;
using System.Linq;
using EmbySyncPlay.Core;
using EmbySyncPlay.EntryPoints;
using EmbySyncPlay.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbySyncPlay.Api
{
    /// <summary>
    /// ARCHITECTURE.md 2.4 — a REST végpontok tényleges implementációja. A felhasználó
    /// azonosítása kizárólag a meglévő Emby-hitelesítésre épül (ARCHITECTURE.md 3.1) —
    /// nincs saját jelszó/fiók, az IAuthorizationContext oldja fel a UserId-t a bejövő
    /// X-Emby-Token / Authorization fejlécből.
    /// </summary>
    public class SyncPlayService : IService, IRequiresRequest
    {
        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; }

        public SyncPlayService(IAuthorizationContext authContext)
        {
            _authContext = authContext;
        }

        private SyncSessionManager Manager => EmbySyncPlayEntryPoint.SyncManager;

        // MEGJEGYZÉS: AuthorizationInfo.UserId ebben az SDK-verzióban egy belső Int64 azonosító,
        // NEM a publikus Guid — a mi DTO-ink és a SessionInfo.UserId is a Guid-string formát
        // használják, ezért az AuthorizationInfo.User (Entities.User, Id:Guid) tulajdonságból
        // olvassuk ki a helyes, kifelé is konzisztens azonosítót.
        private string CurrentUserId => _authContext.GetAuthorizationInfo(Request).User.Id.ToString("N");

        // -----------------------------------------------------------------
        // Sessions
        // -----------------------------------------------------------------

        public object Get(GetSessionsRequest request)
        {
            var sessions = Manager.GetVisibleSessions(CurrentUserId).Select(ToDto).ToList();
            return sessions;
        }

        public object Post(CreateSessionRequest request)
        {
            var mediaKind = (MediaKind)Enum.Parse(typeof(MediaKind), request.MediaKind ?? "Video");
            var visibility = (SessionVisibility)Enum.Parse(typeof(SessionVisibility), request.Visibility ?? "Public");

            var session = Manager.CreateSession(
                CurrentUserId, request.DeviceId, request.EmbySessionId,
                mediaKind, request.ItemId, request.PlayQueue, visibility);

            return ToDto(session);
        }

        public object Post(JoinSessionRequest request)
        {
            var sessionId = Guid.Parse(request.Id);
            var mode = (ParticipantMode)Enum.Parse(typeof(ParticipantMode), request.Mode ?? "Watching");

            Manager.Join(sessionId, CurrentUserId, request.DeviceId, request.EmbySessionId, mode);

            return ToDto(Manager.GetSession(sessionId));
        }

        public void Post(LeaveSessionRequest request)
        {
            Manager.Leave(Guid.Parse(request.Id), CurrentUserId, request.DeviceId);
        }

        public void Post(SendPlaystateRequest request)
        {
            var state = (PlaybackState)Enum.Parse(typeof(PlaybackState), request.State);
            Manager.ApplyHostPlaystate(Guid.Parse(request.Id), state, request.PositionTicks);
        }

        public void Post(ReportProgressRequest request)
        {
            Manager.ReportParticipantProgress(
                Guid.Parse(request.Id), CurrentUserId, request.DeviceId,
                request.PositionTicks, request.IsBuffering);
        }

        public void Post(InviteToSessionRequest request)
        {
            var session = Manager.GetSession(Guid.Parse(request.Id));
            if (session == null) return;

            foreach (var userId in request.UserIds ?? Enumerable.Empty<string>())
            {
                if (!session.InvitedUserIds.Contains(userId))
                {
                    session.InvitedUserIds.Add(userId);
                }
            }

            EmbySyncPlayEntryPoint.Broadcaster.Publish("party.invited", new
            {
                sessionId = session.Id,
                invitedUserIds = request.UserIds,
                hostUserId = session.HostUserId,
                itemId = session.ItemId
            });
        }

        // -----------------------------------------------------------------
        // Chat — ARCHITECTURE.md 6. pont
        // -----------------------------------------------------------------

        public object Post(SendChatMessageRequest request)
        {
            // A megjelenítendő nevet (monogramhoz) a SyncSessionManager oldja fel
            // IUserManager-en keresztül a userId alapján.
            var message = Manager.AddChatMessage(Guid.Parse(request.Id), CurrentUserId, request.Text);
            return ToDto(message);
        }

        public object Get(GetChatHistoryRequest request)
        {
            var session = Manager.GetSession(Guid.Parse(request.Id));
            return session?.ChatLog.Select(ToDto).ToList() ?? new System.Collections.Generic.List<ChatMessageDto>();
        }

        // -----------------------------------------------------------------
        // Eszköz-elnevezés — ARCHITECTURE.md 3.2
        // -----------------------------------------------------------------

        public object Get(GetMyDevicesRequest request)
        {
            var profile = Manager.GetOrCreateDeviceProfile(CurrentUserId);
            return profile.Devices.Select(d => new DeviceLabelDto
            {
                EmbyDeviceId = d.EmbyDeviceId,
                Label = d.Label
            }).ToList();
        }

        public void Post(SetDeviceLabelRequest request)
        {
            Manager.SetDeviceLabel(CurrentUserId, request.EmbyDeviceId, request.Label);
        }

        // -----------------------------------------------------------------
        // DTO leképezés
        // -----------------------------------------------------------------

        private SyncSessionDto ToDto(SyncSession session)
        {
            return new SyncSessionDto
            {
                Id = session.Id.ToString(),
                HostUserId = session.HostUserId,
                MediaKind = session.MediaKind.ToString(),
                ItemId = session.ItemId,
                PlayQueue = session.PlayQueue,
                Visibility = session.Visibility.ToString(),
                State = session.State.ToString(),
                PositionTicks = session.PositionTicks,
                Participants = session.Participants.Select(p => new ParticipantDto
                {
                    UserId = p.UserId,
                    DeviceLabel = ResolveDeviceLabel(p.UserId, p.DeviceId),
                    Role = p.Role.ToString(),
                    Mode = p.Mode.ToString()
                }).ToList()
            };
        }

        private string ResolveDeviceLabel(string userId, string embyDeviceId)
        {
            var profile = Manager.GetOrCreateDeviceProfile(userId);
            var label = profile.Devices.FirstOrDefault(d => d.EmbyDeviceId == embyDeviceId)?.Label;
            return string.IsNullOrEmpty(label) ? embyDeviceId : label;
        }

        private static ChatMessageDto ToDto(ChatMessage message)
        {
            return new ChatMessageDto
            {
                UserId = message.UserId,
                Monogram = message.Monogram,
                Text = message.Text,
                TimestampUtc = message.TimestampUtc.ToString("o")
            };
        }
    }
}
