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

        // ÉLŐ NAS-teszten (2026-09-28) kiderült: az Emby a plugin-oldalakat
        // "Cache-Control: public" fejléccel és egy ETag-gel szolgálja ki, de az ETag NEM a
        // konkrét embedded-resource tartalmától függ (két különböző tartalmú oldalunk,
        // "syncplay" és "syncplayjs", pontosan UGYANAZT az ETag-et kapta), és a böngésző által
        // kért URL-hez fűzött "?v=<szám>" is csak a SZERVER verziója (nem a plugin-é) — tehát
        // amíg a szerver verziója nem változik, a plugin URL-je is fix marad. Ennek
        // eredményeként egy már meglátogatott oldalt a böngésző a plugin-tartalom
        // frissítése UTÁN is a régi, cache-elt verzióból szolgálhat ki — ez okozta, hogy egy
        // már javított funkció (pl. a context-menü "Party indítása" bejegyzése) egy valódi,
        // korábban már használt böngészőben nem jelent meg, miközben egy friss profilú
        // automatizált teszt (nulla cache) helyesen működött. NEM a felhasználó böngésző-
        // cache-ét kell ürítenie — ehelyett MINDEN olyan release-nél, ami syncplay.html vagy
        // syncplay.js tartalmát módosítja, a PAGE VERSION SUFFIX-et (lásd lent) kötelezően
        // bumpolni kell, hogy garantáltan ÚJ URL-t kérjen a kliens (ez sosem cache-elhető
        // véletlenül, mert korábban sosem létezett).
        private const string PageVersionSuffix = "_v3";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = "syncplay" + PageVersionSuffix,
                DisplayName = "EmbySyncPlay",
                EmbeddedResourcePath = GetType().Namespace + ".Web.syncplay.html",
                EnableInMainMenu = true,
                EnableInUserMenu = true,
                MenuIcon = "people"
            };

            // A syncplay.html gyökér eleme data-controller="__plugin/syncplayjs<suffix>"-t
            // hivatkozik (syncplay.html-ben és syncplay.js buildInviteLink()-jében is
            // szinkronban kell tartani ugyanezt a suffixet).
            yield return new PluginPageInfo
            {
                Name = "syncplayjs" + PageVersionSuffix,
                EmbeddedResourcePath = GetType().Namespace + ".Web.syncplay.js"
            };
        }
    }
}
