using System;
using System.Collections.Generic;
using EmbySyncPlay.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace EmbySyncPlay
{
    /// <summary>
    /// A tényleges Emby SDK-ban (mediabrowser.server.core 4.8.0.80) a felhasználói-menübe
    /// tehető, saját HTML/JS oldal regisztrációja NEM az IPluginConfigurationPage-en (az csak
    /// az admin dashboard oldalakra való, EnableInMainMenu nélkül), hanem az
    /// IHasWebPages.GetPages()-en keresztül, PluginPageInfo bejegyzésekkel történik.
    /// ARCHITECTURE.md 2.8.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "EmbySyncPlay";

        public override string Description =>
            "Közös filmnézés, sorozatnézés és zenehallgatás több kliens között, közös chattel.";

        // Egyedi, végleges GUID — a plugin teljes életciklusa alatt nem változhat.
        public override Guid Id => Guid.Parse("6E6D8C2A-6F3B-4E7D-9C1A-3B2D5A9F7C10");

        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = "syncplay",
                DisplayName = "EmbySyncPlay",
                EmbeddedResourcePath = GetType().Namespace + ".Web.syncplay.html",
                EnableInMainMenu = true,
                EnableInUserMenu = true,
                MenuIcon = "people"
            };

            // A syncplay.html gyökér eleme data-controller="__plugin/syncplayjs"-t hivatkozik.
            // ÉLŐ NAS-teszten (2026-09-28) a szerver saját HTTP-logja megerősítette, hogy a
            // valódi böngésző-kérés "?name=syncplayjs&v=<szerververzió>" formában érkezik —
            // NINCS ".js" a végén, a kliens csak egy cache-busting verziószámot told hozzá.
            // (Egy korábbi, téves diagnózis egy saját curl teszt logsorát nézte valódi
            // böngésző-kérésnek — az tévesen ".js"-re következtetett.)
            yield return new PluginPageInfo
            {
                Name = "syncplayjs",
                EmbeddedResourcePath = GetType().Namespace + ".Web.syncplay.js"
            };
        }
    }
}
