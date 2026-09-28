# EmbySyncPlay — Architektúra Terv

A projekt neve **EmbySyncPlay** — ezen a néven hivatkozunk rá a dokumentációban, a route-
prefixekben (`/EmbySyncPlay/...`) és a szerveroldali entry point osztályban
(`EmbySyncPlayEntryPoint`). A belső modell-típusok (`SyncSession`, `SyncSessionManager`,
`ChatMessage`) rövidebb, tisztán technikai nevek, ezek nem a projekt márkanevét viselik.

## 0. Alapvető architekturális korlát — ezt kell először tisztázni

Az Emby plugin-modell **szigorúan szerveroldali**: a kiegészítő kódja kizárólag az Emby
Serveren fut, semmilyen natív klienst (Android TV, Roku, iOS/Android app) **nem** lehet
plugin-kóddal kiterjeszteni.

Ebből nem egy kettévágott (Web-teljes / többi-semmi) modellt építünk, hanem egy
**fokozatos leépülési (graceful degradation) modellt**: minden kliens annyit kap, amennyit
a saját képessége enged. Három szint:

| Szint | Kliens | Mit kap |
|---|---|---|
| **Tier 0 — csak vezérlés** | bármely hivatalos Emby kliens (TV app, Roku, mobil, stb.), amely a beépített remote-control protokollt támogatja | Start/Stop/Pause/Seek szinkron. Ez már ma is minden hivatalos kliensen működik, mert az Emby beépített "Cast to device"/"Play On" funkciója pontosan ezt a csatornát használja (`ISessionManager.SendPlaystateCommand` / `SendPlayCommand`, WebSocket-en). Nem kell semmit a kliensbe injektálni. |
| **Tier 1 — vezérlés + push értesítés** | ugyanaz, kiegészítve az Emby beépített Notifications-csatornájával (harang ikon) vagy `SendMessageCommand` toast üzenettel | Fentiek + "X elkezdett közösen nézni: Y" típusú push, ha a kliens ezt megjeleníti |
| **Tier 2 — teljes** | **Emby Web kliens** | Minden a fentiekből, plusz saját UI: party-böngészés, csatlakozás, chat, chat-overlay a videón — mert csak ide tud a plugin saját felületet regisztrálni (`IPluginConfigurationPage`, `EnableInMainMenu`) |

A szerver minden kliens felé ugyanazt a parancskészletet küldi; egy kliens egyszerűen
figyelmen kívül hagyja, amit nem ért (pl. egy TV app nem jelenít meg chat-üzenetet, de a
Seek parancsot végrehajtja). Nincs kliens-specifikus ági logika a szerverben — a
"képesség szerinti leépülés" a kliens oldalán történik magától, mert azok csak azt az
üzenetformátumot dolgozzák fel, amit ismernek.

**Ez pontosan lefedi a konkrét forgatókönyvet is:** valaki a TV-n nézi (Tier 0/1 — csak
lejátszás-szinkron), közben a telefonján chatel (a telefon ilyenkor a Web kliens
böngészőjén keresztül csatlakozik a partyhoz **chat-only** módban, lásd 3. pont), míg egy
harmadik résztvevő notebookon egyszerre nézi és chatel is (Tier 2, egy eszközön mindkettő).

---

## 1. Fogalmi modell

```
SyncSession ("Party")
 ├─ Id (Guid)
 ├─ HostUserId
 ├─ MediaKind: Video | Audio                 ← film/sorozat VAGY zene, ugyanaz a motor
 ├─ ItemId (a lejátszott film/epizód/zeneszám Emby ItemId-je)
 │    — Audio esetén PlayQueue: List<ItemId> is (lejátszási lista, nem csak 1 track)
 ├─ Visibility: Public (kiajánlott) | InviteOnly
 ├─ State: Playing | Paused
 ├─ PositionTicks (a party "hivatalos" lejátszási pozíciója)
 ├─ CurrentQueueIndex (Audio esetén: melyik track fut épp a PlayQueue-ból)
 ├─ LastUpdatedUtc
 ├─ Participants: List<Participant>
 └─ ChatLog: List<ChatMessage>

Participant
 ├─ UserId
 ├─ DeviceId (lásd 3. pont — a user saját elnevezett eszköze, pl. "Péter tévéje")
 ├─ EmbySessionId (Emby ISessionManager session, azaz maga a kliens-kapcsolat)
 ├─ Role: Host | Guest
 ├─ Mode: Watching | ChatOnly                ← lásd 3. pont, ugyanaz a user egyszerre
 │                                              nézhet egy eszközön és chatelhet másikon
 ├─ PlaybackMethod (DirectPlay | Transcode | DirectStream) — csak infó, a szerver
 │    ezt egyébként is a kliens képességei alapján dönti el minden résztvevőnél külön
 └─ JoinedAtUtc

ChatMessage
 ├─ SyncSessionId
 ├─ UserId, Monogram (lásd 3. pont — rövid megjelenítés: "P: Hello")
 ├─ Text
 └─ TimestampUtc
```

Kulcsfontosságú elv, amit a követelmény is kimond: **nem a média-adatfolyam szinkronizálódik**,
hanem kizárólag a **PlaybackState + időbélyeg**. A tényleges videó/audio stream-et minden
kliens a saját hálózati/eszköz-képességei szerint kapja az Emby normál lejátszási
logikájából (Direct Play / Direct Stream / Transcode) — ebbe a plugin nem nyúl bele.

A `MediaKind` megkülönböztetés miatt a zenei party ugyanazt az állapotgépet és protokollt
használja, mint a videós — csak a `PlayQueue` lista teszi lehetővé, hogy a host továbbléphet
a következő számra, és ez is propagálódik a többiekhez (`NextTrack` parancs, ami technikailag
egy `PlayCommand` az új `ItemId`-re, `PositionTicks = 0`-val).

---

## 2. Fő komponensek (szerveroldal)

### 2.1. `Plugin` + `PluginConfiguration`
`BasePlugin<PluginConfiguration>`. Beállítások: max party méret, chat engedélyezése,
alapértelmezett látógatóhatár (drift tolerancia, ld. 4. pont), automatikus party-takarítás
időzítése.

### 2.2. `SyncSessionManager` (singleton szolgáltatás, DI-be regisztrálva)
Ez a rendszer agya. Az aktív `SyncSession`-öket memóriában tartja, de **perzisztál is**:
minden állapotváltozáskor (és emellett periodikusan, pl. 30 másodpercenként) egy JSON
pillanatképet ír a plugin adatkönyvtárába (`IApplicationPaths.PluginConfigurationsPath`
melletti saját mappa). A `SyncPlayEntryPoint.Run()` szerverindításkor beolvassa ezt a
fájlt, és megpróbálja visszaállítani az aktív party-kat: azokat a résztvevőket, akiknek
időközben megszűnt az Emby session-je, eltávolítja, a többinek pedig a mentett
`PositionTicks`-hez képest becsült (eltelt idővel korrigált) pozícióra küld egy `Seek`-et.
Ha a visszaállítás bármilyen okból nem sikerül (pl. az `ItemId` már nem létezik a
könyvtárban), a party egyszerűen nem áll vissza — ez elfogadható bukási mód, nem kritikus
funkció, csak kényelmi.

Felelősségei:
- Party létrehozása/megszüntetése
- Csatlakozás/kilépés kezelése
- Play/Pause/Seek propagálása a résztvevők felé
- Drift-ellenőrzés és korrekció
- Eseménykibocsátás a `IServerEntryPoint`-nak (pl. WebSocket push a Web UI-nak)

### 2.3. `EmbySyncPlayEntryPoint : IServerEntryPoint`
Feliratkozik a natív Emby lejátszási eseményekre:
- `ISessionManager.PlaybackProgress` — a host kliens progress-jelentéseit figyeli
- `ISessionManager.PlaybackStopped` / `SessionEnded` — host lecsatlakozás kezelése
- `ISessionManager.SessionStarted` — új session regisztráció

Itt történik a **kimenő** parancsok küldése is:
```csharp
await _sessionManager.SendPlaystateCommand(
    controllingSessionId: null,           // szerver-iniciált
    sessionId: participant.EmbySessionId,
    new PlaystateRequest {
        Command = PlaystateCommand.Seek,
        PositionTicks = session.PositionTicks
    },
    cancellationToken);
```

### 2.4. `IService` végpontok (REST API, ServiceStack routing)

| Route | Metódus | Funkció |
|---|---|---|
| `/EmbySyncPlay/Sessions` | GET | Publikus (kiajánlott) party-k listája |
| `/EmbySyncPlay/Sessions` | POST | Új party indítása egy adott ItemId-re |
| `/EmbySyncPlay/Sessions/{Id}/Join` | POST | Csatlakozás |
| `/EmbySyncPlay/Sessions/{Id}/Leave` | POST | Kilépés |
| `/EmbySyncPlay/Sessions/{Id}/Playstate` | POST | Host küld Play/Pause/Seek-et |
| `/EmbySyncPlay/Sessions/{Id}/Chat` | POST | Chat üzenet küldése |
| `/EmbySyncPlay/Sessions/{Id}/Chat` | GET | Chat előzmény (long-poll vagy kezdeti betöltés) |
| `/EmbySyncPlay/Sessions/{Id}/Invite` | POST | Kijelölt felhasználók meghívása (nem publikus party) |

Hitelesítés a meglévő `X-Emby-Token` / `Authorization: MediaBrowser Token="..."` fejléccel,
ahogy minden más Emby API hívás.

### 2.5. Valós idejű push a Web kliens felé — SSE
A `Media-Server-SSE` (Tracearr) minta pontosan ezt a problémát oldja meg: egy
`IServerEntryPoint`-ban feliratkozunk a belső eseményekre, és **Server-Sent Events**
adatfolyamon toljuk ki a Web UI-nak: `party.invited`, `party.playstate_changed`,
`party.chat_message`, `party.participant_joined`. Ez váltja ki a HTTP pollingot a
chat/state frissítésekhez, és ez frissíti a UI-t "élőben" a meghívott felhasználóknál
(az eredeti követelmény: "ilyenkor értesítést kap, megváltozik az ui a többi kliensnél").

### 2.6. `IScheduledTask` — `SyncSessionCleanupTask`
Periodikusan (pl. 2 percenként) eltávolítja azokat a party-kat, amelyek host-ja lecsatlakozott
és nem lett új host kijelölve, vagy amelyek X perce inaktívak.

### 2.7. Declarative UI konfigurációs oldal
A plugin admin beállításai (max party méret, chat ki/be) a szabvány Declarative UI modellel
(`BasePluginConfiguration` + attribútumok), ahogy a PDF 5.2 fejezete leírja — **nem** kézzel
írt HTML/JS.

### 2.8. Web kliens felületi kiegészítés (`IPluginConfigurationPage`, `EnableInMainMenu: true`)
Ez a tényleges "közös mozizás" felhasználói felület: aktív party-k listája, "csatlakozom"
gomb, chat panel, "party indítása" gomb a lejátszó mellett. Ez egy önálló HTML/JS oldal, amit
a plugin publikál a szerver dashboard-jába — ezen a ponton **muszáj** hagyományos HTML/JS-t
írni, mert a Declarative UI csak konfigurációs form-okra való, nem interaktív alkalmazásra.
Ez a régi, "elavult" mintát követi (PDF 5.1), de nincs más választás egyedi, dinamikus UI-hoz;
a kockázatot (Emby UI-frissítés törheti) az minimalizálja, hogy ez egy önálló oldal, nem a
core dashboard elemeinek felülírása.

**Fontos technikai részlet a chat-overlay (6.1) beillesztéséhez:** az Emby Web kliens egy
SPA, a DOM folyamatosan újraépül navigáció közben, a videólejátszó elem statikus
oldalbetöltéskor még nem létezik. Az overlay JS-nek ezért **nem szabad** azonnal, oldal-
betöltéskor megpróbálnia beakasztani magát a lejátszóra — helyette egy `MutationObserver`-
rel kell figyelnie a DOM-ot, és csak akkor renderelni ki az overlay-t, amikor a
videólejátszó konténer elem ténylegesen megjelenik.

---

## 3. Azonosítás és eszközök

### 3.1. Azonosítás — a lehető leglazább
Nincs külön regisztráció, nincs saját fiókrendszer. **Aki be tud jelentkezni az Emby
Serverre, az már azonosítva van** — a plugin egyszerűen az Emby `UserId`-ra épít, amit
minden API hívás a meglévő `X-Emby-Token` fejlécből kap meg. Nincs extra jelszó, nincs
külön "SyncPlay fiók".

### 3.2. Eszköz-elnevezés felhasználónként
A cél, hogy ne ismeretlen "Session #a1b2c3" azonosítók jelenjenek meg, hanem emberileg
felismerhető címkék. Ehhez minden felhasználó a saját fiókja alatt elnevezheti a saját
eszközeit:

```
UserDeviceProfile (per-user, perzisztens: plugin data store)
 ├─ UserId
 └─ Devices: List<DeviceLabel>
      ├─ EmbyDeviceId (az Emby natívan is küldi minden kliens-kapcsolatban)
      └─ Label (felhasználó által adott név, pl. "Nappali TV", "Telefonom", "Notebook")
```

Amikor egy új, még nem elnevezett `EmbyDeviceId` jelenik meg egy felhasználónál, a Web UI
egyszeri névadást kér ("Hogy hívjuk ezt az eszközt?"), utána megjegyzi. Ez az adat
kizárólag az adott user saját eszközeit listázza neki — mások eszközeit nem látja, csak
azt, hogy *"Péter (Nappali TV) csatlakozott"* a party-ban, ha Péter így nevezte el a saját
tévéjét.

Ez oldja meg a "sok szereplő / ismeretlen kliens" problémát: egy party résztvevőlistája
nem session-ID-kat mutat, hanem **"Felhasználó (Eszköznév)"** formátumot, jelentősen
csökkentve a kognitív terhet, amikor valaki egyszerre több eszközzel van jelen (lásd 3.3).

### 3.3. Egy felhasználó, több eszköz, eltérő szerepben
A követelmény explicit forgatókönyve: valaki a TV-n nézi a filmet, közben a telefonján
chatel. Ez azt jelenti, hogy **egy `UserId` egyszerre több `Participant` bejegyzést is
kaphat ugyanabban a `SyncSession`-ben**, eltérő `DeviceId` és `Mode` értékkel:

- `Participant(UserId=Péter, Device=Nappali TV, Mode=Watching)` — ezt vezérli a szerver
  Play/Pause/Seek paranccsal
- `Participant(UserId=Péter, Device=Telefonom, Mode=ChatOnly)` — ez csak a Web kliens
  böngészőjén (mobil nézetben) csatlakozik a chatre, lejátszás-parancsot nem kap, mert
  nincs is neki lejátszási munkamenete a partyban

A `ChatOnly` mód csatlakozásakor a szerver **nem** indít neki lejátszási session-t —
egyszerűen csak feliratkozik a party SSE/chat csatornájára. Ez teszi lehetővé, hogy valaki
kizárólag a telefonjáról kövesse/csinálja a chatet, miközben a tényleges nézés egy másik
eszközén (vagy akár más felhasználóknál) zajlik.

MVP-ben nincs szerveroldali létszám-korlát arra, hány `Participant` bejegyzése lehet egy
felhasználónak egy party-n belül — ez ritkán fordul elő valós használatban, és a korlátozás
felesleges bonyolítás lenne. A résztvevőlista viszont a **Web UI-n userenként csoportosítva**
jelenik meg, nem session-enként felsorolva, hogy a lista hossza a hús-vér emberek számával
egyezzen meg, ne a session-ök számával:

```
👤 Péter — Nézi: Nappali TV · Chat: Telefonom
👤 Anna  — Nézi és chatel: Web kliens
```

### 3.4. Monogram a chatben
A chat üzenetek listájában nem a teljes felhasználónév, hanem annak **monogramja**
(kezdőbetűje, nagybetűvel) jelenik meg a szöveg előtt, hogy a lista rövid és tömör
maradjon: `Péter` → `P: Hello`. Ha két résztvevő monogramja ütközik egy party-n belül
(pl. Péter és Petra), a szerver a monogramhoz egy második karaktert told hozzá csak azon
a party-n belül a megkülönböztetéshez (`Pé:` / `Pe:`), hogy a rövidség megmaradjon,
ütközés nélkül. A teljes név a monogramra mutatva (tooltip) vagy a résztvevő-listában
továbbra is elérhető, csak a chat-sorokban tömörödik monogrammá.

---

## 4. Szinkronizációs protokoll

**Szerver-tekintélyű (server-authoritative), host-vezérelt modell:**

1. A host kliens küldi a Play/Pause/Seek eseményt a saját normál Emby lejátszási
   folyamatában (ezt az Emby amúgy is jelenti a szervernek `ReportPlaybackProgress` /
   `ReportPlaybackStart` hívásokkal).
2. A `SyncSessionEntryPoint` elkapja ezt az eseményt **ha** a küldő session host egy aktív
   party-ban, és frissíti a `SyncSession.PositionTicks` / `State` mezőt.
3. A `SyncSessionManager` minden **más** résztvevő session felé kiadja ugyanazt a parancsot
   a beépített `SendPlaystateCommand` / `SendPlayCommand` API-n.
4. **Drift-korrekció:** minden résztvevő kliens periodikusan (pl. 10 másodpercenként)
   jelenti a saját pozícióját (ez már eleve megy `ReportPlaybackProgress`-en). Ha egy
   guest pozíciója > N másodperc eltérést mutat a party pozíciójától (tolerancia,
   konfigurálható, alapérték ~3s), a szerver egy célzott `Seek` parancsot küld csak annak a
   kliensnek — **nem** mindenkinek, elkerülve a felesleges buffer-megszakításokat azoknál,
   akik amúgy is szinkronban vannak.
5. **Miért nem kliens-kliens (peer) szinkron:** a követelmény explicit szerver-központú
   modellt ír elő ("a kliensek a serverrel egyeztetnek"), ez egyben leegyszerűsíti a NAT/
   tűzfal problémákat is, mivel minden kliens amúgy is a szerverhez csatlakozik globálisan.

### 4.1. Transzkódolási késleltetés — kritikus peremfeltétel
A `PositionTicks`-alapú Seek önmagában nem old meg egy aszimmetriát: ha a host Direct
Play-en van (indítás ~azonnali), egy guest viszont transzkódolásra szorul, a guestnél a
szervernek fel kell építenie az FFmpeg pipeline-t, ami több másodperces késést okozhat a
Play parancs kiküldése és a tényleges lejátszás-indulás között. Ha a szerver ez alatt az
idő alatt is folyamatosan `Seek`/`Play` parancsokkal bombázza a guestet (mert a
drift-ellenőrzés éppen nagy eltérést lát), az megszakíthatja a formálódó FFmpeg streamet,
és egy végtelen **seek-loop**-ba futhat (seek → buffer-megszakadás → újra nagy drift →
újabb seek). Ezt két szabály kezeli:
- Amíg egy kliens `Buffering`/`Paused-for-buffer` státuszt jelent, a drift-ellenőrzés
  **nem** küld neki újabb korrekciós parancsot — kivárja, amíg a kliens saját magától
  `Playing`-ra vált.
- Minden kiküldött `Seek` után az adott résztvevőnél egy rövid **cooldown** (pl. 5–8 mp)
  indul, ami alatt a drift-ellenőrzés nem értékeli ki újra a pozícióját — időt hagyva a
  buffernek stabilizálódni, mielőtt a szerver újra beavatkozna.

### 4.2. Miért működik ez eltérő stream-minőségek mellett is
A `PositionTicks` időbélyeg **tartalom-relatív** (a médiafájlon belüli pozíció), nem
hálózati csomag-pozíció. Így teljesen független attól, hogy az egyik kliens 4K Direct Play-t,
a másik 720p transzkódolt streamet kap — mindkettő ugyanahhoz a `PositionTicks` értékhez
tud seekelni a saját, egyébként is meglévő lejátszási munkamenetében. A szerver a
transzkódolást/Direct Play döntést a meglévő, e projekttől független logikájával hozza meg
minden kliensnél külön.

---

## 5. "Kiajánlás" / discovery / meghívás

- **Publikus party:** `Visibility = Public` esetén a party megjelenik a
  `GET /SyncPlay/Sessions` listában, amit a Web UI lekérdez, és SSE-n keresztül azonnal
  push-olja is minden bejelentkezett felhasználónak: *"Anna most közösen nézi: Dűne"*
  (opcionális toast/notification a natív kliensek beépített Emby-értesítés harangján
  keresztül is, az `INotificationService`-en át).
- **Meghívásos party:** csak a kijelölt `UserId`-k kapnak SSE eseményt / notification-t,
  a lista végponton nem jelenik meg másnak.
- **Csatlakozás:** a felhasználó a Web UI-n rákattint → a szerver hozzáadja
  `Participant`-ként, elindítja neki a saját lejátszási munkamenetét ugyanarra az `ItemId`-re,
  azonnal a party aktuális `PositionTicks` pozíciójára ugorva, majd host state szerint
  Play/Pause.

---

## 6. Chat

- Perzisztencia: a `SyncSession` részeként ugyanúgy JSON pillanatképbe kerül (2.2), így a
  chat-előzmény is túléli a szerver-újraindítást, amíg a party maga is aktív.
- Küldés: `POST /SyncPlay/Sessions/{Id}/Chat`, akár `Watching`, akár `ChatOnly` módú
  résztvevőtől (lásd 3.3).
- Fogadás: SSE stream (`party.chat_message` esemény) a Web UI-n; a natív TV/mobil kliensek
  a chatet nem jelenítik meg (Tier 0/1, lásd 0. pont) — ott a chateléshez a felhasználó a
  Web kliens böngészőjét nyitja meg egy másik eszközön `ChatOnly` módban.
- Megjelenítés: minden sor `Monogram: szöveg` formában jelenik meg (lásd 3.4), a résztvevő-
  listában viszont a teljes "Felhasználó (Eszköznév)" cím szerepel.

### 6.1. Chat-overlay a videón — kliens-oldali, eszközönkénti döntés
A Web kliens lejátszója felett a chat megjeleníthető lebegő overlay-panelként is (mint egy
stream-chat), a hagyományos oldalsó panel mellett/helyett. Ez **kizárólag a Web kliens
felületi beállítása**, tehát csak ott érhető el, ahol a plugin egyáltalán UI-t tud
megjeleníteni (lásd 0. pont, Tier 2) — natív TV/mobil app lejátszójára nem lehet ráúsztatni
semmit plugin-kódból.

A döntés **kliensenként/eszközönként független**, nem globális beállítás:
- localStorage-ban tárolt, eszközhöz kötött preferencia (`overlay: on/off`, pozíció,
  átlátszóság)
- ugyanaz a felhasználó az egyik eszközén bekapcsolva, a másikon kikapcsolva tarthatja
- több eszközön **egyszerre** is bekapcsolható (nincs "csak egy helyen aktív" korlátozás) —
  ha valaki két notebookon is a Web klienst nézi ugyanabban a partyban, mindkettőn külön-
  külön eldöntheti, kéri-e az overlay-t

---

## 7. Élet-ciklus / állapotgép

```
[Nincs party]
   │ POST /Sessions (host indít)
   ▼
[Playing/Paused, 1 résztvevő (host)]
   │ Join (guest)              │ host Leave / Stop
   ▼                           ▼
[Playing/Paused, N résztvevő]  [Party megszűnik]
   │ host Leave, van más guest
   ▼
[Host-átadás guestnek] ──► [Playing/Paused, N-1 résztvevő]
   │ (utolsó résztvevő is Leave, vagy CleanupTask időtúllépés)
   ▼
[Party megszűnik]
```

Host lecsatlakozás esetén a legrégebb óta csatlakozott guest válik automatikusan új
host-tá (hogy a party ne szakadjon meg feleslegesen).

---

## 8. Használt Emby interfészek — összefoglaló

| Interfész | Cél ebben a projektben |
|---|---|
| `ISessionManager` | Play/Pause/Seek parancsok kiküldése kliensekhez, progress-események figyelése |
| `IServerEntryPoint` | Life-cycle hook, eseményfeliratkozás, SSE stream indítása |
| `IUserManager` | Felhasználók feloldása meghívásnál/listázásnál |
| `IHttpClient` | (ha később külső webhook/integráció kell, pl. Discord értesítés) |
| `ILogManager` / `ILogger` | Naplózás |
| `IScheduledTask` | Lejárt party-k takarítása |
| `IService` (ServiceStack) | REST végpontok |
| `INotificationService` | Kiajánlás értesítés minden kliens beépített értesítési csatornáján |
| `IApplicationPaths` | Plugin adatkönyvtár elérése a party-perzisztencia JSON fájljaihoz |
| `BasePluginConfiguration` + Declarative UI | Admin beállítási oldal |

---

## 9. MVP hatókör vs. később

**MVP:**
- Party indítás/csatlakozás/kilépés, publikus + meghívásos láthatóság
- **Videó ÉS zene egyaránt** (`MediaKind`), zenénél `PlayQueue`/lejátszási lista is
- Play/Pause/Seek/NextTrack szinkron minden hivatalos kliensen (natív remote-control API-n),
  Tier szerinti fokozatos leépülés (lásd 0. pont)
- Drift-korrekció
- Azonosítás az Emby bejelentkezésre építve, felhasználónkénti eszköz-elnevezés (3. pont)
- `ChatOnly` csatlakozási mód — egy user egy másik eszközön csak chatel, míg egy másikon néz
- Web kliens felület: party lista, csatlakozás, chat monogrammal, opcionális chat-overlay
- SSE push a Web kliens felé
- **Party-perzisztencia** JSON pillanatképpel — a szerver-újraindítást túlélik a party-k,
  amennyire technikailag lehetséges (best-effort, nem kőbe vésett garancia, ld. 2.2)

**Később:**
- Natív kliens chat/toast integráció (jelenleg csak push-notification szintű elérés)
- Perzisztens party-**history** / statisztika (hosszú távú archívum, nem csak "túléli a
  restart-ot")
- "Kirúgás" / moderálás party-n belül
- Mobil app natív SyncPlay UI (ha ez már túlmutat a szerver-plugin hatókörön — külön
  kliens-oldali fejlesztést igényelne, amit az Emby zárt forráskódú hivatalos kliensei
  esetén nem lehet megvalósítani, csak saját, harmadik feles kliens esetén)

---

## 10. Eldöntött finomhangolási kérdések

Mindkét korábbi nyitott kérdés lezárva:

1. **Drift-tolerancia:** admin/szerver-szintű globális beállítás marad (nem
   felhasználónkénti) — a legtöbb felhasználó nem tudná értelmesen hangolni, ez a
   szervergazda dolga, kísérletezéssel kell megtalálni az optimális értéket (a 4.1 pontban
   leírt cooldown-mechanizmussal együtt finomhangolva).
2. **Chat-overlay:** szigorúan eszköz-szintű, felhasználói beállítás, a Web kliens
   `localStorage`-ában tárolva (ahogy a 6.1 pont már leírja) — az overlay panelen egy kis
   ki/be kapcsoló ikon jelenik meg ehhez.
3. **`ChatOnly` létszám-korlát:** nincs szerveroldali korlátozás MVP-ben (lásd 3.3 —
   userenkénti csoportosítással a UI-on ez amúgy sem okoz átláthatósági problémát).

Nincs jelenleg nyitott kérdés — a terv MVP-fejlesztésre kész.

---

## 11. Élő NAS-teszten (2026-09-28) megismert implementációs tanulságok

Az MVP-kódvázat éles Emby 4.10.0.40 szerveren (a felhasználó saját NAS-a) teszteltük
végig, valódi felhasználóval, két böngészővel (Chrome, Firefox) és két Emby klienssel.
Több olyan, sehol nem dokumentált SDK-viselkedés derült ki, amit érdemes megjegyezni a
további fejlesztéshez:

### 11.1. A menübe/user-menübe rakott saját oldal helyes regisztrációja
- **Nem** `IPluginConfigurationPage` (az csak admin dashboard oldalakra való,
  `EnableInMainMenu` nélkül) — helyette `Plugin : IHasWebPages`, `GetPages()`,
  `PluginPageInfo { EnableInMainMenu, EnableInUserMenu }`.
- A gyökér HTML elemen **kötelező** a `data-controller="__plugin/<jsPageName>"`
  attribútum — enélkül az Emby SPA router (`viewmanager.js`) "instance is undefined"
  hibával elszáll, mert nem talál kontrollert az oldalhoz.
- A `<jsPageName>` egy **külön regisztrált** `PluginPageInfo`-ra mutat, aminek tartalma
  **AMD-modul** (nem sima `<script src="...">` — az innerHTML-be injektált `<script>` tag
  nem futna le). A modul mintája: `define(['baseView'], function (BaseView) { function
  View(view, params) { BaseView.apply(this, arguments); ... } Object.assign(View.prototype,
  BaseView.prototype); View.prototype.onResume = function () {...}; View.prototype.onPause =
  function () {...}; return View; });` — ezt egy már működő gyári plugin
  (`Emby.Server.CinemaMode`) élő forrásából azonosítottuk be.
- A JS-oldal regisztrált `Name`-jének **pontosan** egyeznie kell azzal, amit a kliens
  ténylegesen lekér — ez a `data-controller` értékével egyezik (NEM kap `.js`
  kiterjesztést automatikusan, ellentétben egy korábbi téves feltételezésünkkel, amit
  saját `curl` teszt logsorával igazoltunk tévesen valódi böngésző-kérésnek).
- A form-elemek (`<select>`, `<input>`, `<button>`) az Emby saját `is="emby-select"` /
  `is="emby-input"` / `is="emby-button"` web-komponenseit igénylik — sima HTML elemként
  (pl. egy natív `<select>`) az Emby globális UI-kezelése azonnal bezárja/eltöri őket.
  Ehhez a gyökér elemen `data-require="emby-select,emby-input,emby-button,emby-checkbox"`
  kell.

### 11.2. JSON mezőnév-konvenció
A szerver saját REST API-ja (a mi `SyncPlayService`-ünk is) **PascalCase** JSON-t ad
vissza (`Id`, `ItemId`, `HostUserId`...), NEM camelCase-t — a Web kliens JS-nek ehhez kell
igazodnia (`session.Id`, nem `session.id`).

### 11.3. Item-azonosítók: nem mindig Guid
Ezen a szerververzión (4.10.0.40) a REST API az "Id" mezőt egyszerű **Int64 InternalId**-ként
adja vissza decimális szövegként (pl. `"597723"`), annak ellenére, hogy a `BaseItem.Id`
property maga `Guid` típusú. A szerveroldali feloldásnak (`ResolveInternalIds`)
**mindkét** formátumot kezelnie kell: előbb `long.TryParse`, csak utána esik vissza
`Guid.TryParse` + `ILibraryManager.GetItemById(Guid)`.

### 11.4. Async SDK-hívások — kötelező await, különben néma hibák
Az `ISessionManager.SendPlayCommand` / `SendPlaystateCommand` `Task`-ot ad vissza. Ha ezt
nem várjuk be (`await` nélkül hívjuk egy `void` metódusból), egy menet közbeni hiba
**csendben elvész** — a `try/catch` csak a Task létrehozásakor dobott kivételt fogja el,
nem a végrehajtás közbenit. Ez volt az egyik oka a megbízhatatlan szinkronizációnak.
Megoldás: `.GetAwaiter().GetResult()` a `try/catch`-en belül (szinkron blokkolás — helyi
WebSocket-küldésnél elfogadható), plusz rövid retry (3 kísérlet, 300 ms) a kritikus
(elem-váltó) parancsnál.

### 11.5. SSE stream — kötelező `SendChunked = true`
Enélkül a HTTP-válasz az **első** íráskor/flush-kor lezártnak számított, és a kapcsolat
pár másodpercen belül megszakadt — a kliens gyakorlatilag soha nem kapott folyamatos
push-ot. A `WriteToAsync(IResponse response, ...)` elején explicit be kell állítani:
`response.SendChunked = true;`. Emellett érdemes egy kezdeti `": connected\n\n"` komment-
sort és periodikus (`~15s`) `": ping\n\n"` keep-alive-ot küldeni, hogy köztes
proxy/HTTP-réteg idle-timeoutja ne szakítsa meg a kapcsolatot.

### 11.6. `EventSource` nem tud egyéni fejlécet küldeni
A natív böngésző `EventSource` API nem támogat egyéni HTTP fejlécet (`X-Emby-Token`),
ezért az SSE-kérés 401-et adott. Megoldás: a tokent **query paraméterként** kell
mellékelni (`ApiClient.getUrl(path, { 'X-Emby-Token': ApiClient.accessToken() })`) — ezt
az Emby szerver maga is elfogadja hitelesítésként (más natív végpontoknál is ezt a
mintát használja).

### 11.7. Host progress-jelentés: rutinszerű vs. valódi változás
Az Emby kliens kb. 10 másodpercenként küld progress-jelentést akkor is, ha semmi nem
történt. Ha erre minden alkalommal teljes Seek+Playstate parancsot küldtünk minden
résztvevőnek, az folyamatosan megszakította a lejátszásukat ("az első pár másodperc
ismétlődéseként" jelentkezett — seek-loop). Ha viszont csak Play↔Pause állapotváltásra
szűkítettük a push-ot, egy explicit **seek** (pl. 10 mp-es ugrás a gombbal, ami NEM jár
state-változással) egyáltalán nem terjedt a résztvevőkhöz.

A helyes megoldás **nem** az esemény típusát nézi (arra nincs megbízható SDK-jel kéznél),
hanem magát a **pozícióugrást detektálja**: minden progress-jelentésnél kiszámoljuk, mi
lenne a "várt" pozíció, ha egyszerűen csak telt az idő a legutóbbi ismert állapot óta
(`session.PositionTicks + elapsedSinceLastUpdate`), és ezt összevetjük a ténylegesen
jelentett pozícióval. Ha az eltérés meghalad egy toleranciát (~2 mp — a hálózati/jelentési
késleltetésnek), az csak explicit seekkel magyarázható → azonnali push mindenkinek. Ha az
eltérés a tolerancián belül van ÉS nem volt state-váltás, az rutinszerű jelentés → csak
csendes book-keeping frissítés, nincs push. Lásd `SyncSessionManager.ReportHostProgress`.

### 11.8. Epizódváltás követése — külön esemény kell
A `PlaybackProgress` esemény csak a MÁR futó elem pozícióváltozásait jelenti — amikor a
host új elemre vált (pl. a sorozat következő epizódja automatikusan elindul), az egy
`PlaybackStopped` (a régi elemre) + `PlaybackStart` (az újra) eseménypár. **Kritikus
buktató:** ha a `PlaybackStopped`-ot úgy kezeljük, hogy a hostot kiléptetjük a party-ból
(ahogy egy korábbi verzióban tettük), akkor MINDEN epizódváltás szétverte volna a
party-t, mielőtt a követés egyáltalán lefutott volna. Megoldás:
- A hostot **soha nem** léptetjük ki automatikusan `PlaybackStopped`-ra — csak a
  vendégeket (guest), ha ők állítják le a saját lejátszásukat.
- Külön feliratkozás `ISessionManager.PlaybackStart`-ra: ha a host EmbySessionId-jéhez
  tartozó új esemény ItemId-je eltér a session aktuális ItemId-jétől, meghívjuk
  `SyncSessionManager.ChangeHostItem`-et, ami frissíti a session ItemId-jét, nullázza a
  pozíciót, és PlayCommand-ot küld minden Watching résztvevőnek az új elemre.

### 11.9. Csatlakozáskor a host ÉLŐ pozíciója kell, nem a book-keeping érték
Ha egy résztvevő közvetlenül a party indítása után csatlakozik (mielőtt a host első
periodikus progress-jelentése megérkezne), a `session.PositionTicks` még a kezdeti
(gyakran `0`) értéken áll — az új résztvevő emiatt elölről kezdte volna a filmet. A
`SyncSessionManager.Join` ezért csatlakozáskor frissen lekérdezi a host **élő**
pozícióját `_embySessionManager.Sessions`-ből (`SessionInfo.PlayState.PositionTicks`),
nem vár a következő periodikus jelentésre.
