using System;
using System.Collections.Generic;

namespace HMSync.Services;

/// <summary>
/// Relay bandwidth instrumentation (S328ag). Counts bytes and messages crossing the WebSocket in each direction,
/// with a rolling 1-second window for live rate readout plus cumulative session totals. Fed from the two choke
/// points in RelaySyncService (Send → outbound, the receive loop → inbound); read by /hms netdiag.
///
/// Deliberately dependency-free and allocation-light: the hot path just does two integer adds per message. The
/// rolling window is a small ring of per-second buckets so the live KB/s figure doesn't need a timer — it's
/// computed on read from whatever buckets fall inside the last second.
///
/// This measures the WIRE bytes actually handed to/from the socket (post-serialization, and post-compression IF the
/// transport compresses — which is exactly the unknown we want ground truth on). It does NOT model fan-out: a client
/// sees its own upload + the inbound it receives; the relay's fan-out cost is inferred from those, or measured
/// relay-side separately.
/// </summary>
public sealed class NetStatsService
{
    private readonly object gate = new();

    // Cumulative since the last Reset (session totals).
    public long TotalBytesOut { get; private set; }
    public long TotalBytesIn { get; private set; }
    public long TotalMsgsOut { get; private set; }
    public long TotalMsgsIn { get; private set; }

    // Per-message-type breakdown (which channel dominates — transforms vs map-state vs the rest).
    private readonly Dictionary<string, long> bytesOutByType = new();
    private readonly Dictionary<string, long> msgsOutByType = new();

    // Rolling window: 1s of 100ms buckets (10 buckets) for a live rate. Index by tick.
    private const int BucketMs = 100;
    private const int NumBuckets = 10;   // 10 x 100ms = 1s window
    private readonly long[] outBuckets = new long[NumBuckets];
    private readonly long[] inBuckets = new long[NumBuckets];
    private readonly long[] bucketStampMs = new long[NumBuckets];   // which 100ms slot each bucket currently holds

    private DateTime startedUtc = DateTime.UtcNow;

    private static long NowMs() => (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

    private void AddToBucket(long[] buckets, long bytes)
    {
        long slot = NowMs() / BucketMs;
        int idx = (int)(slot % NumBuckets);
        if (bucketStampMs[idx] != slot)   // this bucket is stale (belongs to an older second) — recycle it
        {
            bucketStampMs[idx] = slot;
            outBuckets[idx] = 0;          // reset BOTH directions for this recycled slot so neither carries stale data
            inBuckets[idx] = 0;
        }
        buckets[idx] += bytes;
    }

    public void RecordOut(long bytes, string type)
    {
        lock (gate)
        {
            TotalBytesOut += bytes; TotalMsgsOut++;
            AddToBucket(outBuckets, bytes);
            if (!string.IsNullOrEmpty(type))
            {
                bytesOutByType.TryGetValue(type, out var b); bytesOutByType[type] = b + bytes;
                msgsOutByType.TryGetValue(type, out var m); msgsOutByType[type] = m + 1;
            }
        }
    }

    public void RecordIn(long bytes)
    {
        lock (gate)
        {
            TotalBytesIn += bytes; TotalMsgsIn++;
            AddToBucket(inBuckets, bytes);
        }
    }

    /// <summary>Live rolling rate over the last ~1s window, bytes/sec, each direction.</summary>
    public (double outBps, double inBps) LiveRates()
    {
        lock (gate)
        {
            long slot = NowMs() / BucketMs;
            long outSum = 0, inSum = 0;
            for (int i = 0; i < NumBuckets; i++)
            {
                // Only count buckets whose stamp is within the last NumBuckets slots (i.e. the last second).
                if (slot - bucketStampMs[i] < NumBuckets)
                {
                    outSum += outBuckets[i];
                    inSum += inBuckets[i];
                }
            }
            // Window is 1s, so the sum IS bytes/sec.
            return (outSum, inSum);
        }
    }

    public TimeSpan Elapsed => DateTime.UtcNow - startedUtc;

    public IReadOnlyDictionary<string, long> BytesOutByType { get { lock (gate) return new Dictionary<string, long>(bytesOutByType); } }
    public IReadOnlyDictionary<string, long> MsgsOutByType { get { lock (gate) return new Dictionary<string, long>(msgsOutByType); } }

    public void Reset()
    {
        lock (gate)
        {
            TotalBytesOut = TotalBytesIn = TotalMsgsOut = TotalMsgsIn = 0;
            bytesOutByType.Clear(); msgsOutByType.Clear();
            Array.Clear(outBuckets); Array.Clear(inBuckets); Array.Clear(bucketStampMs);
            startedUtc = DateTime.UtcNow;
        }
    }
}
