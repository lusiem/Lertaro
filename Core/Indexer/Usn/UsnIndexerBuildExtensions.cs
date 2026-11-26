using Lertaro.Core.IndexV2;

using Lertaro.Core.DriveMonitoring;
using Lertaro.Core.IndexV2.Persistence;
using Lertaro.Core.Indexer.Usn.Journal;
namespace Lertaro.Core.Indexer.Usn;

public static class UsnIndexerBuildExtensions
{
    public static List<(string Drive, ulong JournalId, long NextUsn)> BuildIndex(this UsnIndexer indexer)
    {
        Logger.Log("[UsnIndexer] BuildIndex started");
        var drives = VolumeHelper.DetectIndexableLocalDrives();
        Logger.Log($"[UsnIndexer] Detected NTFS/ReFS drives: {string.Join(", ", drives)}");
        return indexer.BuildDrives(drives, clearExisting: true);
    }

    public static List<(string Drive, ulong JournalId, long NextUsn)> BuildDrives(
        this UsnIndexer indexer,
        IReadOnlyList<string> drives,
        bool clearExisting,
        string? cacheDir = null,
        Func<string, CancellationToken>? getToken = null,
        Action<string>? onDriveCancelled = null,
        Func<string, CancellationToken, DriveIndexRemovalScope?>? createDriveRemovalScope = null,
        bool forceFullScan = false)
    {
        lock (indexer.LockObj)
        {
            if (clearExisting)
            {
                indexer.Status.State = "indexing";
                indexer.Status.Progress = 0;
            }
            if (clearExisting)
            {
                indexer.Status.TotalFiles = 0;
                indexer.Status.TotalDirs = 0;
                indexer._driveMetadata.Clear();
                foreach (var live in indexer._recordIndexes.Values)
                    live.Dispose();
                indexer._recordIndexes.Clear();
                indexer.Status.ActiveDrives.Clear();
            }

            if (clearExisting)
                indexer.Status.ActiveDrives = drives.ToList();
            else
            {
                foreach (var drive in drives)
                {
                    if (!indexer.Status.ActiveDrives.Contains(drive))
                        indexer.Status.ActiveDrives.Add(drive);
                    indexer.SetDriveState(drive, "indexing");
                }
            }
        }

        // IndexV2 has no pure in-memory construction path (mmap is the only runtime representation),
        // so a cache directory is required -- every production call site already supplies one
        // (SearchEngineInitializer, DriveRecovery, SearchEngineDriveMaintenance); the parameterless
        // cacheDir default only existed for the old in-memory RuntimeIndex.Load path and is unused.
        if (string.IsNullOrWhiteSpace(cacheDir))
        {
            Logger.Log("[UsnIndexer] BuildDrives requires a cache directory under IndexV2; no drives will be built.", LogLevel.Error);
            return new List<(string, ulong, long)>();
        }

        FileRecordStore? GetPreviousStore(string drive) => forceFullScan ? null : indexer.GetWalkBaseline(drive);

        return IndexBuilder.BuildDrives(
            indexer._reader,
            drives,
            indexer.SetDriveState,
            indexer.UpdateDriveProgress,
            (drive, onProgress, token) =>
            {
                // Watching from before the walk so nothing changed behind the walker is lost -- see
                // FolderWalkRecorder; EnsureMonitor replays it once the live monitor is up. The recorder takes
                // the build's own token, not `token`: on a cold start that is the walk's removal scope, which
                // ends (and stops passing the build's cancellation on) with the walk, while the recorder lives
                // on until the hand-over. The recorder answers a removal request itself. The baseline is
                // copied inside the recorded span: on a multi-million-entry drive that copy takes a while, and
                // the drive's live monitor leaves its requests to the recording only once it is registered.
                return FolderWalkRecorder.Record(indexer, drive, getToken?.Invoke(drive) ?? CancellationToken.None, () =>
                    LocalDriveWalkBuilder.Build(drive, $"{drive}:\\", GetPreviousStore(drive), onProgress ?? ((_, _) => { }), token,
                        forceFullScan,
                        onCheckpoint: (checkpointStore, _) => indexer.PublishLocalDriveCheckpoint(cacheDir, drive, checkpointStore, token)));
            },
            (drive, result, progress, index) => OnDriveCompleted(indexer, cacheDir, drive, result, progress),
            (drive, result, progress, index) => OnDriveCompleted(indexer, cacheDir, drive, result.Store, progress),
            elapsedSeconds =>
            {
                lock (indexer.LockObj)
                {
                    indexer.Status.State = "ready";
                    indexer.Status.Progress = 100;
                    indexer.Status.ElapsedTime = elapsedSeconds;
                }
                Logger.Log($"[UsnIndexer] All indices built! Files: {indexer.Status.TotalFiles}, Folders: {indexer.Status.TotalDirs}. Time: {elapsedSeconds:F2}s");

                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                Win32Api.TrimWorkingSet();
            },
            getToken,
            onDriveCancelled,
            // Feeds ReFsScanner's own diff-reuse (see JournalReader.IndexDrive) -- a no-op for NTFS/$MFT
            // and never called for a non-journal drive (that's buildFolderDrive's own inline lookup above).
            GetPreviousStore,
            // ReFsScanner's own mid-walk checkpoint -- reuses the same publisher the non-USN local-drive
            // path above does (PublishLocalDriveCheckpoint doesn't care about SourceKind/IdKind, only about
            // writing a store and swapping a fresh LiveIndex in). A no-op for NTFS/$MFT (see JournalReader).
            (drive, checkpointStore, _, token) => indexer.PublishLocalDriveCheckpoint(cacheDir, drive, checkpointStore, token),
            createDriveRemovalScope
        );
    }

    // The drive's current index as the walk's diff baseline, or null for a full walk. An index a failed
    // save left unusable (LiveIndex.Compact could not reopen any snapshot) is no baseline: the re-walk asked
    // for to recover it walks in full instead of failing on it.
    internal static FileRecordStore? GetWalkBaseline(this UsnIndexer indexer, string drive)
    {
        lock (indexer.LockObj)
        {
            try
            {
                return indexer._recordIndexes.TryGetValue(drive, out var live) ? live.ToStore() : null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }

    // Writes the just-scanned store straight to its V2 cache file, THEN frees the in-memory
    // List<FileRecord> before mapping the file back in -- same "don't hold both the raw scan AND the
    // loaded index in memory at once" shape the old engine's LoadFromCacheDirect round-trip had, now
    // free (mmap opening is O(1), not a parse) instead of merely cheaper.
    internal static void OnDriveCompleted(UsnIndexer indexer, string cacheDir, string drive, FileRecordStore store, int progress)
    {
        bool hadIndex;
        lock (indexer.LockObj)
            hadIndex = indexer._recordIndexes.ContainsKey(drive);
        // Dropped before the write: the old index maps this same file, and the write replaces it.
        indexer.DropDriveFromRuntime(drive);

        var path = LocalDriveCacheLocator.GetCachePath(cacheDir, drive);
        try
        {
            SnapshotWriter.Write(store, path);
        }
        catch (Exception ex)
        {
            FailHandIn(indexer, drive, path, hadIndex, "writing", ex);
            throw;
        }
        var metadata = UsnIndexer.CreateMetadata(store);
        store.Records.Clear();
        store.Records.TrimExcess();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Win32Api.TrimWorkingSet();

        LiveIndex live;
        try
        {
            live = new LiveIndex(Snapshot.Open(path));
        }
        catch (Exception ex)
        {
            FailHandIn(indexer, drive, path, hadIndex, "opening", ex);
            throw;
        }

        lock (indexer.LockObj)
        {
            indexer._driveMetadata[drive] = metadata;
            indexer._recordIndexes[drive] = live;
            indexer.UpdateTotalsFromRuntime();
            indexer.Status.Progress = progress;
            indexer.UpdateDriveCounts(drive, markReady: true);
        }
    }

    // A write or open failure that persists (a full disk, a read-only cache file) left the drive with no index
    // at all. SnapshotWriter replaces the file all-or-nothing, so after a failed write the snapshot the dropped
    // index served is still there: serve it again, and let the build report this drive as failed
    // (IndexBuilder). It still misses what the dropped index held beyond that file, which only a re-index
    // recovers (a journal drive's catches up from the file's own stamp), so the drive is flagged for the
    // build's settle to ask for one. Under a failure that persists, that re-index fails the same way and
    // backs off like any repeated request (SearchEngineDriveMaintenance), down to one per 10 minutes.
    private static void FailHandIn(UsnIndexer indexer, string drive, string path, bool hadIndex, string step, Exception ex)
    {
        Logger.Log($"[UsnIndexer] Drive {drive}: {step} its new index failed; serving the snapshot on disk, if it opens, until the drive is re-indexed. {ex.Message}", LogLevel.Error);
        lock (indexer.LockObj)
            indexer._failedHandIns.Add(drive);
        if (hadIndex)
            ReopenAfterFailedWrite(indexer, drive, path);
    }

    // Metadata from the file's own header, not the dropped index's: on a journal drive that one may stamp
    // changes the file does not hold. Installed only if nothing has taken the drive since the drop.
    private static void ReopenAfterFailedWrite(UsnIndexer indexer, string drive, string path)
    {
        LiveIndex live;
        Snapshot snapshot;
        try
        {
            snapshot = Snapshot.Open(path);
            live = new LiveIndex(snapshot);
        }
        catch (Exception ex)
        {
            Logger.Log($"[UsnIndexer] Drive {drive}: reopening its previous index failed too; it serves nothing until it is re-indexed. {ex.Message}", LogLevel.Error);
            return;
        }
        bool installed;
        lock (indexer.LockObj)
        {
            installed = indexer._recordIndexes.TryAdd(drive, live);
            if (installed)
            {
                indexer._driveMetadata[drive] = UsnIndexerCacheExtensions.MetadataOf(snapshot);
                if (!indexer.Status.ActiveDrives.Contains(drive, StringComparer.OrdinalIgnoreCase))
                    indexer.Status.ActiveDrives.Add(drive);
                indexer.UpdateTotalsFromRuntime();
            }
        }
        if (!installed)
            live.Dispose();
    }
}
