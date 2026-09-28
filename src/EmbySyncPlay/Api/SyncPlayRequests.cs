using System.Collections.Generic;
using MediaBrowser.Model.Services;

namespace EmbySyncPlay.Api
{
    // ARCHITECTURE.md 2.4 — REST DTO-k a ServiceStack routing konvenció szerint.
    // Hitelesítés a meglévő X-Emby-Token / Authorization fejléccel történik, amit az Emby
    // middleware-je automatikusan feloldja a hívó UserId-jára (IRequest.RequestContext).

    [Route("/EmbySyncPlay/Sessions", "GET")]
    public class GetSessionsRequest : IReturn<List<SyncSessionDto>>
    {
    }

    [Route("/EmbySyncPlay/Sessions", "POST")]
    public class CreateSessionRequest : IReturn<SyncSessionDto>
    {
        public string ItemId { get; set; }
        public List<string> PlayQueue { get; set; }
        public string MediaKind { get; set; } // "Video" | "Audio"
        public string Visibility { get; set; } // "Public" | "InviteOnly"
        public string DeviceId { get; set; }
        public string EmbySessionId { get; set; }
    }

    [Route("/EmbySyncPlay/Sessions/{Id}/Join", "POST")]
    public class JoinSessionRequest : IReturn<SyncSessionDto>
    {
        public string Id { get; set; }
        public string DeviceId { get; set; }
        public string EmbySessionId { get; set; }
        public string Mode { get; set; } // "Watching" | "ChatOnly"
    }

    [Route("/EmbySyncPlay/Sessions/{Id}/Leave", "POST")]
    public class LeaveSessionRequest : IReturnVoid
    {
        public string Id { get; set; }
        public string DeviceId { get; set; }
    }

    [Route("/EmbySyncPlay/Sessions/{Id}/Playstate", "POST")]
    public class SendPlaystateRequest : IReturnVoid
    {
        public string Id { get; set; }
        public string State { get; set; } // "Playing" | "Paused"
        public long PositionTicks { get; set; }
    }

    [Route("/EmbySyncPlay/Sessions/{Id}/Progress", "POST")]
    public class ReportProgressRequest : IReturnVoid
    {
        public string Id { get; set; }
        public string DeviceId { get; set; }
        public long PositionTicks { get; set; }
        public bool IsBuffering { get; set; }
    }

    [Route("/EmbySyncPlay/Sessions/{Id}/Invite", "POST")]
    public class InviteToSessionRequest : IReturnVoid
    {
        public string Id { get; set; }
        public List<string> UserIds { get; set; }
    }

    [Route("/EmbySyncPlay/Sessions/{Id}/Chat", "POST")]
    public class SendChatMessageRequest : IReturn<ChatMessageDto>
    {
        public string Id { get; set; }
        public string Text { get; set; }
    }

    [Route("/EmbySyncPlay/Sessions/{Id}/Chat", "GET")]
    public class GetChatHistoryRequest : IReturn<List<ChatMessageDto>>
    {
        public string Id { get; set; }
    }

    [Route("/EmbySyncPlay/Devices", "GET")]
    public class GetMyDevicesRequest : IReturn<List<DeviceLabelDto>>
    {
    }

    [Route("/EmbySyncPlay/Devices/{EmbyDeviceId}", "POST")]
    public class SetDeviceLabelRequest : IReturnVoid
    {
        public string EmbyDeviceId { get; set; }
        public string Label { get; set; }
    }

    [Route("/EmbySyncPlay/Stream", "GET")]
    public class SubscribeStreamRequest
    {
    }

    // ---------------------------------------------------------------
    // DTO-k — nem a belső Models/ típusokat adjuk vissza közvetlenül,
    // hogy a szerializált API-forma stabil maradjon a belső modelltől függetlenül.
    // ---------------------------------------------------------------

    public class SyncSessionDto
    {
        public string Id { get; set; }
        public string HostUserId { get; set; }
        // Ember számára olvasható mezők — a nyers UserId/ItemId önmagában nem értelmezhető
        // (felhasználó explicit visszajelzése, 2026-09-28: "ebből semmi értelmes nem derül
        // ki egy ember számára").
        public string HostDisplayName { get; set; }
        public string MediaKind { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public List<string> PlayQueue { get; set; }
        public string Visibility { get; set; }
        public string State { get; set; }
        public long PositionTicks { get; set; }
        public List<ParticipantDto> Participants { get; set; }
    }

    public class ParticipantDto
    {
        public string UserId { get; set; }
        public string DisplayName { get; set; }
        public string DeviceLabel { get; set; }
        public string Role { get; set; }
        public string Mode { get; set; }
    }

    public class ChatMessageDto
    {
        public string UserId { get; set; }
        public string Monogram { get; set; }
        public string Text { get; set; }
        public string TimestampUtc { get; set; }
    }

    public class DeviceLabelDto
    {
        public string EmbyDeviceId { get; set; }
        public string Label { get; set; }
    }
}
