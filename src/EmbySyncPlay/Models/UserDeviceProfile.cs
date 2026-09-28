using System.Collections.Generic;

namespace EmbySyncPlay.Models
{
    /// <summary>ARCHITECTURE.md 3.2 — a felhasználó saját eszközeinek elnevezése, hogy a
    /// résztvevőlista "Felhasználó (Eszköznév)" formában jelenjen meg session-ID-k helyett.</summary>
    public class UserDeviceProfile
    {
        public string UserId { get; set; }

        public List<DeviceLabel> Devices { get; set; } = new List<DeviceLabel>();
    }

    public class DeviceLabel
    {
        /// <summary>Az Emby natívan is elküldi minden kliens-kapcsolatban (Session.DeviceId).</summary>
        public string EmbyDeviceId { get; set; }

        /// <summary>Felhasználó által adott név, pl. "Nappali TV", "Telefonom".</summary>
        public string Label { get; set; }
    }
}
