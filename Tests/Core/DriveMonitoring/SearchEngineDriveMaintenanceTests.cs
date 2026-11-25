using Lertaro.Core.DriveMonitoring;
using Lertaro.Core.Indexer.Usn;

namespace Lertaro.Core.Tests.DriveMonitoring;

// A re-index request raised while the same drive's own rebuild is still running -- the missed-folder-change
// follow-up at the end of that rebuild, or a monitor's request -- used to be refused as a duplicate and lost.
// Most rebuilds here run against an unmounted letter: such a rebuild fails fast and parks the drive without
// touching a volume, which is enough because follow-ups are decided after a rebuild whatever its outcome.
// Tests that need a mounted drive (a volume id, a status refresh) cancel every rebuild of it before it runs,
// so none of them reads the volume. The test runs each queued rebuild itself, and delayed retries wait on a
// clock the test advances. The lifetime source is cancelled but never disposed: a parked drive's re-insertion
// waiter is still registered on it (SearchEngine cancels without disposing for the same reason).
[TestClass]
public sealed class SearchEngineDriveMaintenanceTests
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(30);

    private static string FreeDriveLetter() =>
        Enumerable.Range('A', 26).Select(c => ((char)c).ToString()).First(l => !VolumeHelper.DetectIndexableLocalDrives().Contains(l));

    private sealed class Rig : IDisposable
    {
        public readonly CancellationTokenSource Lifetime = new();
        public readonly UsnIndexer Indexer = new();
        public readonly ManualTime Time = new();
        public readonly WorkQueue Work = new();
        public readonly SearchEngineDriveMaintenance Maintenance;

        public Rig(Func<MachineSettings>? settings = null, Func<bool>? isRebuilding = null, Func<CancellationToken>? token = null) =>
            Maintenance = new SearchEngineDriveMaintenance(Indexer, settings ?? (() => new MachineSettings()),
                token ?? (() => Lifetime.Token), isRebuilding ?? (() => false), () => { }, Time, Work.Add);

        public void Dispose()
        {
            Lifetime.Cancel();
            Indexer.Dispose();
        }
    }

    // Rebuild work, run only when the test says so. Locked, because a delayed retry queues its rebuild from the
    // pool thread its timer continuation runs on.
    private sealed class WorkQueue
    {
        private readonly List<Action> _items = new();

        public void Add(Action work)
        {
            lock (_items)
            {
                _items.Add(work);
                Monitor.PulseAll(_items);
            }
        }

        public int Count { get { lock (_items) return _items.Count; } }

        public void Run(int index)
        {
            Action work;
            lock (_items)
                work = _items[index];
            work();
        }

        public void WaitForCount(int count)
        {
            lock (_items)
                while (_items.Count < count)
                    if (!Monitor.Wait(_items, TimeSpan.FromSeconds(10)))
                        Assert.Fail($"Expected {count} queued rebuilds, got {_items.Count}.");
        }
    }

    // A clock that only moves when the test advances it. Task.Delay(delay, TimeProvider, token) waits on one
    // timer from CreateTimer and disposes it when the token is cancelled.
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();
        private TimeSpan _now;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_timers)
                return DateTimeOffset.UnixEpoch + _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_timers)
            {
                _now += by;
                due = _timers.Where(t => t.DueAt <= _now).ToList();
                _timers.RemoveAll(due.Contains);
            }
            foreach (var timer in due)
                timer.Fire();
        }

        private sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (time._timers)
                {
                    time._timers.Remove(this);
                    if (dueTime == Timeout.InfiniteTimeSpan)
                        return true;
                    DueAt = time._now + dueTime;
                    time._timers.Add(this);
                }
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (time._timers)
                    time._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    // The original rebuild and its immediate rerun, each asked to re-index again while it ran: the drive is
    // left waiting on a delayed retry, with two rebuilds queued and run.
    private static void ReachWaitingRetry(Rig rig, string drive)
    {
        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false));
        rig.Maintenance.QueueDriveRebuild(drive);
        rig.Work.Run(0);
        rig.Maintenance.QueueDriveRebuild(drive);
        rig.Work.Run(1);
        Assert.AreEqual(2, rig.Work.Count, "precondition: the follow-up of a follow-up does not run at once");
        Assert.IsFalse(rig.Maintenance.HasPendingRebuilds);
    }

    private static MachineSettings Selecting(string drive) => new() { LocalDrives = { VolumeHelper.GetVolumeId(drive)! } };

    // ReachWaitingRetry for a mounted drive: its one walk is stopped before it runs, and the request it was
    // asked for meanwhile survives the Stop as a waiting retry.
    private static void ReachWaitingRetryWithoutReadingTheVolume(Rig rig, string drive)
    {
        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false));
        Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive)); // Stop
        rig.Maintenance.QueueDriveRebuild(drive);
        rig.Work.Run(0);
        Assert.AreEqual(1, rig.Work.Count, "precondition: a follow-up of a stopped walk does not run at once");
        Assert.IsFalse(rig.Maintenance.HasPendingRebuilds);
    }

    // True when a plain request collapses into a waiting retry instead of starting a rebuild. Starts one when
    // it does not, so it is only ever the last check of a test.
    private static bool IsWaiting(Rig rig, string drive)
    {
        var before = rig.Work.Count;
        rig.Maintenance.QueueDriveRebuild(drive);
        return rig.Work.Count == before;
    }

    [TestMethod]
    public void QueueDriveRebuild_RequestedWhileThatDriveRebuilds_RunsOnceAfterIt()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();

        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: true));
        // Two requests during one rebuild collapse into one rerun.
        rig.Maintenance.QueueDriveRebuild(drive);
        rig.Maintenance.QueueDriveRebuild(drive);
        rig.Work.Run(0);

        Assert.AreEqual(2, rig.Work.Count);
        Assert.IsTrue(rig.Maintenance.HasPendingRebuilds);
        rig.Lifetime.Cancel();
        rig.Work.Run(1);
        Assert.AreEqual(2, rig.Work.Count);
        Assert.IsFalse(rig.Maintenance.HasPendingRebuilds);
    }

    // A busy FAT32/exFAT drive's old folder watcher flags a missed change on every walk, so every rebuild asks
    // for another. No request may be lost, and the walk rate still has to be bounded. So the first follow-up
    // runs at once, and each one after it waits on the clock: 30 s, then doubling up to 10 min.
    [TestMethod]
    public void QueueDriveRebuild_EveryRebuildAsksForAnother_BacksOffWithoutDroppingARequest()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();

        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false));
        rig.Maintenance.QueueDriveRebuild(drive);
        rig.Work.Run(0);
        Assert.AreEqual(2, rig.Work.Count, "the first follow-up runs at once");
        var walk = 1;
        foreach (var delay in new[] { 30, 60, 120, 240, 480, 600, 600 }.Select(s => TimeSpan.FromSeconds(s)))
        {
            rig.Maintenance.QueueDriveRebuild(drive);
            rig.Work.Run(walk);
            Assert.AreEqual(walk + 1, rig.Work.Count, "a follow-up of a follow-up ran at once");
            rig.Time.Advance(delay - TimeSpan.FromMilliseconds(1));
            Assert.AreEqual(walk + 1, rig.Work.Count, $"the retry due after {delay} ran early");
            rig.Time.Advance(TimeSpan.FromMilliseconds(1));
            rig.Work.WaitForCount(walk + 2);
            walk++;
        }
    }

    // A real loss (a watcher overflow, say) during the rerun used to be logged as dropped, with nothing queued,
    // recorded or retried afterwards.
    [TestMethod]
    public void QueueDriveRebuild_RealRequestDuringTheRerun_IsNotLost()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();

        ReachWaitingRetry(rig, drive);
        rig.Time.Advance(FirstRetryDelay);

        rig.Work.WaitForCount(3);
    }

    // The chain ends once a rebuild completes with no request raised during it, but only resets after the
    // drive has then stayed quiet for that rebuild's wait. After that, its next request runs at once again.
    [TestMethod]
    public void QueueDriveRebuild_QuietForTheWholeWaitAfterTheChainEnded_NextRequestRunsAtOnce()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();

        ReachWaitingRetry(rig, drive);
        rig.Time.Advance(FirstRetryDelay);
        rig.Work.WaitForCount(3);
        rig.Work.Run(2);
        Assert.IsFalse(rig.Maintenance.HasPendingRebuilds, "precondition: the chain ended");
        rig.Time.Advance(FirstRetryDelay * 2); // the wait of the retry that just ran

        rig.Maintenance.QueueDriveRebuild(drive);
        rig.Maintenance.QueueDriveRebuild(drive);
        rig.Work.Run(3);

        Assert.AreEqual(5, rig.Work.Count);
    }

    // A FAT32/exFAT drive's walk recording absorbs what its live monitor asks for during the walk, so a drive
    // whose monitor overflows right after every hand-over asks only once each rebuild has ended -- and used to
    // restart an immediate whole-drive walk every time. Asked again within the last rebuild's wait, it waits,
    // and the wait keeps doubling.
    [TestMethod]
    public void QueueDriveRebuild_AskedAgainSoonAfterItsRebuildEnded_WaitsAndKeepsBackingOff()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();

        rig.Maintenance.QueueDriveRebuild(drive);
        rig.Work.Run(0); // ends quietly
        rig.Time.Advance(FirstRetryDelay - TimeSpan.FromSeconds(1));
        rig.Maintenance.QueueDriveRebuild(drive); // the monitor overflows right after the hand-over
        Assert.AreEqual(1, rig.Work.Count, "asked again within the last rebuild's wait, and walked at once");
        rig.Time.Advance(FirstRetryDelay);
        rig.Work.WaitForCount(2);
        rig.Work.Run(1); // ends quietly too
        rig.Time.Advance(FirstRetryDelay * 2 - TimeSpan.FromSeconds(1));
        rig.Maintenance.QueueDriveRebuild(drive);
        rig.Time.Advance(FirstRetryDelay * 2 - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(2, rig.Work.Count, "the second wait did not double");
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        rig.Work.WaitForCount(3);
    }

    // A forced rebuild (a manual one, or a failed removal's recovery) runs now and supersedes the waiting
    // retry, which then never starts a second rebuild of the same drive.
    [TestMethod]
    public void QueueDriveRebuild_ForcedWhileARetryWaits_SupersedesIt()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();
        ReachWaitingRetry(rig, drive);

        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: true));
        rig.Time.Advance(FirstRetryDelay);
        rig.Work.Run(2);
        rig.Time.Advance(FirstRetryDelay); // past the forced rebuild's own wait

        Assert.AreEqual(3, rig.Work.Count);
        Assert.IsFalse(IsWaiting(rig, drive));
    }

    // Monitor and recovery requests during the wait collapse into the retry: one walk answers them all, when
    // the delay elapses.
    [TestMethod]
    public void QueueDriveRebuild_PlainRequestsWhileARetryWaits_RunOnceWhenTheDelayElapses()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();
        ReachWaitingRetry(rig, drive);

        rig.Maintenance.QueueDriveRebuild(drive);
        rig.Maintenance.QueueDriveRebuild(drive);
        Assert.AreEqual(2, rig.Work.Count);
        Assert.IsFalse(rig.Maintenance.HasPendingRebuilds);
        rig.Time.Advance(FirstRetryDelay);
        rig.Work.WaitForCount(3);
        rig.Work.Run(2);

        Assert.AreEqual(3, rig.Work.Count);
    }

    // A removal request keeps the waiting retry, like a Stop: the removal may never resolve (a volume lock),
    // and nothing else would walk the drive then. If the drive does leave, the retry fails fast and parks it.
    [TestMethod]
    public void RemovalRequest_WhileARetryWaits_KeepsIt()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();
        ReachWaitingRetry(rig, drive);

        Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive));
        rig.Time.Advance(FirstRetryDelay);

        rig.Work.WaitForCount(3);
    }

    // The request a walk was started FOR is used up when it starts: by a waiting retry's own walk, by a manual
    // rebuild that superseded the retry, by a monitor's request that found the drive idle, or by a status
    // refresh restoring a pending or re-selected drive. A Stop of that walk used to leave nothing owed, and the
    // drive kept serving an index missing what the monitor lost, or nothing at all.
    [TestMethod]
    [DataRow("retry")]
    [DataRow("forced")]
    [DataRow("idle")]
    [DataRow("status")]
    public void Stop_DuringTheWalkThatRecoversALoss_KeepsTheLossOwed(string walk)
    {
        var drive = MountedDrive();
        using var rig = new Rig(() => Selecting(drive));
        int index;
        if (walk is "idle" or "status")
        {
            if (walk == "idle")
                rig.Maintenance.QueueDriveRebuild(drive); // a monitor's loss, drive idle: walked at once
            else
                Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false)); // RefreshDrivesInStatus
            Assert.AreEqual(1, rig.Work.Count, "precondition: the recovery walk started");
            index = 0;
        }
        else
        {
            ReachWaitingRetryWithoutReadingTheVolume(rig, drive);
            if (walk == "retry")
                rig.Time.Advance(FirstRetryDelay); // the retry's own walk starts
            else
                Assert.IsTrue(rig.Maintenance.RebuildDriveIndex(drive)); // a manual rebuild supersedes it
            rig.Work.WaitForCount(2);
            index = 1;
        }

        Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive)); // the user's Stop
        rig.Work.Run(index);

        Assert.IsFalse(rig.Maintenance.HasPendingRebuilds);
        Assert.IsTrue(rig.Maintenance.HasDelayedRebuild(drive), "the Stop discharged the loss the stopped walk was recovering");
    }

    // A failed removal's recovery (query-remove-failed -> QueueDriveRebuildAfterRemoval) is a forced rebuild,
    // on an idle drive or as the forced follow-up of the rebuild the query-remove cancelled. The query-remove
    // already released the monitor and dropped the index, so this walk is the drive's only recovery: a Stop
    // of it owes it again, like any other recovery walk.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Stop_DuringAFailedRemovalsForcedRecovery_KeepsTheLossOwed(bool asFollowUp)
    {
        var drive = MountedDrive();
        using var rig = new Rig(() => Selecting(drive));
        var index = 0;
        if (asFollowUp)
        {
            Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false)); // a status refresh's restore
            Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive));                     // the query-remove cancels it
            rig.Maintenance.QueueDriveRebuildAfterRemoval(drive);                         // the removal failed
            rig.Work.Run(0);
            Assert.AreEqual(2, rig.Work.Count, "precondition: the forced recovery runs after the cancelled rebuild");
            index = 1;
        }
        else
        {
            rig.Maintenance.QueueDriveRebuildAfterRemoval(drive); // the removal failed, drive idle
            Assert.AreEqual(1, rig.Work.Count, "precondition: the forced recovery started");
        }

        Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive)); // the user's Stop
        rig.Work.Run(index);

        Assert.IsFalse(rig.Maintenance.HasPendingRebuilds);
        Assert.IsTrue(rig.Maintenance.HasDelayedRebuild(drive), "the Stop discharged a failed removal's recovery");
    }

    // A manual rebuild of an idle drive recovers no loss, so once the user stops it nothing is owed: no retry
    // walks the drive again behind their back.
    [TestMethod]
    public void Stop_DuringAManualRebuild_LeavesNoRetry()
    {
        var drive = MountedDrive();
        using var rig = new Rig(() => Selecting(drive));
        Assert.IsTrue(rig.Maintenance.RebuildDriveIndex(drive));
        Assert.AreEqual(1, rig.Work.Count, "precondition: the manual rebuild started");

        Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive));
        rig.Work.Run(0);

        Assert.IsFalse(rig.Maintenance.HasPendingRebuilds);
        Assert.IsFalse(rig.Maintenance.HasDelayedRebuild(drive), "a Stop of a manual rebuild left a retry");
    }

    // A volume lock (chkdsk /f, a format or imaging tool) reaches the drive's removal monitor as a removal
    // request, which cancels the running recovery walk, and its unlock produces neither a failed nor a
    // completed removal. What the walk owed is kept as a waiting retry, as after a Stop, rather than left to a
    // removal recovery that never comes: the drive is walked again once the retry's wait is over. This covers
    // only the walk's own obligation. The lock request also releases the monitor and drops the index, and the
    // unlock restores neither, so a lock of an idle drive, or one that stops a manual rebuild, still leaves
    // the drive unindexed until the next rebuild or startup (as on main; #577).
    [TestMethod]
    public void VolumeLockThenUnlock_DuringARecoveryWalk_StillRecoversTheDrive()
    {
        var drive = MountedDrive();
        using var rig = new Rig(() => Selecting(drive));
        var registration = new DriveMonitorRegistration(new Nothing(), null);
        var removal = DriveMonitorFactory.CreateRemovalMonitor(rig.Indexer, drive, registration, rig.Lifetime.Token,
            rig.Maintenance.QueueDriveRebuild, d => rig.Maintenance.CancelDriveRebuild(d),
            d => rig.Maintenance.QueueDriveRebuildAfterRemoval(d));
        Assert.IsTrue(registration.ReplaceRemovalMonitor(removal));
        rig.Indexer.RegisterDriveMonitor(drive, registration);

        rig.Maintenance.QueueDriveRebuild(drive); // a monitor's loss, drive idle: the recovery walk
        Assert.AreEqual(1, rig.Work.Count, "precondition: the recovery walk started");
        CustomEvent(removal, VolumeLock);   // another tool locks the volume: a removal request
        CustomEvent(removal, VolumeUnlock); // and releases it: no failed or completed removal follows
        rig.Work.Run(0);                    // the cancelled walk settles
        rig.Time.Advance(FirstRetryDelay * 2);

        rig.Work.WaitForCount(2);
        Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive)); // so the retry's walk never reads the volume
    }

    private static readonly Guid VolumeLock = new("50708874-c9af-11d1-8fef-00a0c9a06d32");   // GUID_IO_VOLUME_LOCK
    private static readonly Guid VolumeUnlock = new("9a8c3d68-d0cb-11d1-8fef-00a0c9a06d32"); // GUID_IO_VOLUME_UNLOCK

    private static void CustomEvent(DriveDeviceRemovalMonitor monitor, Guid guid)
    {
        var data = System.Runtime.InteropServices.Marshal.AllocHGlobal(24);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(guid, IntPtr.Add(data, 8), false);
            monitor.HandleNotification(IntPtr.Zero, IntPtr.Zero, DeviceNotificationNative.DeviceCustomEvent, data, 24u);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(data);
        }
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }

    // A per-drive rebuild that a full rebuild cancelled settles after the full rebuild installed its new
    // lifetime token. Its quiet window is stamped with the token it started under, so it ends with that full
    // rebuild too.
    [TestMethod]
    public void QueueDriveRebuild_AfterAFullRebuildThatCancelledTheLastRebuild_DoesNotInheritTheQuietWindow()
    {
        var drive = FreeDriveLetter();
        var generation = new CancellationTokenSource();
        using var rig = new Rig(token: () => generation.Token);
        try
        {
            rig.Maintenance.QueueDriveRebuild(drive);   // per-drive rebuild, generation 1
            generation.Cancel();                        // a full rebuild starts, cancelling it...
            generation = new CancellationTokenSource(); // ...under a new lifetime token
            rig.Work.Run(0);                            // the cancelled rebuild settles

            rig.Maintenance.QueueDriveRebuild(drive);   // the full rebuild's request for the drive

            Assert.AreEqual(2, rig.Work.Count, "the request waited out a backoff from before the full rebuild");
        }
        finally
        {
            generation.Cancel();
        }
    }

    // A full rebuild walked the drive afresh, so the wait left by its last per-drive rebuild no longer applies:
    // the first request after the startup (the initializer's own recovery, say) runs at once.
    [TestMethod]
    public void QueueDriveRebuild_AfterAFullRebuild_DoesNotInheritTheQuietWindow()
    {
        var drive = FreeDriveLetter();
        var generation = new CancellationTokenSource();
        using var rig = new Rig(token: () => generation.Token);
        try
        {
            rig.Maintenance.QueueDriveRebuild(drive);
            rig.Work.Run(0); // ends quietly
            generation.Cancel();
            generation = new CancellationTokenSource(); // a full rebuild ran

            rig.Maintenance.QueueDriveRebuild(drive);

            Assert.AreEqual(2, rig.Work.Count, "the request waited out a backoff from before the full rebuild");
        }
        finally
        {
            generation.Cancel();
        }
    }

    // A plain request raised before a Stop is a recovery obligation, so it survives as a waiting retry, but it
    // never restarts the walk the user stopped at once, even behind a forced rebuild.
    [TestMethod]
    public void QueueDriveRebuild_RequestedBeforeAStop_DoesNotRerun()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();

        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: true));
        rig.Maintenance.QueueDriveRebuild(drive);
        Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive));
        rig.Work.Run(0);

        Assert.AreEqual(1, rig.Work.Count);
        Assert.IsFalse(rig.Maintenance.HasPendingRebuilds);
    }

    // A busy FAT32/exFAT drive keeps asking during each follow-up walk. A user's Stop landing while that walk
    // runs keeps the loss as a waiting retry (QueueDriveRebuild_RaisedAroundAStop_IsKeptAsAWaitingRetry); the
    // same click landing a moment after the walk ended found the waiting retry instead, and discarded it.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Stop_JustBeforeOrJustAfterTheWalkEnded_KeepsTheLossAsAWaitingRetry(bool stopAfterTheWalkEnded)
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();
        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false));
        rig.Maintenance.QueueDriveRebuild(drive); // the monitor loses events during walk 0
        rig.Work.Run(0);
        Assert.AreEqual(2, rig.Work.Count, "precondition: the first follow-up runs at once");
        rig.Maintenance.QueueDriveRebuild(drive); // and again during walk 1

        if (!stopAfterTheWalkEnded)
            Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive));
        rig.Work.Run(1);
        if (stopAfterTheWalkEnded)
            Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive));

        Assert.AreEqual(2, rig.Work.Count, "the walk the user stopped started over at once");
        Assert.IsTrue(IsWaiting(rig, drive), "the Stop discarded the loss the waiting retry owed");
        rig.Time.Advance(FirstRetryDelay * 2);
        rig.Work.WaitForCount(3);
    }

    // A startup (a settings save, a client's InitializeOrLoadIndex) cancels the lifetime token and publishes a
    // new one, then walks every drive. A per-drive rebuild that carried a follow-up for a loss raised BEFORE
    // the startup unwinds once the new token is published, and its retry, linked to the new token, survived
    // the startup to walk the drive again. A loss raised after the startup began must keep its retry:
    // QueueDriveRebuild_RaisedAfterAFullRebuildWhileTheCancelledRebuildUnwinds_IsNotDropped.
    [TestMethod]
    public void StartupInterruptsARebuildCarryingAnOlderLoss_LeavesNoRetryForTheStartupToRepeat()
    {
        var drive = FreeDriveLetter();
        var generation = new CancellationTokenSource();
        var rebuilding = false;
        using var rig = new Rig(isRebuilding: () => rebuilding, token: () => generation.Token);
        try
        {
            Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false)); // per-drive rebuild
            rig.Maintenance.QueueDriveRebuild(drive);                                     // a loss before the startup
            rebuilding = true;                                                            // InitializeOrLoadIndex
            generation.Cancel();
            generation = new CancellationTokenSource();
            rig.Work.Run(0);                                                              // the stale rebuild unwinds
            rebuilding = false;                                                           // the startup walked the drive

            Assert.IsFalse(rig.Maintenance.HasDelayedRebuild(drive),
                "a retry survived the startup for a loss raised before it: one more whole-drive walk after the startup's own");
        }
        finally
        {
            generation.Cancel();
        }
    }

    // The engine's lifetime token is cancelled by a full rebuild (which covers the drive) and by Dispose.
    [TestMethod]
    public void LifetimeCancelled_WhileARetryWaits_RetryNeverRuns()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();
        ReachWaitingRetry(rig, drive);

        rig.Lifetime.Cancel();
        rig.Time.Advance(FirstRetryDelay);

        Assert.AreEqual(2, rig.Work.Count);
        Assert.IsFalse(IsWaiting(rig, drive));
    }

    // A query-remove cancels the drive's rebuild through the same CancelDriveRebuild a Stop uses. That
    // cancellation must not swallow the recovery a failed removal asks for afterwards.
    [TestMethod]
    public void RemovalFailsAfterItCancelledTheRebuild_ForcedRebuildStillRuns()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();

        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false));
        rig.Maintenance.QueueDriveRebuild(drive);
        Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive)); // query-remove
        rig.Maintenance.QueueDriveRebuildAfterRemoval(drive);     // query-remove-failed
        rig.Work.Run(0);

        Assert.AreEqual(2, rig.Work.Count);
    }

    // A rebuild asked for while the drive was unplugged is replayed by its re-insertion waiter, with the
    // arguments ArmRebuildForReinsertion is handed. If the volume's arrival has already started a restore of
    // that drive, the replay used to be refused as a duplicate. Both rebuilds here are cancelled before they
    // run, so neither reads the volume.
    [TestMethod]
    public void RebuildDriveIndex_ReplayedOnReinsertionWhileTheDriveRestores_RunsForcedAfterIt()
    {
        var drive = MountedDrive();
        using var rig = new Rig(() => Selecting(drive));

        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false)); // the arrival's restore
        rig.Maintenance.RebuildDriveIndex(drive, followUpIfBusy: true);               // the waiter's replay
        rig.Lifetime.Cancel();
        rig.Work.Run(0);

        Assert.AreEqual(2, rig.Work.Count, "the replayed rebuild was refused as a duplicate");
    }

    // A mounted drive removed from configuration has nothing left for its retry to recover; the
    // disabled->enabled edge restores it if it is selected again. The drive is selected again before the delay
    // elapses, so a retry that survived the refresh would show up as a collapsed request.
    [TestMethod]
    public void RefreshDrivesInStatus_DriveNoLongerEnabled_CancelsItsRetry()
    {
        var drive = MountedDrive();
        var selected = true;
        using var rig = new Rig(() => selected ? Selecting(drive) : new MachineSettings());
        ReachWaitingRetryWithoutReadingTheVolume(rig, drive);

        selected = false;
        rig.Maintenance.RefreshDrivesInStatus();
        selected = true;
        rig.Time.Advance(FirstRetryDelay);

        Assert.AreEqual(1, rig.Work.Count, "the cancelled retry rebuilt the drive");
        Assert.IsFalse(IsWaiting(rig, drive));
    }

    // A drive deselected while it rebuilt gets no follow-up, plain or forced: the refresh drops its index, and
    // selecting it again restores it.
    [TestMethod]
    public void RebuildDrive_DriveDeselectedWhileItRebuilt_GetsNoFollowUp()
    {
        var drive = MountedDrive();
        var selected = true;
        using var rig = new Rig(() => selected ? Selecting(drive) : new MachineSettings());

        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false));
        Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive)); // keep it off the volume
        rig.Maintenance.QueueDriveRebuild(drive);                 // a plain follow-up that would survive the Stop
        rig.Maintenance.QueueDriveRebuildAfterRemoval(drive);     // and a forced one
        selected = false;
        rig.Work.Run(0);

        Assert.AreEqual(1, rig.Work.Count);
        Assert.IsFalse(rig.Maintenance.HasPendingRebuilds);
    }

    // Deselected while its retry waited, with no status refresh since to cancel it: when the delay elapses the
    // retry finds the drive deselected and does not rebuild it. The retry's own continuation is what lets the
    // drive go: until it has run, a request folds into the waiting retry; once it has, the next request starts
    // the only rebuild. Had the retry rebuilt the drive, every request would fold into that rebuild instead.
    [TestMethod]
    public void DelayedRetry_DriveDeselectedWhileItWaits_DoesNotRebuildIt()
    {
        var drive = MountedDrive();
        var selected = true;
        using var rig = new Rig(() => selected ? Selecting(drive) : new MachineSettings());
        ReachWaitingRetryWithoutReadingTheVolume(rig, drive);

        selected = false;
        rig.Time.Advance(FirstRetryDelay);

        Assert.IsTrue(SpinWait.SpinUntil(() => !IsWaiting(rig, drive), TimeSpan.FromSeconds(10)),
            "the retry still holds the drive: it is waiting, or it rebuilt the deselected drive");
        Assert.AreEqual(2, rig.Work.Count, "a deselected drive was rebuilt");
    }

    // The reverse: the drive is deselected and re-selected around the moment its retry is scheduled, so the
    // deselection's refresh had nothing to cancel. The re-selection's refresh restores the drive at once
    // instead of folding into a retry that may be minutes away.
    [TestMethod]
    public void RefreshDrivesInStatus_DriveReselectedWhileARetryWaits_RestoresItAtOnce()
    {
        var drive = MountedDrive();
        var selected = true;
        using var rig = new Rig(() => selected ? Selecting(drive) : new MachineSettings());
        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false));
        Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive)); // Stop
        rig.Maintenance.QueueDriveRebuild(drive);
        selected = false;
        rig.Maintenance.RefreshDrivesInStatus(); // deselected: no retry exists yet
        selected = true;
        rig.Work.Run(0); // the stopped walk ends and schedules the retry
        Assert.AreEqual(1, rig.Work.Count, "precondition: the retry is waiting");

        rig.Maintenance.RefreshDrivesInStatus(); // the re-selection's refresh

        Assert.AreEqual(2, rig.Work.Count, "the re-selected drive waited on the retry instead of being restored");
    }

    // #569's last-resort catch in FolderDriveMonitor.FlushPending asks for a re-walk when applying a change
    // throws. Raised while the drive's rebuild is queued but its walk recording not yet registered, that
    // request used to be refused as a duplicate. (Once the recording is registered, the request is left to it:
    // see FolderWalkRecorderTests.)
    [TestMethod]
    public void FolderMonitorFailsToApplyAChangeBeforeItsDrivesWalkRecordingRegisters_RerunsAfterIt()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();
        using var asked = new ManualResetEventSlim();

        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false));
        // Bound to the drive the way DriveMonitorFactory.EnsureMonitor binds it; the watcher itself never starts.
        // No walk recorder is registered for the drive, so RequestFolderReindex passes the request straight on.
        using (var monitor = new FolderDriveMonitor(Path.GetTempPath(), (_, _, _) => throw new IOException("The device is not ready."),
                   rig.Lifetime.Token, () => { rig.Indexer.RequestFolderReindex(drive, rig.Maintenance.QueueDriveRebuild); asked.Set(); }))
        {
            monitor.Schedule(WatcherChangeTypes.Created, Path.Combine(Path.GetTempPath(), "new-folder"));
            Assert.IsTrue(asked.Wait(TimeSpan.FromSeconds(10)), "precondition: the monitor asked for a re-walk");
        }
        rig.Work.Run(0);

        Assert.AreEqual(2, rig.Work.Count, "the monitor's request was neither run nor turned into a rerun");
    }

    // The pending check and the Add used to run under two lock acquisitions. A queue landing between them won
    // the Add, and this request fell into the duplicate branch without being recorded. The gap is held open
    // through isRebuilding, the only callback that runs before the Add. It is re-entrant on this thread, which
    // reaches the same state as another thread queueing between the two locks.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void QueueDriveRebuild_AnotherQueueWinsBetweenCheckAndAdd_StillRerunsAfterIt(bool afterRemoval)
    {
        var drive = FreeDriveLetter();
        Rig? rig = null;
        var interleaved = false;
        using var created = rig = new Rig(isRebuilding: () =>
        {
            if (!interleaved)
            {
                interleaved = true;
                Assert.IsTrue(rig!.Maintenance.QueueDriveRebuild(drive, forceRebuild: false), "the competing queue");
            }
            return false;
        });

        if (afterRemoval)
            rig.Maintenance.QueueDriveRebuildAfterRemoval(drive);
        else
            rig.Maintenance.QueueDriveRebuild(drive); // the monitor's request
        Assert.AreEqual(1, rig.Work.Count, "precondition: the competing rebuild is the only one queued");
        rig.Work.Run(0);

        Assert.AreEqual(2, rig.Work.Count, "the request was neither run nor turned into a follow-up");
    }

    // A mounted drive, so a cancelled rebuild settles as "cached" rather than arming a re-insertion waiter that
    // would cover the request by other means. Every rebuild of it is cancelled before it runs.
    private static string MountedDrive()
    {
        var drive = VolumeHelper.DetectIndexableLocalDrives().FirstOrDefault(d => VolumeHelper.GetVolumeId(d) != null);
        if (drive == null)
            Assert.Inconclusive("No mounted drive with a volume id.");
        return drive;
    }

    // A full rebuild cancels the lifetime token, which cancels a per-drive rebuild that is still unwinding
    // (TryLoadDriveFromCache, say, takes no token). The full rebuild completes and then asks for this drive:
    // the initializer's flushed "ended initialization with no monitor" request, or a request from the new
    // monitor it started. The stale rebuild is still pending, so the request becomes its follow-up, and must
    // not be discarded with the cancelled walk: it waits as a retry.
    [TestMethod]
    public void QueueDriveRebuild_RaisedAfterAFullRebuildWhileTheCancelledRebuildUnwinds_IsNotDropped()
    {
        var drive = MountedDrive();
        var generation = new CancellationTokenSource();
        using var rig = new Rig(() => Selecting(drive), token: () => generation.Token);

        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false)); // per-drive rebuild, generation 1
        generation.Cancel();                                                          // a full rebuild starts...
        generation = new CancellationTokenSource();                                   // ...and completes
        rig.Maintenance.QueueDriveRebuild(drive);                                     // its request for this drive
        rig.Work.Run(0);                                                              // the stale rebuild unwinds

        Assert.AreEqual(1, rig.Work.Count);
        Assert.IsTrue(IsWaiting(rig, drive), "a request raised during the cancelled rebuild was dropped");
        generation.Cancel(); // so the retry never reads the volume
    }

    // A Stop cancels the walk, not the losses reported to it: a request made before or after the Stop is a
    // monitor's loss (or a recording's), which only a walk after it recovers. It survives the Stop as a
    // waiting retry: the user asked for that walk to stop, so the next one does not start at once.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void QueueDriveRebuild_RaisedAroundAStop_IsKeptAsAWaitingRetry(bool beforeTheStop)
    {
        var drive = MountedDrive();
        using var rig = new Rig(() => Selecting(drive));

        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false));
        if (beforeTheStop)
            rig.Maintenance.QueueDriveRebuild(drive);
        Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive)); // Stop
        if (!beforeTheStop)
            rig.Maintenance.QueueDriveRebuild(drive);
        rig.Work.Run(0);

        Assert.AreEqual(1, rig.Work.Count, "the walk the user stopped started over at once");
        Assert.IsFalse(rig.Maintenance.HasPendingRebuilds);
        Assert.IsTrue(IsWaiting(rig, drive), "the request was dropped with the stopped walk");
    }

    // A per-drive rebuild hands its walk recording over inside RebuildDrive, while the drive is still pending.
    // A recording that lost events asks for one re-walk there, which used to be refused as a duplicate of the
    // very rebuild running it.
    [TestMethod]
    public void PerDriveHandOver_LostRecorder_ReWalkBecomesTheFollowUp()
    {
        using var rig = new Rig();
        var drive = FreeDriveLetter();
        using var build = new CancellationTokenSource();
        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false));
        var recorder = new FolderWalkRecorder(drive, build.Token);
        recorder.MarkLost();
        FolderWalkRecorder.Keep(rig.Indexer, drive, recorder);

        FolderWalkRecorder.Replay(rig.Indexer, drive, build.Token, rig.Maintenance.QueueDriveRebuild); // EnsureMonitor
        rig.Work.Run(0);

        Assert.AreEqual(2, rig.Work.Count, "the hand-over's re-walk was refused as a duplicate of the rebuild running it");
    }

    // SettleFailedBuild raises the re-walk a live folder monitor left to the build from inside RebuildDrive, so
    // the drive is still pending then too. The drive has to be mounted for SettleFailedBuild to raise it, and a
    // mounted drive's rebuild cannot run here (it would read the volume), so that rebuild is stopped before it
    // runs: the follow-up it was asked for survives the Stop as a waiting retry.
    [TestMethod]
    public void SettleFailedBuild_DeferredReWalkRaisedInsideTheFailingRebuild_BecomesTheFollowUp()
    {
        var drive = MountedDrive();
        using var rig = new Rig(() => Selecting(drive));
        rig.Indexer.Status.Drives.Add(new UsnIndexer.DriveIndexStatus { Drive = drive, State = "ready", Enabled = true });
        using var build = new CancellationTokenSource();
        Assert.IsTrue(rig.Maintenance.QueueDriveRebuild(drive, forceRebuild: false));
        FolderWalkRecorder.Keep(rig.Indexer, drive, new FolderWalkRecorder(drive, build.Token));
        rig.Indexer.RequestFolderReindex(drive, _ => Assert.Fail("precondition: the request was left to the rebuild"));

        DriveArrivalRecovery.SettleFailedBuild(rig.Indexer, drive, wasCancelled: false, build.Token, rig.Lifetime.Token,
            null, rig.Maintenance.QueueDriveRebuild);
        Assert.IsTrue(rig.Maintenance.CancelDriveRebuild(drive)); // Stop
        rig.Work.Run(0);

        Assert.AreEqual(1, rig.Work.Count);
        Assert.IsTrue(IsWaiting(rig, drive), "the deferred re-walk was raised into its own pending rebuild and refused");
    }

}
