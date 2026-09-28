using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace EmbySyncPlay.Core
{
    /// <summary>
    /// Egyszerű, felhasználónkénti Server-Sent Events szórásmotor. ARCHITECTURE.md 2.5 —
    /// a Media-Server-SSE (Tracearr) mintát követi: az EmbySyncPlayEntryPoint idekötözi a
    /// belső SyncSessionManager eseményeket, a Web kliens pedig a
    /// GET /EmbySyncPlay/Stream végponton keresztül iratkozik fel rájuk.
    ///
    /// MEGJEGYZÉS: a tényleges HTTP streaming megvalósítás (chunked response, hosszú-élettartamú
    /// connection) SDK-verziónként eltérő API-t használhat (pl. IAsyncStreamWriter) — ez az
    /// osztály a szállítási rétegtől független, pusztán az esemény-puffereket kezeli soronként,
    /// amiket az Api/SyncPlayStreamService ír ki ténylegesen a HTTP response-ba.
    /// </summary>
    public class SseBroadcaster
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _subscriberQueues =
            new ConcurrentDictionary<string, ConcurrentQueue<string>>();

        private const int MaxQueuedEventsPerSubscriber = 500;

        public string Subscribe()
        {
            var subscriberId = Guid.NewGuid().ToString("N");
            _subscriberQueues[subscriberId] = new ConcurrentQueue<string>();
            return subscriberId;
        }

        public void Unsubscribe(string subscriberId)
        {
            _subscriberQueues.TryRemove(subscriberId, out _);
        }

        public bool TryDequeue(string subscriberId, out string payload)
        {
            payload = null;
            return _subscriberQueues.TryGetValue(subscriberId, out var queue) && queue.TryDequeue(out payload);
        }

        /// <summary>Minden feliratkozónak elküldi az eseményt. A szűrés (pl. csak a meghívott
        /// userId-knak) a hívó (EntryPoint) felelőssége — itt lehetne per-user queue-t is
        /// bevezetni, ha a broadcast-only modell nem elég finomszemcsés.</summary>
        public void Publish(string eventName, object data)
        {
            var json = JsonConvert.SerializeObject(data);
            var line = $"event: {eventName}\ndata: {json}\n\n";

            foreach (var queue in _subscriberQueues.Values)
            {
                queue.Enqueue(line);
                // Lassú/elakadt kliens ne halmozzon fel korlátlanul memóriát: a legrégebbit eldobjuk.
                while (queue.Count > MaxQueuedEventsPerSubscriber && queue.TryDequeue(out _)) { }
            }
        }
    }
}
