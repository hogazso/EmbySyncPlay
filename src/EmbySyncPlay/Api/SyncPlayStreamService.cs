using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbySyncPlay.EntryPoints;
using MediaBrowser.Model.Services;

namespace EmbySyncPlay.Api
{
    /// <summary>
    /// ARCHITECTURE.md 2.5 — a GET /EmbySyncPlay/Stream végpont, ami hosszú-élettartamú
    /// Server-Sent Events kapcsolatot tart a Web kliens felé (a Media-Server-SSE/Tracearr
    /// minta alapján), és a SseBroadcaster sorába került eseményeket írja ki rá.
    /// A mediabrowser.server.core 4.8.0.80-ban az IAsyncStreamWriter.WriteToAsync egy
    /// IResponse-t kap, aminek OutputWriter property-je egy System.IO.Pipelines.PipeWriter.
    /// </summary>
    public class SyncPlayStreamService : IService
    {
        public object Get(SubscribeStreamRequest request)
        {
            return new SseStreamWriter();
        }

        private class SseStreamWriter : IAsyncStreamWriter, IHasHeaders
        {
            public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>
            {
                ["Content-Type"] = "text/event-stream",
                ["Cache-Control"] = "no-cache",
                ["Connection"] = "keep-alive"
            };

            public async Task WriteToAsync(IResponse response, CancellationToken cancellationToken)
            {
                // Élő NAS-teszten (2026-09-28) kiderült: SendChunked nélkül a válasz az ELSŐ
                // íráskor/flush-kor "lezártnak" számított, és a kapcsolat pár másodpercen
                // belül megszakadt — emiatt a kliens gyakorlatilag soha nem kapott folyamatos
                // push-ot. Explicit chunked módra kell állítani, hogy a HTTP-válasz nyitva
                // maradjon a teljes stream-életciklus alatt.
                response.SendChunked = true;

                var writer = response.OutputWriter;
                var subscriberId = EmbySyncPlayEntryPoint.Broadcaster.Subscribe();
                var lastActivityUtc = DateTime.UtcNow;

                try
                {
                    // Kezdeti komment-sor (SSE-konvenció szerint ":"-tal kezdődő sor figyelmen
                    // kívül hagyva a kliens által) — ez azonnal kikényszeríti az első flush-t,
                    // hogy a böngésző EventSource rögtön "open" állapotba kerüljön, ne csak az
                    // első valódi eseménynél.
                    await writer.WriteAsync(Encoding.UTF8.GetBytes(": connected\n\n"), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var wroteAny = false;
                        while (EmbySyncPlayEntryPoint.Broadcaster.TryDequeue(subscriberId, out var payload))
                        {
                            var bytes = Encoding.UTF8.GetBytes(payload);
                            await writer.WriteAsync(new ReadOnlyMemory<byte>(bytes), cancellationToken).ConfigureAwait(false);
                            wroteAny = true;
                        }

                        // SSE keep-alive ping ~15 másodpercenként, hogy a kapcsolatot esetleg
                        // idle-timeoutként lezáró közbenső proxy/HTTP-réteg ne szakítsa meg.
                        if ((DateTime.UtcNow - lastActivityUtc).TotalSeconds >= 15)
                        {
                            await writer.WriteAsync(Encoding.UTF8.GetBytes(": ping\n\n"), cancellationToken).ConfigureAwait(false);
                            wroteAny = true;
                        }

                        if (wroteAny)
                        {
                            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                            lastActivityUtc = DateTime.UtcNow;
                        }

                        // Egyszerű poll-ciklus a broadcaster sorára. Ha az SDK későbbi
                        // verziója push-alapú ébresztést tesz lehetővé (pl. SemaphoreSlim a
                        // broadcaster oldalán), az hatékonyabb — MVP-ben elég ez is.
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    EmbySyncPlayEntryPoint.Broadcaster.Unsubscribe(subscriberId);
                }
            }
        }
    }
}
