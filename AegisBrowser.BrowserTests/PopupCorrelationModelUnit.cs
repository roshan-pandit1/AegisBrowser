namespace AegisBrowser.BrowserTests;

// A deliberately synthetic feasibility experiment. It assumes a hypothetical bridge
// delivers every approval and keyed abort, and delivers every successful creation
// with a distinct browser ID but no popup attempt ID. It makes no CEF guarantee.
internal static partial class Program
{
    private enum AttemptState { Pending, Created, Closed, Aborted }

    private sealed record Attempt(AttemptState State, int? Browser = null);

    private sealed class CorrelationModel
    {
        private List<Dictionary<int, Attempt>> worlds = [new()];
        private readonly HashSet<int> usedAttempts = [];
        private readonly HashSet<int> usedBrowsers = [];
        private bool ownerCloseStarted;
        private bool ownerReady;
        private bool ownerClosed;
        private bool invalid;

        public bool Invalid => invalid;
        public bool OwnerClosedObserved => ownerClosed;
        public int Possibilities => worlds.Count;
        public bool MayRelease => !invalid && ownerCloseStarted && ownerReady && worlds.Count != 0 &&
            worlds.All(w => w.Values.All(a => a.State is AttemptState.Closed or AttemptState.Aborted));

        public void Approve(int attempt)
        {
            if (invalid || ownerCloseStarted || !usedAttempts.Add(attempt)) { invalid = true; return; }
            foreach (var world in worlds) world.Add(attempt, new(AttemptState.Pending));
        }

        public void Created(int browser)
        {
            if (invalid || !usedBrowsers.Add(browser)) { invalid = true; return; }
            // An unkeyed creation could belong to any still-pending attempt. Do
            // not assume FIFO, since the actual API does not establish that.
            var next = new List<Dictionary<int, Attempt>>();
            foreach (var world in worlds)
                foreach (var entry in world.Where(e => e.Value.State == AttemptState.Pending))
                {
                    var candidate = new Dictionary<int, Attempt>(world) { [entry.Key] = new(AttemptState.Created, browser) };
                    next.Add(candidate);
                }
            worlds = next;
            if (worlds.Count == 0) invalid = true;
        }

        public void Aborted(int attempt)
        {
            if (invalid) return;
            worlds = worlds.Where(w => w.TryGetValue(attempt, out var a) && a.State == AttemptState.Pending).ToList();
            foreach (var world in worlds) world[attempt] = new(AttemptState.Aborted);
            if (worlds.Count == 0) invalid = true;
        }

        public void Closed(int browser)
        {
            if (invalid) return;
            worlds = worlds.Where(w => w.Values.Any(a => a.State == AttemptState.Created && a.Browser == browser)).ToList();
            foreach (var world in worlds)
            {
                var attempt = world.Single(e => e.Value.State == AttemptState.Created && e.Value.Browser == browser).Key;
                world[attempt] = new(AttemptState.Closed, browser);
            }
            if (worlds.Count == 0) invalid = true;
        }

        public void OwnerStart() => ownerCloseStarted = true;
        public void OwnerReady() => ownerReady = true;
        public void OwnerClosed() => ownerClosed = true;
        // Failure to deliver any callback or loss of its target cannot establish
        // terminal state; an external recovery protocol would be needed.
        public void DeliveryLost() => invalid = true;
    }

    private static int PopupCorrelationModelUnit()
    {
        var start = checks;
        try
        {
            var single = new CorrelationModel();
            single.Approve(1); single.OwnerStart(); single.OwnerReady();
            Check(!single.MayRelease, "pending approval withholds model release");
            single.Created(10);
            Check(!single.MayRelease, "created popup still withholds model release");
            single.Closed(10);
            Check(single.MayRelease, "observed created popup closure resolves single attempt in model");

            var abort = new CorrelationModel();
            abort.Approve(1); abort.OwnerStart(); abort.OwnerReady(); abort.Aborted(1);
            Check(abort.MayRelease, "keyed abortion resolves single attempt in model");
            abort.Aborted(1);
            Check(abort.Invalid && !abort.MayRelease, "duplicate abort invalidates model");

            var ambiguous = new CorrelationModel();
            ambiguous.Approve(1); ambiguous.Approve(2); ambiguous.Created(10);
            Check(ambiguous.Possibilities == 2, "unkeyed creation cannot identify one of two approvals");
            ambiguous.OwnerStart(); ambiguous.OwnerReady();
            Check(!ambiguous.MayRelease, "ambiguous created and pending attempts withhold model release");
            ambiguous.Aborted(1);
            Check(ambiguous.Possibilities == 1 && !ambiguous.MayRelease, "keyed abort disambiguates creation but open popup remains");
            ambiguous.Closed(10);
            Check(ambiguous.MayRelease, "all terminal outcomes and popup closure allow model release");

            var reverse = new CorrelationModel();
            reverse.Approve(1); reverse.Approve(2); reverse.Aborted(2); reverse.OwnerStart(); reverse.Created(10); reverse.OwnerReady();
            Check(reverse.Possibilities == 1 && !reverse.MayRelease, "abort before creation leaves created popup outstanding");
            reverse.Closed(10);
            Check(reverse.MayRelease, "late creation after owner start resolves only on popup close");

            var multi = new CorrelationModel();
            multi.Approve(1); multi.Approve(2); multi.Created(10); multi.Created(20); multi.OwnerStart(); multi.OwnerReady();
            Check(multi.Possibilities == 2 && !multi.MayRelease, "two unkeyed successful creations remain identity-ambiguous");
            multi.Closed(10);
            Check(!multi.MayRelease, "closing one popup does not close another");
            multi.Closed(20);
            Check(multi.MayRelease && multi.Possibilities == 2, "aggregate model can finish with unresolved per-attempt identity");

            var unknown = new CorrelationModel(); unknown.Approve(1); unknown.Aborted(2);
            Check(unknown.Invalid && !unknown.MayRelease, "unknown attempt abort is rejected");
            var duplicateCreated = new CorrelationModel(); duplicateCreated.Approve(1); duplicateCreated.Approve(2); duplicateCreated.Created(10); duplicateCreated.Created(10);
            Check(duplicateCreated.Invalid && !duplicateCreated.MayRelease, "duplicate browser creation is rejected");
            var conflicting = new CorrelationModel(); conflicting.Approve(1); conflicting.Created(10); conflicting.Aborted(1);
            Check(conflicting.Invalid && !conflicting.MayRelease, "conflicting creation and abort is rejected");
            var reused = new CorrelationModel(); reused.Approve(1); reused.Aborted(1); reused.Approve(1);
            Check(reused.Invalid && !reused.MayRelease, "reused attempt identifier is rejected by model");
            var missing = new CorrelationModel(); missing.Approve(1); missing.OwnerStart(); missing.OwnerReady();
            Check(!missing.Invalid && !missing.MayRelease, "missing terminal event remains incomplete");
            var lost = new CorrelationModel(); lost.Approve(1); lost.OwnerStart(); lost.OwnerReady(); lost.DeliveryLost();
            Check(lost.Invalid && !lost.MayRelease, "lost callback target or dispatch is not completion");
            var lateAbort = new CorrelationModel(); lateAbort.Approve(1); lateAbort.OwnerStart(); lateAbort.OwnerReady();
            Check(!lateAbort.MayRelease, "owner readiness does not imply pending abortion");
            lateAbort.OwnerClosed();
            Check(lateAbort.OwnerClosedObserved && !lateAbort.MayRelease, "owner final callback does not resolve pending creation");
            lateAbort.Aborted(1);
            Check(lateAbort.MayRelease, "hypothetical abortion after owner final callback resolves pending attempt");
            var duplicateClose = new CorrelationModel(); duplicateClose.Approve(1); duplicateClose.Created(10); duplicateClose.Closed(10); duplicateClose.Closed(10);
            Check(duplicateClose.Invalid && !duplicateClose.MayRelease, "duplicate close notification is rejected by strict model");
            var unapproved = new CorrelationModel(); unapproved.Created(10);
            Check(unapproved.Invalid && !unapproved.MayRelease, "creation without approval is rejected by model");
            var noOwner = new CorrelationModel(); noOwner.Approve(1); noOwner.Aborted(1);
            Check(!noOwner.MayRelease, "terminal popup outcome does not establish owner readiness");
            var lostThenLate = new CorrelationModel(); lostThenLate.Approve(1); lostThenLate.OwnerStart(); lostThenLate.OwnerReady(); lostThenLate.DeliveryLost(); lostThenLate.Aborted(1);
            Check(lostThenLate.Invalid && !lostThenLate.MayRelease, "later callback cannot repair an untrusted event history");
            var doubleStart = new CorrelationModel(); doubleStart.Approve(1); doubleStart.OwnerStart(); doubleStart.OwnerStart(); doubleStart.Aborted(1); doubleStart.OwnerReady();
            Check(doubleStart.MayRelease, "repeated owner start remains idempotent in model");
            var closeWithoutReady = new CorrelationModel(); closeWithoutReady.Approve(1); closeWithoutReady.Aborted(1); closeWithoutReady.OwnerStart(); closeWithoutReady.OwnerClosed();
            Check(!closeWithoutReady.MayRelease, "owner final callback does not replace readiness evidence");
            var noKnownClose = new CorrelationModel(); noKnownClose.Approve(1); noKnownClose.Created(10); noKnownClose.Closed(20);
            Check(noKnownClose.Invalid && !noKnownClose.MayRelease, "unknown popup closure is rejected");
            var creationAfterOwner = new CorrelationModel(); creationAfterOwner.Approve(1); creationAfterOwner.OwnerStart(); creationAfterOwner.OwnerReady(); creationAfterOwner.OwnerClosed(); creationAfterOwner.Created(10);
            Check(!creationAfterOwner.MayRelease, "hypothetical creation after owner final callback remains outstanding");
            creationAfterOwner.Closed(10);
            Check(creationAfterOwner.MayRelease, "observed late popup closure resolves hypothetical creation");
            Console.WriteLine($"PASS: {checks - start} synthetic popup correlation model checks (hypothetical bridge; not native evidence)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
