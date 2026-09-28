using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace EmbySyncPlay.EntryPoints
{
    /// <summary>
    /// ARCHITECTURE.md 2.6 — periodikusan lefut, eltávolítja az inaktív party-kat, és ugyanitt
    /// fut a drift-korrekciós kiértékelés is (ARCHITECTURE.md 4. és 4.1 pont), mert mindkettő
    /// egy egyszerű, néhány másodperces időzítést igényel, nincs értelme külön háttérszálat
    /// nyitni rá.
    /// </summary>
    public class SyncSessionCleanupTask : IScheduledTask
    {
        public string Name => "EmbySyncPlay — Party takarítás és drift-korrekció";

        public string Description =>
            "Eltávolítja az inaktív közös-nézési party-kat, és kiértékeli/korrigálja a résztvevők lejátszási driftjét.";

        public string Category => "EmbySyncPlay";

        public string Key => "EmbySyncPlaySessionCleanup";

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || EmbySyncPlayEntryPoint.SyncManager == null)
            {
                return Task.CompletedTask;
            }

            EmbySyncPlayEntryPoint.SyncManager.RunDriftCorrectionPass(
                config.DriftToleranceSeconds, config.SeekCooldownSeconds);

            EmbySyncPlayEntryPoint.SyncManager.RemoveIdleSessions(config.SessionIdleTimeoutMinutes);

            progress.Report(100);
            return Task.CompletedTask;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromSeconds(10).Ticks
            };
        }
    }
}
