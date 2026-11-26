using Lertaro.Core.DriveMonitoring;
using Lertaro.Core.Indexer.Usn;
using Lertaro.Core.IndexV2;
using Lertaro.Core.IndexV2.Delta;
using Lertaro.Core.IndexV2.Persistence;
using Lertaro.Core.Tests.IndexV2;

namespace Lertaro.Core.Tests.Indexer.Usn;

// The two things this guards, both of which are invisible from the outside when wrong: a watermark that
// advances past changes nothing applied (permanently un-replayable), and a delta that never reaches the
// cache file at all (so every restart replays the whole session, bounded by the volume's journal window).
[TestClass]
public sealed class UsnIndexerDurabilityExtensionsTests
{
    private const ulong JournalId = 7;
    private const long StartUsn = 100;

    [TestMethod]
    public void AdvanceJournalWatermark_SameJournal_RecordsThePositionToStampWith()
    {
        var indexer = new UsnIndexer();
        indexer._driveMetadata["C"] = Metadata("NTFS");

        indexer.AdvanceJournalWatermark("C", JournalId, 500);

        Assert.AreEqual(500L, indexer._driveMetadata["C"].NextUsn);
        Assert.IsFalse(indexer.IsJournalWatermarkPinned("C"));
    }

    [TestMethod]
    public void AdvanceJournalWatermark_JournalRecreatedMidSession_PinsInsteadOfMoving()
    {
        var indexer = new UsnIndexer();
        indexer._driveMetadata["C"] = Metadata("NTFS");

        indexer.AdvanceJournalWatermark("C", JournalId + 1, 500);

        Assert.AreEqual(StartUsn, indexer._driveMetadata["C"].NextUsn);
        Assert.IsTrue(indexer.IsJournalWatermarkPinned("C"));
    }

    // The pin is what keeps a dropped batch replayable, so it has to outlast every later batch, which all
    // arrive with the SAME journal id the metadata already holds.
    [TestMethod]
    public void AdvanceJournalWatermark_AfterPinning_IgnoresLaterBatches()
    {
        var indexer = new UsnIndexer();
        indexer._driveMetadata["C"] = Metadata("NTFS");
        indexer.PinJournalWatermark("C");

        indexer.AdvanceJournalWatermark("C", JournalId, 500);

        Assert.AreEqual(StartUsn, indexer._driveMetadata["C"].NextUsn);
    }

    // A rebuild or cold-start catch-up installs a fresh metadata over a consistent index/watermark pair,
    // which clears the pin by itself -- no per-drive bookkeeping to forget to reset.
    [TestMethod]
    public void PinJournalWatermark_IsNotInheritedByTheNextMetadataInstance()
    {
        var indexer = new UsnIndexer();
        indexer.PinJournalWatermark("C");
        indexer._driveMetadata["C"] = Metadata("NTFS");

        indexer.AdvanceJournalWatermark("C", JournalId, 500);

        Assert.AreEqual(500L, indexer._driveMetadata["C"].NextUsn);
    }

    [TestMethod]
    public void AdvanceJournalWatermark_DriveNoLongerLoaded_IsIgnored()
    {
        var indexer = new UsnIndexer();

        indexer.AdvanceJournalWatermark("C", JournalId, 500);

        Assert.IsFalse(indexer.IsJournalWatermarkPinned("C"));
    }

    [TestMethod]
    public void CompactIdleDeltas_BelowThreshold_WritesNoCacheFile()
    {
        using var tempDir = new TempDirectory();
        using var fixture = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        var indexer = Loaded(fixture, "NTFS");

        fixture.Index.Mutate((_, delta) => DeltaLinkOps.AddLink(delta, 2, 1, "one.txt", FileRecordFlags.None));

        Assert.AreEqual(0, indexer.CompactIdleDeltas(tempDir.Path, null));
        Assert.IsFalse(File.Exists(CachePath(tempDir.Path)));
    }

    [TestMethod]
    public void CompactIdleDeltas_AboveThreshold_FoldsTheDeltaAndStampsTheLiveWatermark()
    {
        using var tempDir = new TempDirectory();
        using var fixture = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        var indexer = Loaded(fixture, "NTFS");
        AddChurn(fixture.Index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold);
        indexer.AdvanceJournalWatermark("C", JournalId, 4242);

        Assert.AreEqual(1, indexer.CompactIdleDeltas(tempDir.Path, null));

        var header = SnapshotFormat.TryReadHeaderFromFile(CachePath(tempDir.Path));
        Assert.IsNotNull(header);
        Assert.AreEqual(4242L, header.NextUsn);
        Assert.AreEqual(JournalId, header.JournalId);
        // Root + the whole folded delta: the rows really made it into the file, not just the stamp.
        Assert.AreEqual(1 + UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold, header.RowCount);
    }

    // The threshold is self-closing rather than timed: Compact swaps in an empty DeltaOverlay, so the same
    // idle tick can't immediately pay for another full rewrite.
    [TestMethod]
    public void CompactIdleDeltas_RunAgainRightAfter_WritesNothing()
    {
        using var tempDir = new TempDirectory();
        using var fixture = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        var indexer = Loaded(fixture, "NTFS");
        AddChurn(fixture.Index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold);

        Assert.AreEqual(1, indexer.CompactIdleDeltas(tempDir.Path, null));
        Assert.AreEqual(0, indexer.CompactIdleDeltas(tempDir.Path, null));
    }

    // A FAT32/exFAT drive no longer persists on every watcher change (#298); this threshold is now its
    // only in-session persist, so it must not be skipped.
    [TestMethod]
    public void CompactIdleDeltas_NonJournalFileSystem_AboveThreshold_IsWritten()
    {
        using var tempDir = new TempDirectory();
        using var fixture = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        var indexer = Loaded(fixture, "exFAT");
        AddChurn(fixture.Index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold);

        Assert.AreEqual(1, indexer.CompactIdleDeltas(tempDir.Path, null));
        Assert.IsTrue(File.Exists(CachePath(tempDir.Path)));
    }

    // Mid-rebuild, the rebuild's own write owns the cache path and this LiveIndex is about to be replaced.
    [TestMethod]
    public void CompactIdleDeltas_NonJournalFileSystemWhileRebuilding_IsSkipped()
    {
        using var tempDir = new TempDirectory();
        using var fixture = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        var indexer = Loaded(fixture, "exFAT");
        indexer.Status.Drives.Add(new UsnIndexer.DriveIndexStatus { Drive = "C", State = "indexing" });
        AddChurn(fixture.Index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold);

        Assert.AreEqual(0, indexer.CompactIdleDeltas(tempDir.Path, null));
        Assert.IsFalse(File.Exists(CachePath(tempDir.Path)));
    }

    // This runs on SearchEngine's idle Timer, where an escaping exception ends the process. A registered
    // LiveIndex that is disposed (every swap removes an index before disposing it, so a failed save left this
    // one unusable) is not read: it is dropped, and the drive rebuilt.
    [TestMethod]
    [DataRow("NTFS")]
    [DataRow("exFAT")]
    public void CompactIdleDeltas_LiveIndexDisposed_DropsItWithoutThrowing(string fileSystemType)
    {
        using var tempDir = new TempDirectory();
        var fixture = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        var indexer = Loaded(fixture, fileSystemType);
        var requested = new List<string>();
        fixture.Index.Dispose();

        Assert.AreEqual(0, indexer.CompactIdleDeltas(tempDir.Path, requested.Add));
        Assert.IsFalse(File.Exists(CachePath(tempDir.Path)));
        Assert.IsFalse(indexer._recordIndexes.ContainsKey("C"));
        CollectionAssert.AreEqual(new[] { "C" }, requested);
        try { fixture.Dispose(); } catch { }
    }

    // A write that fails and cannot even reopen the snapshot on disk leaves the LiveIndex unusable: the drive
    // serves nothing, and left registered it made every totals recompute throw (another drive's change, its
    // rebuild's hand-in, a USN batch). It is dropped, and the drive rebuilt: a FAT32/exFAT drive re-walked
    // from no baseline, a journal drive restored from its cache file and caught up from that file's stamp.
    // Staged by putting a directory where the cache file was, so the write cannot replace it and the reopen
    // cannot open it.
    [TestMethod]
    [DataRow("exFAT")]
    [DataRow("NTFS")]
    public void CompactIdleDeltas_LeftUnusable_DropsTheIndexAndAsksForARebuild(string fileSystemType)
    {
        using var tempDir = new TempDirectory();
        var fixture = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        using var other = LiveIndexFixture.Build("Q", new[] { LiveIndexFixture.Root() });
        var indexer = Loaded(fixture, fileSystemType);
        indexer._recordIndexes["Q"] = other.Index;
        indexer._driveMetadata["Q"] = Metadata("NTFS");
        var requested = new List<string>();

        LeaveUnusable(tempDir.Path, fixture.Index, () => indexer.CompactIdleDeltas(tempDir.Path, requested.Add));

        CollectionAssert.AreEqual(new[] { "C" }, requested);
        Assert.IsFalse(indexer._recordIndexes.ContainsKey("C"), "the unusable index is still registered");
        Assert.IsFalse(indexer._driveMetadata.ContainsKey("C"));
        Assert.IsNull(indexer.GetWalkBaseline("C"));
        lock (indexer.LockObj)
            indexer.UpdateTotalsFromRuntime(); // what every other drive's change and hand-in run
        Assert.AreEqual(other.Index.GetCounts().Files, indexer.Status.TotalFiles);
        indexer._recordIndexes.Clear();
        try { fixture.Dispose(); } catch { }
    }

    // Two idle passes can both have selected a drive: the second selects it while the first is still busy
    // compacting another drive. Then the pass whose reopen failed and the pass that finds the index it left
    // unusable both fail on it, and only the first owes the re-walk. Pass A is parked on D's Compact by a
    // reader holding D's index, after it selected [D, C]; pass B (D now rebuilding, so skipped) leaves C
    // unusable, and parks too: dropping C recomputes the totals, which reads D's counts behind pass A's queued
    // write lock. Then both go on, and pass A reaches C.
    [TestMethod]
    public void CompactIdleDeltas_TwoPassesSelectedTheSameDrive_OneUnusableIndexAsksForOneRewalk()
    {
        using var tempDir = new TempDirectory();
        var c = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        using var d = LiveIndexFixture.Build("D", new[] { LiveIndexFixture.Root() });
        var indexer = new UsnIndexer();
        var requests = new List<string>();
        void Request(string drive) { lock (requests) requests.Add(drive); }
        using var release = new ManualResetEventSlim();
        using var holding = new ManualResetEventSlim();
        Thread? holder = null, passA = null, passB = null;
        try
        {
            // Inserted D first so a pass compacts D before C.
            indexer._recordIndexes["D"] = d.Index;
            indexer._driveMetadata["D"] = Metadata("exFAT");
            indexer._recordIndexes["C"] = c.Index;
            indexer._driveMetadata["C"] = Metadata("exFAT");
            indexer.Status.Drives.Add(new UsnIndexer.DriveIndexStatus { Drive = "D", State = "indexing" });
            LeaveUnusableOnNextWrite(tempDir.Path, c.Index, () => indexer.CompactIdleDeltas(tempDir.Path, null));
            indexer.Status.Drives.Clear();
            AddChurn(d.Index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold);

            holder = new Thread(() => d.Index.Read((_, _) => { holding.Set(); release.Wait(); return 0; })) { IsBackground = true };
            holder.Start();
            Assert.IsTrue(holding.Wait(TimeSpan.FromSeconds(30)));
            passA = new Thread(() => indexer.CompactIdleDeltas(tempDir.Path, Request)) { IsBackground = true };
            passA.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() => (passA.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(30)),
                "precondition: pass A is parked on D's Compact");

            indexer.Status.Drives.Add(new UsnIndexer.DriveIndexStatus { Drive = "D", State = "indexing" });
            passB = new Thread(() => indexer.CompactIdleDeltas(tempDir.Path, Request)) { IsBackground = true };
            passB.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() => (passB.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(30)),
                "precondition: pass B left C unusable and waits for D's counts");
            release.Set();
            Assert.IsTrue(passA.Join(TimeSpan.FromSeconds(30)));
            Assert.IsTrue(passB.Join(TimeSpan.FromSeconds(30)));

            CollectionAssert.AreEqual(new[] { "C" }, requests);
        }
        finally
        {
            release.Set();
            holder?.Join(TimeSpan.FromSeconds(5));
            passA?.Join(TimeSpan.FromSeconds(5));
            passB?.Join(TimeSpan.FromSeconds(5));
            indexer._recordIndexes.Clear();
            try { c.Dispose(); } catch { }
        }
    }

    // The catch-up save leaves C unusable after a pass has selected it: that pass's Compact then fails with
    // EnsureUsable's unwrapped ObjectDisposedException on an index that is still current. It is just as
    // unusable, so the pass drops it and asks once, instead of re-walking a drive whose dead index stays
    // registered. Pass A is parked on D's Compact by a reader holding D, after it selected [D, C].
    [TestMethod]
    public void CompactIdleDeltas_SelectedIndexLeftUnusableBeforeItsCompact_IsDroppedAndAskedForOnce()
    {
        using var tempDir = new TempDirectory();
        var c = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        using var d = LiveIndexFixture.Build("D", new[] { LiveIndexFixture.Root() });
        var indexer = new UsnIndexer();
        var requests = new List<string>();
        void Request(string drive) { lock (requests) requests.Add(drive); }
        using var release = new ManualResetEventSlim();
        using var holding = new ManualResetEventSlim();
        Thread? holder = null, passA = null;
        try
        {
            indexer._recordIndexes["D"] = d.Index; // first, so the pass compacts D before C
            indexer._driveMetadata["D"] = Metadata("exFAT");
            indexer._recordIndexes["C"] = c.Index;
            indexer._driveMetadata["C"] = Metadata("exFAT");
            AddChurn(c.Index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold);
            AddChurn(d.Index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold);
            holder = new Thread(() => d.Index.Read((_, _) => { holding.Set(); release.Wait(); return 0; })) { IsBackground = true };
            holder.Start();
            Assert.IsTrue(holding.Wait(TimeSpan.FromSeconds(30)));
            passA = new Thread(() => indexer.CompactIdleDeltas(tempDir.Path, Request)) { IsBackground = true };
            passA.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() => (passA.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(30)),
                "precondition: pass A is parked on D's Compact");

            Directory.CreateDirectory(CachePath(tempDir.Path));
            Assert.ThrowsExactly<ObjectDisposedException>(() =>
                indexer.SaveDrivesToCache(tempDir.Path, new() { ("C", JournalId, StartUsn) }),
                "precondition: the catch-up save left C unusable");
            release.Set();
            Assert.IsTrue(passA.Join(TimeSpan.FromSeconds(30)));

            CollectionAssert.AreEqual(new[] { "C" }, requests);
            Assert.IsFalse(indexer._recordIndexes.ContainsKey("C"), "the dead index is still registered");
        }
        finally
        {
            release.Set();
            holder?.Join(TimeSpan.FromSeconds(5));
            passA?.Join(TimeSpan.FromSeconds(5));
            indexer._recordIndexes.Clear();
            try { c.Dispose(); } catch { }
        }
    }

    // Two passes, two drives on the same failing disk. Pass A is queued on D's write lock (a reader holds D).
    // Pass B leaves C unusable and drops it, and the drop's totals read D's counts behind A's queued writer.
    // A then leaves D unusable too, and B's read of D threw out of CompactIdleDeltas -- on the idle Timer, a
    // process exit -- after C was removed and before its rebuild was asked for.
    [TestMethod]
    public void CompactIdleDeltas_DropReadsAnotherDriveTheOtherPassLeftUnusable_DoesNotThrowAndAsksForBoth()
    {
        using var tempDir = new TempDirectory();
        var c = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        var d = LiveIndexFixture.Build("D", new[] { LiveIndexFixture.Root() });
        var indexer = new UsnIndexer();
        var requests = new List<string>();
        void Request(string drive) { lock (requests) requests.Add(drive); }
        Exception? passBThrew = null;
        using var release = new ManualResetEventSlim();
        using var holding = new ManualResetEventSlim();
        Thread? holder = null, passA = null, passB = null;
        try
        {
            indexer._recordIndexes["D"] = d.Index; // first, so pass A compacts D before C
            indexer._driveMetadata["D"] = Metadata("exFAT");
            indexer._recordIndexes["C"] = c.Index;
            indexer._driveMetadata["C"] = Metadata("exFAT");
            Directory.CreateDirectory(LocalDriveCacheLocator.GetCachePath(tempDir.Path, "C"));
            Directory.CreateDirectory(LocalDriveCacheLocator.GetCachePath(tempDir.Path, "D"));
            AddChurn(c.Index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold);
            AddChurn(d.Index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold);

            holder = new Thread(() => d.Index.Read((_, _) => { holding.Set(); release.Wait(); return 0; })) { IsBackground = true };
            holder.Start();
            Assert.IsTrue(holding.Wait(TimeSpan.FromSeconds(30)));
            passA = new Thread(() => indexer.CompactIdleDeltas(tempDir.Path, Request)) { IsBackground = true };
            passA.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() => (passA.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(30)),
                "precondition: pass A is queued on D's write lock");
            indexer.Status.Drives.Add(new UsnIndexer.DriveIndexStatus { Drive = "D", State = "indexing" }); // pass B skips D
            passB = new Thread(() =>
            {
                try { indexer.CompactIdleDeltas(tempDir.Path, Request); }
                catch (Exception ex) { passBThrew = ex; }
            }) { IsBackground = true };
            passB.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() => (passB.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(30)),
                "precondition: pass B dropped C and waits for D's counts");
            release.Set();
            Assert.IsTrue(passA.Join(TimeSpan.FromSeconds(30)));
            Assert.IsTrue(passB.Join(TimeSpan.FromSeconds(30)));

            Assert.IsNull(passBThrew, $"escaped the idle Timer (process exit): {passBThrew}");
            lock (requests)
                CollectionAssert.AreEquivalent(new[] { "C", "D" }, requests);
        }
        finally
        {
            release.Set();
            holder?.Join(TimeSpan.FromSeconds(5));
            passA?.Join(TimeSpan.FromSeconds(5));
            passB?.Join(TimeSpan.FromSeconds(5));
            indexer._recordIndexes.Clear();
            try { c.Dispose(); } catch { }
            try { d.Dispose(); } catch { }
        }
    }

    // A write that fails while the snapshot on disk reopens leaves the index serving, without the changes made
    // since the last write. A FAT32/exFAT drive has no journal to find them in, so it is re-walked at once from
    // the index it still serves (the walk's own hand-in keeps serving it if that write fails too:
    // UsnIndexerBuildExtensionsTests). A journal drive keeps serving it; its recovery is #573.
    [TestMethod]
    [DataRow("exFAT")]
    [DataRow("NTFS")]
    public void CompactIdleDeltas_WriteFailsButTheSnapshotReopens_KeepsServingIt(string fileSystemType)
    {
        using var tempDir = new TempDirectory();
        using var fixture = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        var indexer = Loaded(fixture, fileSystemType);
        var requested = new List<string>();
        AddChurn(fixture.Index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold);
        Assert.AreEqual(1, indexer.CompactIdleDeltas(tempDir.Path, requested.Add), "precondition: the first write lands");
        AddChurn(fixture.Index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold, first: 100_000);
        File.SetAttributes(CachePath(tempDir.Path), FileAttributes.ReadOnly);
        try
        {
            Assert.AreEqual(0, indexer.CompactIdleDeltas(tempDir.Path, requested.Add));
        }
        finally
        {
            File.SetAttributes(CachePath(tempDir.Path), FileAttributes.Normal);
        }

        Assert.AreEqual(0, fixture.Index.PendingChangeCount, "serving the reopened snapshot");
        Assert.IsNotNull(indexer.GetWalkBaseline("C"));
        if (fileSystemType == "exFAT")
            CollectionAssert.AreEqual(new[] { "C" }, requested, "the changes the reopen dropped are never walked again");
        indexer._recordIndexes.Clear();
    }

    // The idle Timer keeps running while a startup does, when the engine refuses every rebuild request (see
    // SearchEngineDriveMaintenance.TryBeginRebuild): for instance a second InitializeOrLoadIndex (a client's
    // rebuild) while the previous index still holds enough pending changes, or the moment after a drive's
    // hand-over and before the startup completes. Handed the startup's RequestRebuild, as SearchEngine does,
    // the re-walk waits for the startup to finish instead of being dropped.
    [TestMethod]
    public void CompactIdleDeltas_FolderDriveLeftUnusableDuringStartup_ReWalkIsFlushedAfterIt()
    {
        using var tempDir = new TempDirectory();
        var fixture = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        var indexer = Loaded(fixture, "exFAT");
        var starting = true;
        var accepted = new List<string>();
        var initializer = new SearchEngineInitializer(indexer, tempDir.Path, d => { if (!starting) accepted.Add(d); });

        LeaveUnusable(tempDir.Path, fixture.Index, () => indexer.CompactIdleDeltas(tempDir.Path, initializer.RequestRebuild));
        indexer._recordIndexes.Clear();
        try { fixture.Dispose(); } catch { }
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            initializer.Run(forceRebuild: true, cts, isRebuilding => starting = isRebuilding, new MachineSettings());
        }

        CollectionAssert.AreEqual(new[] { "C" }, accepted);
    }

    // The re-walk a failed save asks for ends in OnDriveCompleted, which drops the drive's index before
    // writing the walk's result over the same file. Under a write failure that persists (here, a read-only
    // cache file) that left the drive with no index at all. It serves the snapshot still on disk instead,
    // with that file's own stamp, and the build reports the drive as failed.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void OnDriveCompleted_WriteFails_ServesThePreviousSnapshotAgain(bool hadIndex)
    {
        using var tempDir = new TempDirectory();
        var path = CachePath(tempDir.Path);
        SnapshotWriter.Write(Store("before.txt"), path);
        var indexer = new UsnIndexer();
        if (hadIndex)
        {
            indexer._recordIndexes["C"] = new LiveIndex(Snapshot.Open(path));
            indexer._driveMetadata["C"] = Metadata("exFAT"); // the dropped index's, stamped past the file
        }
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Exception? thrown = null;
        try
        {
            UsnIndexerBuildExtensions.OnDriveCompleted(indexer, tempDir.Path, "C", Store("walked-1.txt", "walked-2.txt"), 100);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.IsNotNull(thrown, "a failed write must fail the drive's hand-in");
        if (hadIndex)
        {
            Assert.AreEqual(1, indexer._recordIndexes["C"].GetCounts().Files, "not serving the previous snapshot");
            Assert.AreEqual(0L, indexer._driveMetadata["C"].NextUsn, "stamped from the dropped index, not the file");
        }
        else
            Assert.IsFalse(indexer._recordIndexes.ContainsKey("C"), "a drive that had no index got one from a stale file");
        indexer.Dispose();
    }

    // A failed hand-in on a FAT32/exFAT drive reopens the snapshot on disk, without what the old live monitor
    // applied this session (an uncompacted delta) and what the walk recorded. The build settles as failed, which
    // discards the recording too: it must ask for the re-index that recovers them, as the idle path does.
    [TestMethod]
    public void FailedHandIn_OnAFolderDrive_TheSettleAsksForAReIndex()
    {
        if (!DriveArrivalRecovery.IsMounted("C"))
            Assert.Inconclusive("C: must be mounted so SettleFailedBuild takes its mounted branch.");
        using var tempDir = new TempDirectory();
        var path = CachePath(tempDir.Path);
        SnapshotWriter.Write(Store("before.txt"), path);
        var indexer = new UsnIndexer();
        indexer._recordIndexes["C"] = new LiveIndex(Snapshot.Open(path));
        indexer._driveMetadata["C"] = Metadata("exFAT");
        indexer.Status.Drives.Add(new UsnIndexer.DriveIndexStatus { Drive = "C", State = "indexing", Enabled = true });
        indexer._recordIndexes["C"].Mutate((_, delta) => DeltaLinkOps.AddLink(delta, 50, 1, "made-this-session.txt", FileRecordFlags.None));
        using var build = new CancellationTokenSource();
        var recorder = new FolderWalkRecorder("C", build.Token);
        FolderWalkRecorder.Keep(indexer, "C", recorder);
        var plain = new List<string>();
        File.SetAttributes(path, FileAttributes.ReadOnly); // a transient write failure (AV, a sharing violation)
        var handInFailed = false;
        try
        {
            UsnIndexerBuildExtensions.OnDriveCompleted(indexer, tempDir.Path, "C", Store("before.txt", "made-this-session.txt"), 100);
        }
        catch (Exception)
        {
            handInFailed = true;
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        Assert.IsTrue(handInFailed, "precondition: the hand-in's write failed");

        DriveArrivalRecovery.SettleFailedBuild(indexer, "C", wasCancelled: false, build.Token, CancellationToken.None, null, plain.Add);

        CollectionAssert.AreEqual(new[] { "C" }, plain, "the failed hand-in's losses are never walked again");
        Assert.IsFalse(indexer.ConsumeMissedFolderChangeDuringRebuild("C"));
        indexer.Dispose();
    }

    // The catch-up save after a cold start or a drive's return (SaveDrivesToCache, Compact with force) only logs a
    // failure, so an index it left unusable stayed registered: the idle compactor skipped it for good, the drive
    // served nothing for the session, and every totals recompute threw on it.
    [TestMethod]
    public void CompactIdleDeltas_IndexLeftUnusableByTheCatchUpSave_IsDroppedAndRebuilt()
    {
        using var tempDir = new TempDirectory();
        var fixture = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        var indexer = Loaded(fixture, "NTFS");
        var requested = new List<string>();
        Directory.CreateDirectory(CachePath(tempDir.Path));
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            indexer.SaveDrivesToCache(tempDir.Path, new() { ("C", JournalId, StartUsn + 50) }),
            "precondition: the catch-up save left the index unusable");

        indexer.CompactIdleDeltas(tempDir.Path, requested.Add);

        CollectionAssert.AreEqual(new[] { "C" }, requested);
        Assert.IsFalse(indexer._recordIndexes.ContainsKey("C"), "the unusable index is still registered");
        try { fixture.Dispose(); } catch { }
    }

    // Another drive's index already unusable (the catch-up save above): dropping this one recomputes the totals,
    // which read that drive's counts and threw out of CompactIdleDeltas, on the idle Timer a process exit, after
    // this drive was removed and before its rebuild was asked for.
    [TestMethod]
    public void CompactIdleDeltas_AnotherDriveAlreadyUnusable_DroppingThisOneDoesNotThrow()
    {
        using var tempDir = new TempDirectory();
        var c = LiveIndexFixture.Build("C", new[] { LiveIndexFixture.Root() });
        var d = LiveIndexFixture.Build("D", new[] { LiveIndexFixture.Root() });
        var indexer = Loaded(c, "exFAT");
        indexer._recordIndexes["D"] = d.Index;
        indexer._driveMetadata["D"] = Metadata("exFAT");
        // Mid-rebuild, so no pass sweeps it: the rebuild replaces it.
        indexer.Status.Drives.Add(new UsnIndexer.DriveIndexStatus { Drive = "D", State = "indexing" });
        var requested = new List<string>();
        d.Index.Dispose(); // what an unusable index looks like to every reader

        LeaveUnusable(tempDir.Path, c.Index, () => indexer.CompactIdleDeltas(tempDir.Path, requested.Add));

        CollectionAssert.AreEqual(new[] { "C" }, requested);
        indexer._recordIndexes.Clear();
        try { c.Dispose(); } catch { }
        try { d.Dispose(); } catch { }
    }

    private static FileRecordStore Store(params string[] files)
    {
        var store = new FileRecordStore { SourceKey = "C", SourceKind = FileRecordSourceKind.LocalMft, IdKind = FileRecordIdKind.MftFrn, RootId = 1 };
        store.Records.Add(LiveIndexFixture.Root());
        for (var i = 0; i < files.Length; i++)
            store.Records.Add(new FileRecord((ulong)(i + 2), 1, files[i], FileRecordFlags.None));
        return store;
    }

    private static void LeaveUnusable(string cacheDir, LiveIndex index, Action compact)
    {
        LeaveUnusableOnNextWrite(cacheDir, index, compact);
        compact();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = index.PendingChangeCount, "precondition: left unusable");
    }

    // Lands one write of drive C, then puts a directory where its cache file was, so the next write cannot
    // replace it and the reopen cannot open it.
    private static void LeaveUnusableOnNextWrite(string cacheDir, LiveIndex index, Action compact)
    {
        AddChurn(index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold);
        compact();
        Assert.IsTrue(File.Exists(CachePath(cacheDir)), "precondition: the first write lands");
        File.Delete(CachePath(cacheDir)); // the LiveIndex's mapping allows the delete
        Directory.CreateDirectory(CachePath(cacheDir));
        AddChurn(index, UsnIndexerDurabilityExtensions.IdleCompactPendingThreshold, first: 100_000);
    }

    private static UsnIndexer.DriveRuntimeMetadata Metadata(string fileSystemType) => new()
    {
        SourceKind = FileRecordSourceKind.LocalMft,
        IdKind = FileRecordIdKind.MftFrn,
        FileSystemType = fileSystemType,
        JournalId = JournalId,
        NextUsn = StartUsn,
    };

    private static UsnIndexer Loaded(LiveIndexFixture fixture, string fileSystemType)
    {
        var indexer = new UsnIndexer();
        indexer._recordIndexes["C"] = fixture.Index;
        indexer._driveMetadata["C"] = Metadata(fileSystemType);
        return indexer;
    }

    private static void AddChurn(LiveIndex index, int count, int first = 0) => index.Mutate((_, delta) =>
    {
        for (var i = first; i < first + count; i++)
            DeltaLinkOps.AddLink(delta, (ulong)(i + 2), 1, $"churn{i}.txt", FileRecordFlags.None);
    });

    private static string CachePath(string cacheDir) => LocalDriveCacheLocator.GetCachePath(cacheDir, "C");

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("lertaro-tests-").FullName;
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
