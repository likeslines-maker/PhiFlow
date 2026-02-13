using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PhiFlow;

internal static class Program
{
    static void Main(string[] args)
    {
        // Defaults (safe to demo quickly). You can override via args.
        int width = Arg(args, 0, 5_000);
        int layers = Arg(args, 1, 60);
        int domain = Arg(args, 2, 1024);
        int kWork = Arg(args, 3, 50);
        int ticks = Arg(args, 4, 500); // was 2000 -> too slow without progress
        int deltaCount = Arg(args, 5, 4);
        int queriesPerTick = Arg(args, 6, 50);
        int batchPeriod = Arg(args, 7, 100);
        int queryLayer = layers - 1;

        Console.WriteLine("=== PhiFlow Advantage Demo (Two Modes) ===");
        Console.WriteLine($"width={width:N0},layers={layers},domain={domain},kWork={kWork}");
        Console.WriteLine($"ticks={ticks:N0},deltaCount={deltaCount},queriesPerTick={queriesPerTick},queryLayer={queryLayer}");
        Console.WriteLine($"batchPeriod={batchPeriod} (Mode 2:MV rebuild every N ticks)");
        Console.WriteLine();

        var rng = new Random(12345);

        var initialInput = new int[width];
        for (int i = 0; i < width; i++) initialInput[i] = rng.Next(domain);

        var updatesAll = new InputUpdate[ticks * deltaCount];
        for (int t = 0; t < ticks; t++)
            for (int j = 0; j < deltaCount; j++)
            {
                int idx = rng.Next(0, width);
                int val = rng.Next(0, domain);
                updatesAll[t * deltaCount + j] = new InputUpdate(idx, val);
            }

        var thresholds = new int[ticks * queriesPerTick];
        var rangeLo = new int[ticks * queriesPerTick];
        var rangeHi = new int[ticks * queriesPerTick];
        var topKs = new int[ticks * queriesPerTick];

        for (int t = 0; t < ticks; t++)
            for (int q = 0; q < queriesPerTick; q++)
            {
                int o = t * queriesPerTick + q;
                thresholds[o] = rng.Next(0, domain);
                int lo = rng.Next(0, domain);
                int hi = rng.Next(lo + 1, domain + 1);
                rangeLo[o] = lo;
                rangeHi[o] = hi;
                topKs[o] = rng.Next(1, 51);
            }

        var phi = new DefaultPhi();

        // PhiFlow runtime (incremental + incremental indexes)
        var rt = new PhiFlowRuntime(width, layers, domain);
        rt.Reserve(deltaCount);
        rt.AttachIndex(queryLayer, new FenwickCountIndex(domain));
        rt.AttachIndex(queryLayer, new SumIndex(width));
        rt.AttachIndex(queryLayer, new HistogramTopKIndex(domain, width));

        // Baseline arrays + baseline indexes
        var stateFull = new int[width * layers];
        var idxCount = new FenwickCountIndex(domain);
        var idxSum = new SumIndex(width);
        var idxTopK = new HistogramTopKIndex(domain, width);

        Warmup(phi);

        // -------------------- MODE 1 --------------------
        Console.WriteLine("MODE 1:Per-tick (real-time) comparison");
        Console.WriteLine(" A) Full recompute + scan queries");
        Console.WriteLine(" B) Full recompute + rebuild index each tick + index queries");
        Console.WriteLine(" C) PhiFlow incremental + incremental index queries");
        Console.WriteLine();

        var m1 = RunMode1(
        width, layers, domain, kWork, ticks, deltaCount, queriesPerTick, queryLayer,
        initialInput, updatesAll, thresholds, rangeLo, rangeHi, topKs,
        stateFull, phi, idxCount, idxSum, idxTopK, rt);

        PrintMode1(m1, ticks);

        // -------------------- MODE 2 --------------------
        Console.WriteLine();
        Console.WriteLine("MODE 2:Batch/MV simulation (rebuild index every N ticks)");
        Console.WriteLine(" Queries between rebuilds use a stale index -> fast but not real-time correct.");
        Console.WriteLine();

        var m2 = RunMode2_BatchMV(
        width, layers, domain, kWork, ticks, deltaCount, queriesPerTick, queryLayer, batchPeriod,
        initialInput, updatesAll, thresholds, rangeLo, rangeHi, topKs,
        stateFull, phi, idxCount, idxSum, idxTopK, rt);

        PrintMode2(m2, ticks);

        Console.WriteLine();
        Console.WriteLine("Tip:increase ticks back to 2000 for a stronger demo:");
        Console.WriteLine(" dotnet run -c Release -- 5000 60 1024 50 2000 4 50 100");
    }

    // ============================================================
    // MODE 1
    // ============================================================
    private sealed record Mode1Result(
    TimeSpan TimeA, TimeSpan TimeB, TimeSpan TimeC,
    ulong ChecksumA, ulong ChecksumB, ulong ChecksumC);

    private static Mode1Result RunMode1(
    int width, int layers, int domain, int kWork, int ticks, int deltaCount, int qPerTick, int qLayer,
    int[] initialInput, InputUpdate[] updatesAll, int[] thresholds, int[] rLo, int[] rHi, int[] topKs,
    int[] stateFull, DefaultPhi phi,
    FenwickCountIndex idxCount, SumIndex idxSum, HistogramTopKIndex idxTopK,
    PhiFlowRuntime rt)
    {
        // A) Full + Scan
        Array.Clear(stateFull);
        SetLayer0(stateFull, width, initialInput);
        FullBuildAll(stateFull, width, layers, domain, kWork, phi);

        Console.WriteLine("Running A ...");
        var sw = Stopwatch.StartNew();
        ulong checksumA = RunLoop_FullScan(stateFull, width, layers, domain, kWork, ticks, deltaCount, qPerTick, qLayer,
        updatesAll, thresholds, rLo, rHi, topKs, phi);
        sw.Stop();
        var timeA = sw.Elapsed;
        Console.WriteLine();

        // B) Full + RebuildIndex + IndexQueries
        Array.Clear(stateFull);
        SetLayer0(stateFull, width, initialInput);
        FullBuildAll(stateFull, width, layers, domain, kWork, phi);

        Console.WriteLine("Running B ...");
        sw.Restart();
        ulong checksumB = RunLoop_FullRebuildIndex(
        stateFull, width, layers, domain, kWork, ticks, deltaCount, qPerTick, qLayer,
        updatesAll, thresholds, rLo, rHi, topKs, phi, idxCount, idxSum, idxTopK);
        sw.Stop();
        var timeB = sw.Elapsed;
        Console.WriteLine();

        // C) PhiFlow incremental + incremental indexes
        rt.SetInput(initialInput);
        rt.BuildAll(kWork);

        Console.WriteLine("Running C ...");
        sw.Restart();
        ulong checksumC = RunLoop_PhiFlow(rt, kWork, ticks, deltaCount, qPerTick, qLayer,
        updatesAll, thresholds, rLo, rHi, topKs);
        sw.Stop();
        var timeC = sw.Elapsed;
        Console.WriteLine();

        return new Mode1Result(timeA, timeB, timeC, checksumA, checksumB, checksumC);
    }

    private static void PrintMode1(Mode1Result r, int ticks)
    {
        Console.WriteLine("Correctness (Mode 1):");
        Console.WriteLine($" checksum A:0x{r.ChecksumA:X16}");
        Console.WriteLine($" checksum B:0x{r.ChecksumB:X16}");
        Console.WriteLine($" checksum C:0x{r.ChecksumC:X16}");
        Console.WriteLine(r.ChecksumA == r.ChecksumB && r.ChecksumA == r.ChecksumC ? " OK (all equal)" : " FAIL (mismatch!)");
        Console.WriteLine();

        PrintResult("A) Full + Scan", r.TimeA, ticks);
        PrintResult("B) Full + RebuildIndex each tick + IndexQueries", r.TimeB, ticks);
        PrintResult("C) PhiFlow Incremental + Incremental IndexQueries", r.TimeC, ticks);

        Console.WriteLine();
        Console.WriteLine($"Speedup vs A:x{(r.TimeA.TotalMilliseconds / r.TimeC.TotalMilliseconds):F1}");
        Console.WriteLine($"Speedup vs B:x{(r.TimeB.TotalMilliseconds / r.TimeC.TotalMilliseconds):F1}");
    }

    // ============================================================
    // MODE 2 (Batch MV)
    // ============================================================
    private sealed record Mode2Result(
    TimeSpan TimeBatchMV,
    TimeSpan TimePhiFlow,
    long TotalQueries,
    long MismatchedQueries,
    int BatchPeriod);

    private static Mode2Result RunMode2_BatchMV(
    int width, int layers, int domain, int kWork, int ticks, int deltaCount, int qPerTick, int qLayer, int batchPeriod,
    int[] initialInput, InputUpdate[] updatesAll, int[] thresholds, int[] rLo, int[] rHi, int[] topKs,
    int[] stateFull, DefaultPhi phi,
    FenwickCountIndex idxCount, SumIndex idxSum, HistogramTopKIndex idxTopK,
    PhiFlowRuntime rt)
    {
        // Batch/MV
        Array.Clear(stateFull);
        SetLayer0(stateFull, width, initialInput);
        FullBuildAll(stateFull, width, layers, domain, kWork, phi);

        // initial snapshot
        {
            var layerSpan = GetLayerSpan(stateFull, width, qLayer);
            idxCount.Clear(); idxCount.Build(layerSpan);
            idxSum.Clear(); idxSum.Build(layerSpan);
            idxTopK.Clear(); idxTopK.Build(layerSpan);
        }

        Console.WriteLine("Running Batch/MV ...");
        var swBatch = Stopwatch.StartNew();

        long mismatches = 0;
        long totalQ = 0;

        for (int t = 0; t < ticks; t++)
        {
            Progress(" Batch/MV", t, ticks, swBatch);

            var up = updatesAll.AsSpan(t * deltaCount, deltaCount);
            ApplyUpdatesLayer0(stateFull, width, domain, up);

            FullRecomputeAll(stateFull, width, layers, domain, kWork, phi);

            if (t % batchPeriod == 0)
            {
                var layerSpan = GetLayerSpan(stateFull, width, qLayer);
                idxCount.Clear(); idxCount.Build(layerSpan);
                idxSum.Clear(); idxSum.Build(layerSpan);
                idxTopK.Clear(); idxTopK.Build(layerSpan);
            }

            var layerNow = GetLayerSpan(stateFull, width, qLayer);
            int baseQ = t * qPerTick;

            for (int q = 0; q < qPerTick; q++)
            {
                int o = baseQ + q;

                // stale answers
                long aS = idxCount.CountGreater(thresholds[o]);
                long bS = idxCount.RangeCount(rLo[o], rHi[o]);
                long cS = idxTopK.TopKSum(topKs[o]);
                long dS = idxSum.Sum();

                // true answers
                long aT = ScanCountGreater(layerNow, thresholds[o]);
                long bT = ScanRangeCount(layerNow, rLo[o], rHi[o]);
                long cT = ScanTopKSum(layerNow, domain, topKs[o]);
                long dT = ScanSum(layerNow);

                if (aS != aT || bS != bT || cS != cT || dS != dT) mismatches++;
                totalQ++;
            }
        }
        swBatch.Stop();
        Console.WriteLine("\r Batch/MV:100% done".PadRight(60));

        // PhiFlow reference (real-time exact)
        rt.SetInput(initialInput);
        rt.BuildAll(kWork);

        Console.WriteLine("Running PhiFlow (real-time exact) ...");
        var swPhi = Stopwatch.StartNew();
        for (int t = 0; t < ticks; t++)
        {
            Progress(" PhiFlow", t, ticks, swPhi);

            var up = updatesAll.AsSpan(t * deltaCount, deltaCount);
            rt.ApplyInputUpdates(up, kWork);

            _ = PhiFlowQueryMix(rt, qLayer, qPerTick, t, thresholds, rLo, rHi, topKs); // just to keep comparable load
        }
        swPhi.Stop();
        Console.WriteLine("\r PhiFlow:100% done".PadRight(60));

        return new Mode2Result(swBatch.Elapsed, swPhi.Elapsed, totalQ, mismatches, batchPeriod);
    }

    private static void PrintMode2(Mode2Result r, int ticks)
    {
        PrintResult($"Batch/MV (rebuild every {r.BatchPeriod} ticks)", r.TimeBatchMV, ticks);
        PrintResult("PhiFlow (real-time exact)", r.TimePhiFlow, ticks);

        double mismatchPct = 100.0 * r.MismatchedQueries / Math.Max(1, r.TotalQueries);

        Console.WriteLine();
        Console.WriteLine("Staleness report (Batch/MV):");
        Console.WriteLine($" total queries:{r.TotalQueries:N0}");
        Console.WriteLine($" mismatched queries:{r.MismatchedQueries:N0} ({mismatchPct:F2}%)");
        Console.WriteLine();
        Console.WriteLine($"Speedup (Batch/MV vs PhiFlow):x{(r.TimeBatchMV.TotalMilliseconds / r.TimePhiFlow.TotalMilliseconds):F1}");
    }

    // ============================================================
    // Mode 1 loops
    // ============================================================
    private static ulong RunLoop_FullScan(
    int[] state, int width, int layers, int domain, int kWork, int ticks, int deltaCount, int qPerTick, int qLayer,
    InputUpdate[] updatesAll, int[] thresholds, int[] rLo, int[] rHi, int[] topKs, DefaultPhi phi)
    {
        var sw = Stopwatch.StartNew();
        ulong checksum = 0;

        for (int t = 0; t < ticks; t++)
        {
            Progress(" A", t, ticks, sw);

            var up = updatesAll.AsSpan(t * deltaCount, deltaCount);
            ApplyUpdatesLayer0(state, width, domain, up);

            FullRecomputeAll(state, width, layers, domain, kWork, phi);

            checksum = Mix64(checksum ^ ScanQueryMix(state, width, domain, qLayer, qPerTick, t, thresholds, rLo, rHi, topKs));
        }
        Console.WriteLine("\r A:100% done".PadRight(60));
        return checksum;
    }

    private static ulong RunLoop_FullRebuildIndex(
    int[] state, int width, int layers, int domain, int kWork, int ticks, int deltaCount, int qPerTick, int qLayer,
    InputUpdate[] updatesAll, int[] thresholds, int[] rLo, int[] rHi, int[] topKs, DefaultPhi phi,
    FenwickCountIndex idxCount, SumIndex idxSum, HistogramTopKIndex idxTopK)
    {
        var sw = Stopwatch.StartNew();
        ulong checksum = 0;

        for (int t = 0; t < ticks; t++)
        {
            Progress(" B", t, ticks, sw);

            var up = updatesAll.AsSpan(t * deltaCount, deltaCount);
            ApplyUpdatesLayer0(state, width, domain, up);

            FullRecomputeAll(state, width, layers, domain, kWork, phi);

            var layerSpan = GetLayerSpan(state, width, qLayer);
            idxCount.Clear(); idxCount.Build(layerSpan);
            idxSum.Clear(); idxSum.Build(layerSpan);
            idxTopK.Clear(); idxTopK.Build(layerSpan);

            checksum = Mix64(checksum ^ IndexQueryMix(domain, width, qPerTick, t, thresholds, rLo, rHi, topKs, idxCount, idxSum, idxTopK));
        }
        Console.WriteLine("\r B:100% done".PadRight(60));
        return checksum;
    }

    private static ulong RunLoop_PhiFlow(
    PhiFlowRuntime rt, int kWork, int ticks, int deltaCount, int qPerTick, int qLayer,
    InputUpdate[] updatesAll, int[] thresholds, int[] rLo, int[] rHi, int[] topKs)
    {
        var sw = Stopwatch.StartNew();
        ulong checksum = 0;

        for (int t = 0; t < ticks; t++)
        {
            Progress(" C", t, ticks, sw);

            var up = updatesAll.AsSpan(t * deltaCount, deltaCount);
            rt.ApplyInputUpdates(up, kWork);

            checksum = Mix64(checksum ^ PhiFlowQueryMix(rt, qLayer, qPerTick, t, thresholds, rLo, rHi, topKs));
        }
        Console.WriteLine("\r C:100% done".PadRight(60));
        return checksum;
    }

    // ============================================================
    // Query mixes (shared)
    // ============================================================
    private static ulong ScanQueryMix(
    int[] state, int width, int domain, int queryLayer, int qPerTick, int tick,
    int[] thresholds, int[] rLo, int[] rHi, int[] topKs)
    {
        ulong cs = 0;
        var layer = GetLayerSpan(state, width, queryLayer);

        int baseQ = tick * qPerTick;

        for (int q = 0; q < qPerTick; q++)
        {
            int i = baseQ + q;
            long a = ScanCountGreater(layer, thresholds[i]);
            long b = ScanRangeCount(layer, rLo[i], rHi[i]);
            long c = ScanTopKSum(layer, domain, topKs[i]);
            long d = ScanSum(layer);

            cs = Mix64(cs ^ (ulong)a);
            cs = Mix64(cs ^ (ulong)b);
            cs = Mix64(cs ^ (ulong)c);
            cs = Mix64(cs ^ (ulong)d);
        }
        return cs;
    }

    private static ulong IndexQueryMix(
    int domain, int width, int qPerTick, int tick,
    int[] thresholds, int[] rLo, int[] rHi, int[] topKs,
    FenwickCountIndex idxCount, SumIndex idxSum, HistogramTopKIndex idxTopK)
    {
        ulong cs = 0;
        int baseQ = tick * qPerTick;

        for (int q = 0; q < qPerTick; q++)
        {
            int i = baseQ + q;

            long a = idxCount.CountGreater(thresholds[i]);
            long b = idxCount.RangeCount(rLo[i], rHi[i]);
            long c = idxTopK.TopKSum(topKs[i]);
            long d = idxSum.Sum();

            cs = Mix64(cs ^ (ulong)a);
            cs = Mix64(cs ^ (ulong)b);
            cs = Mix64(cs ^ (ulong)c);
            cs = Mix64(cs ^ (ulong)d);
        }
        return cs;
    }

    private static ulong PhiFlowQueryMix(
    PhiFlowRuntime rt, int queryLayer, int qPerTick, int tick,
    int[] thresholds, int[] rLo, int[] rHi, int[] topKs)
    {
        ulong cs = 0;
        int baseQ = tick * qPerTick;

        for (int q = 0; q < qPerTick; q++)
        {
            int i = baseQ + q;

            long a = rt.CountGreater(queryLayer, thresholds[i]);
            long b = rt.RangeCount(queryLayer, rLo[i], rHi[i]);
            long c = rt.TopKSum(queryLayer, topKs[i]);
            long d = rt.Sum(queryLayer);

            cs = Mix64(cs ^ (ulong)a);
            cs = Mix64(cs ^ (ulong)b);
            cs = Mix64(cs ^ (ulong)c);
            cs = Mix64(cs ^ (ulong)d);
        }
        return cs;
    }

    // ============================================================
    // Baseline compute
    // ============================================================
    private static void FullBuildAll(int[] state, int width, int layers, int domain, int kWork, DefaultPhi phi)
    {
        for (int layer = 1; layer < layers; layer++)
        {
            var prev = GetLayerSpan(state, width, layer - 1);
            var cur = GetLayerSpan(state, width, layer);
            for (int i = 0; i < width; i++)
                cur[i] = phi.FromPrevLayer(prev, i, width, domain, kWork);
        }
    }

    private static void FullRecomputeAll(int[] state, int width, int layers, int domain, int kWork, DefaultPhi phi)
    => FullBuildAll(state, width, layers, domain, kWork, phi);

    private static Span<int> GetLayerSpan(int[] state, int width, int layer)
    => state.AsSpan(layer * width, width);

    private static void SetLayer0(int[] state, int width, int[] input)
    => input.AsSpan().CopyTo(state.AsSpan(0, width));

    private static void ApplyUpdatesLayer0(int[] state, int width, int domain, ReadOnlySpan<InputUpdate> updates)
    {
        var layer0 = state.AsSpan(0, width);
        for (int j = 0; j < updates.Length; j++)
        {
            var u = updates[j];
            if ((uint)u.Index >= (uint)width) continue;
            int v = u.Value;
            if ((uint)v >= (uint)domain) v %= domain;
            layer0[u.Index] = v;
        }
    }

    // ============================================================
    // Scan primitives
    // ============================================================
    private static long ScanCountGreater(ReadOnlySpan<int> layer, int threshold)
    {
        long cnt = 0;
        for (int i = 0; i < layer.Length; i++)
            if (layer[i] > threshold) cnt++;
        return cnt;
    }

    private static long ScanRangeCount(ReadOnlySpan<int> layer, int lo, int hi)
    {
        if (hi < lo) (lo, hi) = (hi, lo);
        long cnt = 0;
        for (int i = 0; i < layer.Length; i++)
        {
            int v = layer[i];
            if ((uint)(v - lo) < (uint)(hi - lo)) cnt++;
        }
        return cnt;
    }

    private static long ScanSum(ReadOnlySpan<int> layer)
    {
        long sum = 0;
        for (int i = 0; i < layer.Length; i++) sum += layer[i];
        return sum;
    }

    private static long ScanTopKSum(ReadOnlySpan<int> layer, int domain, int k)
    {
        if (k < 1) k = 1;
        if (k > layer.Length) k = layer.Length;

        Span<int> hist = domain <= 4096 ? stackalloc int[domain] : new int[domain];
        hist.Clear();
        for (int i = 0; i < layer.Length; i++) hist[layer[i]]++;

        int need = k;
        long sum = 0;
        for (int v = domain - 1; v >= 0 && need > 0; v--)
        {
            int c = hist[v];
            if (c <= 0) continue;
            int take = c < need ? c : need;
            sum += (long)v * take;
            need -= take;
        }
        return sum;
    }

    // ============================================================
    // Progress / utilities
    // ============================================================
    private static void Progress(string label, int t, int ticks, Stopwatch sw)
    {
        // update roughly every ~1%
        int step = Math.Max(1, ticks / 100);
        if (t % step != 0) return;

        double pct = 100.0 * t / ticks;
        double elapsed = sw.Elapsed.TotalSeconds;
        double eta = t > 0 ? elapsed * (ticks - t) / t : double.NaN;

        Console.Write($"\r{label}:{pct,6:F1}% | elapsed {elapsed,6:F1}s | ETA {eta,6:F1}s");
    }

    private static void PrintResult(string name, TimeSpan time, int ticks)
    {
        Console.WriteLine($"{name}");
        Console.WriteLine($" total:{time.TotalMilliseconds:F1} ms");
        Console.WriteLine($" per tick:{time.TotalMilliseconds / ticks:F4} ms");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix64(ulong x)
    {
        unchecked
        {
            x ^= x >> 33;
            x *= 0xff51afd7ed558ccdUL;
            x ^= x >> 33;
            x *= 0xc4ceb9fe1a85ec53UL;
            x ^= x >> 33;
            return x;
        }
    }

    private static void Warmup(DefaultPhi phi)
    {
        int w = 256, l = 16, d = 1024;
        var s = new int[w * l];
        var rnd = new Random(1);
        for (int i = 0; i < w; i++) s[i] = rnd.Next(d);
        FullBuildAll(s, w, l, d, kWork: 10, phi);
    }

    private static int Arg(string[] args, int i, int def)
    => args.Length > i && int.TryParse(args[i], out var v) ? v : def;
}