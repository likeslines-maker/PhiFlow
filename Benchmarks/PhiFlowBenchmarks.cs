#nullable enable
using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using PhiFlow;

public enum QueryKind
{
    CountGreater,
    RangeCount,
    Sum,
    Avg,
    TopK
}

[MemoryDiagnoser]
public class PhiFlowBenchmarks
{
    [Params(5_000)]
    public int Width { get; set; }

    [Params(60)]
    public int Layers { get; set; }

    [Params(1024)]
    public int DomainSize { get; set; }

    [Params(10, 50)]
    public int KWork { get; set; }

    [Params(200)]
    public int Steps { get; set; }

    [Params(1, 4)]
    public int DeltaCount { get; set; }

    [Params(50)]
    public int QueriesPerStep { get; set; }

    [Params(QueryKind.CountGreater, QueryKind.RangeCount, QueryKind.Sum, QueryKind.Avg, QueryKind.TopK)]
    public QueryKind QueryKind { get; set; }

    // K for TopK queries
    [Params(50)]
    public int TopK { get; set; }

    // -1 => last layer
    [Params(-1)]
    public int QueryLayerParam { get; set; }

    private int QueryLayer => QueryLayerParam < 0 ? Layers - 1 : QueryLayerParam;

    // Pre-generated inputs
    private int[] _initInput = Array.Empty<int>();
    private InputUpdate[] _updates = Array.Empty<InputUpdate>(); // Steps*DeltaCount
    private int[] _qargs = Array.Empty<int>(); // packed; stride depends on QueryKind
    private int _qStride;

    // Runtimes for 4 strategies
    private PhiFlowRuntime _rtA = default!; // baseline full + scan
    private PhiFlowRuntime _rtB = default!; // baseline full + rebuild index + index queries
    private PhiFlowRuntime _rtC = default!; // incremental + scan
    private PhiFlowRuntime _rtD = default!; // incremental + incremental index + index queries

    [GlobalSetup]
    public void GlobalSetup()
    {
        if (Width <= 0 || Layers <= 1) throw new ArgumentOutOfRangeException();
        if (DomainSize <= 1) throw new ArgumentOutOfRangeException(nameof(DomainSize));
        if (QueryLayer < 0 || QueryLayer >= Layers) throw new ArgumentOutOfRangeException(nameof(QueryLayerParam));
        if (TopK < 1) TopK = 1;
        if (TopK > Width) TopK = Width;

        _rtA = MakeRuntime(withIndexes: false);
        _rtB = MakeRuntime(withIndexes: true);
        _rtC = MakeRuntime(withIndexes: false);
        _rtD = MakeRuntime(withIndexes: true);

        var rng = new Random(12345);

        // deterministic input
        _initInput = new int[Width];
        for (int i = 0; i < Width; i++)
            _initInput[i] = rng.Next(DomainSize);

        // deterministic updates (delta = set value)
        _updates = new InputUpdate[Steps * DeltaCount];
        for (int s = 0; s < Steps; s++)
            for (int j = 0; j < DeltaCount; j++)
            {
                int idx = rng.Next(0, Width);
                int val = rng.Next(0, DomainSize);
                _updates[s * DeltaCount + j] = new InputUpdate(idx, val);
            }

        // deterministic queries
        _qStride = QueryKind == QueryKind.RangeCount ? 2 : 1;
        _qargs = new int[Steps * QueriesPerStep * _qStride];

        for (int s = 0; s < Steps; s++)
            for (int q = 0; q < QueriesPerStep; q++)
            {
                int off = (s * QueriesPerStep + q) * _qStride;
        switch (QueryKind)
        {
            case QueryKind.CountGreater:
                _qargs[off] = rng.Next(0, DomainSize);
                break;

            case QueryKind.RangeCount:
                {
                    int lo = rng.Next(0, DomainSize);
                    int hi = rng.Next(lo + 1, DomainSize + 1); // exclusive
                    _qargs[off] = lo;
                    _qargs[off + 1] = hi;
                    break;
                }

            case QueryKind.TopK:
                _qargs[off] = rng.Next(1, TopK + 1);
                break;

            default:
                _qargs[off] = 0;
                break;
        }
    }

    // correctness:all strategies must match checksum
    InitRuntime(_rtA);
    InitRuntime(_rtB);
    InitRuntime(_rtC);
    InitRuntime(_rtD);

    ulong a = RunA(_rtA);
    ulong b = RunB(_rtB);
    ulong c = RunC(_rtC);
    ulong d = RunD(_rtD);

 if (a != b || a != c || a != d)
 throw new InvalidOperationException($"Correctness failed:A={a:X16},B={b:X16},C={c:X16},D={d:X16}");
}

private PhiFlowRuntime MakeRuntime(bool withIndexes)
{
    var rt = new PhiFlowRuntime(Width, Layers, DomainSize);
    rt.Reserve(DeltaCount);

    if (withIndexes)
    {
        // Attach all 3 index types to the query layer.
        // Only those relevant to QueryKind will be used,the rest is for "product demo" flexibility.
        rt.AttachIndex(QueryLayer, new FenwickCountIndex(DomainSize));
        rt.AttachIndex(QueryLayer, new SumIndex(Width));
        rt.AttachIndex(QueryLayer, new HistogramTopKIndex(DomainSize, Width));
    }

    return rt;
}

// ------- Iteration setups (not measured) -------

[IterationSetup(Target = nameof(A_BaselineFull_ScanQueries))]
public void SetupA() => InitRuntime(_rtA);

[IterationSetup(Target = nameof(B_BaselineFull_RebuildIndex_IndexQueries))]
public void SetupB() => InitRuntime(_rtB);

[IterationSetup(Target = nameof(C_Incremental_ScanQueries))]
public void SetupC() => InitRuntime(_rtC);

[IterationSetup(Target = nameof(D_Incremental_IndexQueries))]
public void SetupD() => InitRuntime(_rtD);

private void InitRuntime(PhiFlowRuntime rt)
{
    rt.SetInput(_initInput);
    rt.BuildAll(KWork); // makes state consistent; rebuilds indexes if present
}

// ---------------- Benchmarks ----------------

// A) Full recompute each step + scan queries (no indexes attached)
[Benchmark(Baseline = true)]
public ulong A_BaselineFull_ScanQueries() => RunA(_rtA);

// B) Full recompute each step + rebuild index each step + index queries
[Benchmark]
public ulong B_BaselineFull_RebuildIndex_IndexQueries() => RunB(_rtB);

// C) Incremental recompute each step + scan queries (no indexes attached)
[Benchmark]
public ulong C_Incremental_ScanQueries() => RunC(_rtC);

// D) Incremental recompute each step + incremental index maintenance + index queries
[Benchmark]
public ulong D_Incremental_IndexQueries() => RunD(_rtD);

// ---------------- Strategy implementations ----------------

private ulong RunA(PhiFlowRuntime rt)
{
    ulong cs = 0;
    for (int s = 0; s < Steps; s++)
    {
        var up = GetUpdatesSpan(s);
        ApplyInputOnly(rt, up); // set layer0 only
        rt.RecomputeAll(KWork); // full recompute

        cs = Mix64(cs ^ QueryChecksum(rt, s));
    }
    return cs;
}

private ulong RunB(PhiFlowRuntime rt)
{
    ulong cs = 0;
    for (int s = 0; s < Steps; s++)
    {
        var up = GetUpdatesSpan(s);
        ApplyInputOnly(rt, up);
        rt.RecomputeAll(KWork); // full recompute
        rt.RebuildIndexes(QueryLayer); // rebuild index each step (isolates index benefit)

        cs = Mix64(cs ^ QueryChecksum(rt, s));
    }
    return cs;
}

private ulong RunC(PhiFlowRuntime rt)
{
    ulong cs = 0;
    for (int s = 0; s < Steps; s++)
    {
        var up = GetUpdatesSpan(s);
        rt.ApplyInputUpdates(up, KWork); // incremental recompute
        cs = Mix64(cs ^ QueryChecksum(rt, s));
    }
    return cs;
}

private ulong RunD(PhiFlowRuntime rt)
{
    ulong cs = 0;
    for (int s = 0; s < Steps; s++)
    {
        var up = GetUpdatesSpan(s);
        rt.ApplyInputUpdates(up, KWork); // incremental recompute + incremental index updates
        cs = Mix64(cs ^ QueryChecksum(rt, s));
    }
    return cs;
}

// Update only input layer without recompute (baseline strategies)
private void ApplyInputOnly(PhiFlowRuntime rt, ReadOnlySpan<InputUpdate> updates)
{
    var layer0 = rt.GetLayerSpan(0);
    for (int i = 0; i < updates.Length; i++)
    {
        var u = updates[i];
        if ((uint)u.Index >= (uint)Width) continue;
        layer0[u.Index] = u.Value;
    }
}

// Query checksum:mix each query result so scan vs index must match bitwise
private ulong QueryChecksum(PhiFlowRuntime rt, int step)
{
    var qargs = GetQueryArgsSpan(step);
    ulong cs = 0;

    switch (QueryKind)
    {
        case QueryKind.CountGreater:
            for (int i = 0; i < QueriesPerStep; i++)
            {
                long r = rt.CountGreater(QueryLayer, qargs[i]);
                cs = Mix64(cs ^ (ulong)r);
            }
            return cs;

        case QueryKind.RangeCount:
            for (int i = 0; i < QueriesPerStep; i++)
            {
                int lo = qargs[i * 2];
                int hi = qargs[i * 2 + 1];
                long r = rt.RangeCount(QueryLayer, lo, hi);
                cs = Mix64(cs ^ (ulong)r);
            }
            return cs;

        case QueryKind.Sum:
            {
                long r = rt.Sum(QueryLayer);
                for (int i = 0; i < QueriesPerStep; i++) cs = Mix64(cs ^ (ulong)r);
                return cs;
            }

        case QueryKind.Avg:
            {
                long r = rt.Avg(QueryLayer);
                for (int i = 0; i < QueriesPerStep; i++) cs = Mix64(cs ^ (ulong)r);
                return cs;
            }

        case QueryKind.TopK:
            for (int i = 0; i < QueriesPerStep; i++)
            {
                int k = qargs[i];
                long r = rt.TopKSum(QueryLayer, k);
                cs = Mix64(cs ^ (ulong)r);
            }
            return cs;

        default:
            throw new ArgumentOutOfRangeException();
    }
}

// helper slices
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private ReadOnlySpan<InputUpdate> GetUpdatesSpan(int step)
=> _updates.AsSpan(step * DeltaCount, DeltaCount);

[MethodImpl(MethodImplOptions.AggressiveInlining)]
private ReadOnlySpan<int> GetQueryArgsSpan(int step)
=> _qargs.AsSpan(step * QueriesPerStep * _qStride, QueriesPerStep * _qStride);

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
}
