using Lertaro.Core.IndexV2;

namespace Lertaro.Core.Indexer.Usn;

// Advances the durable USN watermark and persists snapshots during idle maintenance. UsnMonitor advances
// progress; SearchEngine's idle trim saves it. Persisting live progress bounds replay work after restart
// and reduces exposure to journal wrap.
internal static class UsnIndexerDurabilityExtensions
{
    // Pending delta changes required before an idle tick is worth rewriting the whole snapshot for.
    // Self-throttling rather than timed: Compact swaps in a fresh DeltaOverlay, so this gate closes the
    // moment a write succeeds and only reopens once this much NEW churn has accumulated.
    internal const int IdleCompactPendingThreshold = 4096;

    // Records the journal position a successfully applied batch reached, so the next persist can stamp
    // it. Only ever called for a batch ApplyUsnRecords said it applied, and never past a pin, which is
    // what keeps "watermark <= what the snapshot actually contains" true.
    public static void AdvanceJournalWatermark(this UsnIndexer indexer, string drive, ulong journalId, long nextUsn)
    {
        lock (indexer.LockObj)
        {
            if (!indexer._driveMetadata.TryGetValue(drive, out var metadata))
                return;

            if (metadata.JournalWatermarkPinned)
                return;

            if (metadata.JournalId != journalId)
            {
                // The journal was recreated under a still-running monitor. Nothing newer can be
                // replayed from here, so pin at the last position this instance was consistent with.
                metadata.JournalWatermarkPinned = true;
                Logger.Log($"[UsnIndexer] {drive}: journal changed from {metadata.JournalId} to {journalId} mid-session; watermark pinned at {metadata.NextUsn} so the next cold start replays from there.", LogLevel.Warn);
                return;
            }

            metadata.JournalId = journalId;
            metadata.NextUsn = nextUsn;
        }
    }

    // Stops the watermark for a drive's CURRENT metadata instance, called when a batch had nowhere to
    // land. Cold start is the recovery path for a pin (catch-up replays from the persisted stamp), and a
    // rebuild clears it by installing a new instance.
    public static void PinJournalWatermark(this UsnIndexer indexer, string drive)
    {
        lock (indexer.LockObj)
            if (indexer._driveMetadata.TryGetValue(drive, out var metadata))
                metadata.JournalWatermarkPinned = true;
    }

    public static bool IsJournalWatermarkPinned(this UsnIndexer indexer, string drive)
    {
        lock (indexer.LockObj)
            return indexer._driveMetadata.TryGetValue(drive, out var metadata) && metadata.JournalWatermarkPinned;
    }

    // Folds each drive's accumulated delta into its cache file, stamped with the live watermark (journal
    // drives) or the metadata it was built with (FAT32/exFAT drives, whose only in-session persist this is).
    // Returns how many drives were written. Cheap to call often: drives below IdleCompactPendingThreshold
    // are skipped outright and Compact's own force:false path skips one whose delta emptied meanwhile.
    // `onReindexRequired` is the plain per-drive rebuild request, for a FAT32/exFAT drive whose write failed
    // and for any drive whose index a failed write left unusable.
    public static int CompactIdleDeltas(this UsnIndexer indexer, string cacheDir, Action<string>? onReindexRequired)
    {
        var targets = new List<(string Drive, LiveIndex Live, CompactionStamp Stamp)>();
        var leftUnusable = new List<string>();
        lock (indexer.LockObj)
        {
            foreach (var (drive, live) in indexer._recordIndexes)
            {
                if (!indexer._driveMetadata.TryGetValue(drive, out var metadata))
                    continue;

                // A folder (FAT32/exFAT) drive mid-rebuild: this LiveIndex is about to be replaced
                // wholesale and the rebuild's own SnapshotWriter.Write owns the cache path, so writing it
                // here would only race that. Its stamp is just the metadata it was built with -- there
                // is no live journal position to advance.
                if (!VolumeHelper.IsJournalCapableFileSystem(metadata.FileSystemType) &&
                    indexer.Status.Drives.Any(d => d.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase) && d.State == "indexing"))
                    continue;

                // Runs on SearchEngine's idle Timer, where an escaping exception ends the process. Every swap
                // and drop removes an index from _recordIndexes, under this lock, before disposing it (so does
                // UsnIndexer.Dispose), so one still registered that throws here was left unusable by a failed
                // save: another pass's Compact, the catch-up save (IndexCacheManager.SaveDrivesToCache) or the
                // alias reconciler, which only log it. It serves nothing, so it is dropped and rebuilt like the
                // one below.
                int pending;
                try
                {
                    pending = live.PendingChangeCount;
                }
                catch (ObjectDisposedException)
                {
                    leftUnusable.Add(drive);
                    continue;
                }
                if (pending < IdleCompactPendingThreshold)
                    continue;

                // A replay owns its drive until it finishes: Compact holds the LiveIndex write lock
                // across the merge and the whole snapshot rewrite, and taking it in the middle of a
                // catch-up drain stalls exactly the replay that is trying to catch up before the journal
                // moves on. The watermark stays where it is, so the next tick after the drain persists
                // the same changes -- nothing is lost by waiting.
                if (indexer.IsReplayInProgress(drive))
                    continue;

                // Stamp read BEFORE Compact, never after: a batch landing in between can only make the
                // persisted content NEWER than the stamp, so the next cold start replays a few records
                // that were already applied (harmless, and RemoveLink reports them). The opposite order
                // could persist a stamp ahead of content, which is the permanent hole this pins against.
                targets.Add((drive, live, new CompactionStamp(metadata.JournalId, metadata.NextUsn)));
            }
            foreach (var drive in leftUnusable)
            {
                indexer.DropDriveFromRuntime(drive);
                indexer._droppedUnusable.Add(drive);
            }
        }
        // Outside the lock, like the request below: it can start a rebuild.
        foreach (var drive in leftUnusable)
        {
            Logger.Log($"[UsnIndexer] {drive}: its index was left unusable by a failed save; dropped it and rebuilding the drive.", LogLevel.Error);
            indexer.RequestFolderReindex(drive, onReindexRequired);
        }

        var written = 0;
        foreach (var (drive, live, stamp) in targets)
        {
            try
            {
                // Outside LockObj on purpose: this holds the LiveIndex write lock across a full merge and
                // a multi-hundred-MB rewrite, which no search should queue behind while the indexer is
                // otherwise idle. Compact's own force:false gate re-checks the delta under that lock.
                if (live.Compact(LocalDriveCacheLocator.GetCachePath(cacheDir, drive), stamp))
                    written++;
            }
            catch (Exception ex)
            {
                bool replaced, walked, unusable;
                lock (indexer.LockObj)
                {
                    replaced = !indexer._recordIndexes.TryGetValue(drive, out var current) || !ReferenceEquals(current, live);
                    walked = indexer._driveMetadata.TryGetValue(drive, out var metadata) &&
                        !VolumeHelper.IsJournalCapableFileSystem(metadata.FileSystemType);
                    // A failed write that could not reopen any snapshot either leaves the LiveIndex unusable,
                    // and Compact says so with an ObjectDisposedException wrapping the write's failure. One
                    // without an inner exception comes from an index something else left unusable meanwhile
                    // (another pass, say). Either way, an index still current that throws it is unusable, and
                    // whoever finds it first under this lock drops it and asks; anyone after finds it
                    // `replaced`. Left in _recordIndexes it served nothing. Dropped under the lock that just
                    // found it current, so a rebuild swapping a new index in cannot land between the check and
                    // the drop.
                    unusable = !replaced && ex is ObjectDisposedException;
                    if (unusable)
                    {
                        indexer.DropDriveFromRuntime(drive);
                        indexer._droppedUnusable.Add(drive);
                    }
                }
                // A rebuild swapped this drive's LiveIndex out mid-write: its own SnapshotWriter.Write owns the
                // cache path now, so there is nothing left for this persist to say.
                if (ex is ObjectDisposedException && replaced)
                    continue;
                // An unusable index is rebuilt on any drive. With none loaded, the rebuild restores the snapshot
                // on disk if it can (a journal drive then catches up from that file's own stamp) and walks a
                // FAT32/exFAT drive from it. Any other failure leaves the index serving: the snapshot on disk
                // was reopened (without the changes since the last write), the merge failed before anything was
                // touched, or only the open of a fresh file failed. A FAT32/exFAT drive has no journal to find
                // what the reopen dropped, so it is re-walked from the index it serves, in all three cases since
                // they cannot be told apart here without racing live changes; the walk's hand-in keeps serving
                // that snapshot if its own write fails too (OnDriveCompleted). Both requests go through
                // RequestFolderReindex, so a walk already recording the drive covers them. A journal drive whose
                // snapshot reopened is left as before (#573): its recovery also has to keep the watermark from
                // stamping the lost changes as persisted.
                if (replaced || !(unusable || walked))
                {
                    Logger.Log($"[UsnIndexer] {drive}: idle compaction failed: {ex.Message}", LogLevel.Error);
                    continue;
                }
                Logger.Log(unusable
                    ? $"[UsnIndexer] {drive}: idle compaction left the index unusable; dropped it and rebuilding the drive. {ex.Message}"
                    : $"[UsnIndexer] {drive}: idle compaction failed and may have dropped the changes since the last write; re-walking the drive. {ex.Message}", LogLevel.Error);
                indexer.RequestFolderReindex(drive, onReindexRequired);
            }
        }

        return written;
    }
}
