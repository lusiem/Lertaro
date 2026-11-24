using Lertaro.Core.Indexer.Usn;

namespace Lertaro.Core.DriveMonitoring;

// Cancels pending drive-maintenance operations.
internal static class SearchEngineDriveMaintenanceCancellationExtensions
{
    // Mirrors NetworkIndexer.CancelDrive for a local drive's own rebuild: the user's Stop, and a removal
    // request (whose volume the walk must let go of). True if the drive has a rebuild running or a delayed
    // retry waiting. Neither caller discharges what the drive owes. A waiting retry is the recovery a
    // stopped or lost walk still owes: a cancel during the walk turns it into exactly this retry (see
    // RebuildDrive), so a cancel a moment after the walk ended keeps it too, and it is already "not now". A
    // removal may never resolve (a volume lock by chkdsk or an imaging tool sends the request and later
    // unlocks), so it does not hand the obligation to the removal's own recovery either. If the drive does
    // leave, the retry fails fast and parks it; that re-insertion can then cost one extra walk. This keeps
    // only what the walk owed: an unlock does not restore the monitor and index the lock request released.
    public static bool CancelDriveRebuild(this SearchEngineDriveMaintenance maintenance, string drive)
    {
        drive = DriveMaintenanceHelper.NormalizeDrive(drive);
        lock (maintenance._pendingDriveRebuilds)
        {
            if (maintenance.HasDelayedRebuild(drive))
                return true;
            if (!maintenance._activeRebuildCts.TryGetValue(drive, out var cts) || cts is null)
                return false;

            // Cancelled inside the lock it was read under: RebuildDrive's finally removes and disposes
            // that same instance while holding it, so a Stop landing exactly as the rebuild completed used
            // to call Cancel() on a disposed source and throw ObjectDisposedException at the caller. The
            // only thing registered on this token is handle.Dispose, which does not come back here.
            cts.Cancel();
            return true;
        }
    }

    // A forced rebuild now, or after the one already running for this drive -- decided in one critical
    // section (see TryBeginRebuild), so a request racing another queue is never left as a refused duplicate.
    public static void QueueDriveRebuildAfterRemoval(this SearchEngineDriveMaintenance maintenance, string drive) =>
        maintenance.QueueDriveRebuild(DriveMaintenanceHelper.NormalizeDrive(drive), forceRebuild: true,
            followUpIfBusy: true);
}
