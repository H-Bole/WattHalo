using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace WattMonitor.Core;

// 只保存可证明的时间段；没有证据的部分始终留作未知。
internal sealed class CoverageTracker
{
    private const int Recorded = 1, Unrecorded = 2, MixedSleep = 3, Aggregate = 4, AggregateConflict = 5;
    private readonly SqliteConnection db;
    private readonly Task<string?> bootMarker = Task.Run(ReadBootMarker);
    private readonly List<Span> pending = [];
    private readonly Dictionary<long, DiagnosticBucket> diagnostics = [];
    private TelemetrySnapshot? previous, first, checkpoint;
    private string? checkpointBoot;
    private long? tailId;
    private double tailEnd;
    private int tailKind;

    private sealed class Span(double start, double end, int kind, double awake = 0, double sleep = 0)
    {
        public double Start = start, End = end, Awake = awake, Sleep = sleep;
        public int Kind = kind;
    }

    private sealed class DiagnosticBucket
    {
        public int Samples, Dropped, MissingPower, ClockChanges, BasisChanges;
        public double Interval, Total, Cpu, Gpu, Process;
    }

    internal CoverageTracker(SqliteConnection db)
    {
        this.db = db;
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS coverage_span (
              id INTEGER PRIMARY KEY, start REAL NOT NULL, end REAL NOT NULL, kind INTEGER NOT NULL,
              awake REAL NOT NULL DEFAULT 0, sleep REAL NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS coverage_span_start ON coverage_span(start);
            CREATE INDEX IF NOT EXISTS coverage_sleep_end ON coverage_span(end) WHERE kind=3;
            CREATE TABLE IF NOT EXISTS coverage_checkpoint (
              id INTEGER PRIMARY KEY CHECK(id=1), time REAL NOT NULL, tick INTEGER NOT NULL,
              unbiased INTEGER NOT NULL, boot TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sampling_minute (
              start INTEGER PRIMARY KEY, samples INTEGER NOT NULL, dropped INTEGER NOT NULL,
              missing_power INTEGER NOT NULL, clock_changes INTEGER NOT NULL, basis_changes INTEGER NOT NULL,
              max_interval REAL NOT NULL, max_total REAL NOT NULL, max_cpu REAL NOT NULL,
              max_gpu REAL NOT NULL, max_process REAL NOT NULL);
            """;
        command.ExecuteNonQuery();
        using var read = db.CreateCommand();
        read.CommandText = "SELECT time,tick,unbiased,boot FROM coverage_checkpoint WHERE id=1";
        using var row = read.ExecuteReader();
        if (row.Read())
        {
            checkpoint = new() { Timestamp = FromSeconds(row.GetDouble(0)), TickCountMilliseconds = row.GetInt64(1), UnbiasedTimeTicks = row.GetInt64(2) };
            checkpointBoot = row.GetString(3);
        }
    }

    internal static bool CanIntegrate(TelemetrySnapshot a, TelemetrySnapshot b) =>
        !HasClocks(a) || !HasClocks(b) || Compare(a, b).Kind == Recorded;

    internal void Record(TelemetrySnapshot snapshot, bool integrated, bool missingPower, bool basisChanged)
    {
        first ??= snapshot;
        RestoreCheckpoint();
        var observed = previous;
        previous = snapshot;
        var minute = snapshot.Timestamp.ToUnixTimeSeconds() / 60 * 60;
        if (!diagnostics.TryGetValue(minute, out var bucket)) diagnostics[minute] = bucket = new();
        bucket.Samples++;
        if (snapshot.Timings is { } timings)
        {
            bucket.Total = Math.Max(bucket.Total, timings.TotalMilliseconds);
            bucket.Cpu = Math.Max(bucket.Cpu, timings.CpuMilliseconds);
            bucket.Gpu = Math.Max(bucket.Gpu, timings.GpuMilliseconds);
            bucket.Process = Math.Max(bucket.Process, timings.ProcessMilliseconds);
        }
        if (observed is null) return;
        var elapsed = (snapshot.Timestamp - observed.Timestamp).TotalSeconds;
        bucket.Interval = Math.Max(bucket.Interval, elapsed);
        if (!integrated) bucket.Dropped++;
        if (missingPower) bucket.MissingPower++;
        if (basisChanged) bucket.BasisChanges++;
        if (!HasClocks(observed) || !HasClocks(snapshot)) return;
        var comparison = Compare(observed, snapshot);
        if (comparison.Kind == 0) { bucket.ClockChanges++; return; }
        if (comparison.Kind == Recorded)
            AddSpan(new(ToSeconds(observed.Timestamp), ToSeconds(snapshot.Timestamp), integrated ? Recorded : Unrecorded));
        else if (!integrated)
            AddSpan(new(ToSeconds(observed.Timestamp), ToSeconds(snapshot.Timestamp), MixedSleep, comparison.Awake, comparison.Sleep));
    }

    private void RestoreCheckpoint()
    {
        if (checkpoint is null || first is null || !bootMarker.IsCompleted) return;
        if (bootMarker.IsCompletedSuccessfully && bootMarker.Result is { } marker && marker == checkpointBoot && HasClocks(first))
        {
            var result = Compare(checkpoint, first);
            if (result.Kind == Recorded)
                AddSpan(new(ToSeconds(checkpoint.Timestamp), ToSeconds(first.Timestamp), Unrecorded));
            else if (result.Kind == MixedSleep)
                AddSpan(new(ToSeconds(checkpoint.Timestamp), ToSeconds(first.Timestamp), MixedSleep, result.Awake, result.Sleep));
        }
        checkpoint = null;
    }

    private static bool HasClocks(TelemetrySnapshot value) => value.TickCountMilliseconds.HasValue && value.UnbiasedTimeTicks.HasValue;

    private static (int Kind, double Awake, double Sleep) Compare(TelemetrySnapshot a, TelemetrySnapshot b)
    {
        var wall = (b.Timestamp - a.Timestamp).TotalSeconds;
        var tick = (b.TickCountMilliseconds!.Value - a.TickCountMilliseconds!.Value) / 1000d;
        var awake = (b.UnbiasedTimeTicks!.Value - a.UnbiasedTimeTicks!.Value) / 10_000_000d;
        if (wall <= 0 || tick <= 0 || awake < 0 || awake > tick + .25 || Math.Abs(wall - tick) > .25)
            return (0, 0, 0);
        var sleep = Math.Max(0, tick - awake);
        var boundedAwake = Math.Min(wall, awake);
        return sleep <= .25 ? (Recorded, wall, 0) : (MixedSleep, boundedAwake, Math.Min(wall - boundedAwake, sleep));
    }

    private void AddSpan(Span span)
    {
        if (span.End <= span.Start) return;
        if (span.Kind == MixedSleep) { pending.Add(span); return; }
        var start = span.Start;
        while (start < span.End)
        {
            var end = Math.Min(span.End, (Math.Floor(start / 86400) + 1) * 86400);
            if (pending.LastOrDefault() is { } last && last.Kind == span.Kind &&
                Math.Abs(last.End - start) < .001 && SameUtcDay(last.Start, start))
                last.End = end;
            else pending.Add(new(start, end, span.Kind));
            start = end;
        }
    }

    private static bool SameUtcDay(double a, double b) => Math.Floor(a / 86400) == Math.Floor(b / 86400);

    internal void Flush(SqliteTransaction tx, long cutoff)
    {
        RestoreCheckpoint();
        var nextTailId = tailId;
        var nextTailEnd = tailEnd;
        var nextTailKind = tailKind;
        var minutes = new HashSet<long>();
        foreach (var span in pending)
        {
            if (span.Kind != MixedSleep)
            {
                minutes.Add((long)Math.Floor(span.Start / 60) * 60);
                minutes.Add((long)Math.Floor((span.End - .001) / 60) * 60);
            }
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            if (span.Kind != MixedSleep && nextTailId.HasValue && nextTailKind == span.Kind &&
                Math.Abs(nextTailEnd - span.Start) < .001 && SameUtcDay(nextTailEnd - .001, span.Start))
            {
                cmd.CommandText = "UPDATE coverage_span SET end=$end WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", nextTailId.Value);
            }
            else
            {
                cmd.CommandText = "INSERT INTO coverage_span(start,end,kind,awake,sleep) VALUES($start,$end,$kind,$awake,$sleep); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$start", span.Start); cmd.Parameters.AddWithValue("$kind", span.Kind);
                cmd.Parameters.AddWithValue("$awake", span.Awake); cmd.Parameters.AddWithValue("$sleep", span.Sleep);
            }
            cmd.Parameters.AddWithValue("$end", span.End);
            if (cmd.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)) cmd.ExecuteNonQuery();
            else nextTailId = (long)cmd.ExecuteScalar()!;
            nextTailKind = span.Kind; nextTailEnd = span.End;
        }
        foreach (var minute in minutes) CompactMinute(tx, minute, ref nextTailId);
        foreach (var (minute, value) in diagnostics)
        {
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sampling_minute VALUES($start,$samples,$dropped,$missing,$clock,$basis,$interval,$total,$cpu,$gpu,$process)
                ON CONFLICT(start) DO UPDATE SET samples=samples+excluded.samples,dropped=dropped+excluded.dropped,
                missing_power=missing_power+excluded.missing_power,clock_changes=clock_changes+excluded.clock_changes,
                basis_changes=basis_changes+excluded.basis_changes,max_interval=MAX(max_interval,excluded.max_interval),
                max_total=MAX(max_total,excluded.max_total),max_cpu=MAX(max_cpu,excluded.max_cpu),
                max_gpu=MAX(max_gpu,excluded.max_gpu),max_process=MAX(max_process,excluded.max_process)
                """;
            cmd.Parameters.AddWithValue("$start", minute); cmd.Parameters.AddWithValue("$samples", value.Samples);
            cmd.Parameters.AddWithValue("$dropped", value.Dropped); cmd.Parameters.AddWithValue("$missing", value.MissingPower);
            cmd.Parameters.AddWithValue("$clock", value.ClockChanges); cmd.Parameters.AddWithValue("$basis", value.BasisChanges);
            cmd.Parameters.AddWithValue("$interval", value.Interval); cmd.Parameters.AddWithValue("$total", value.Total);
            cmd.Parameters.AddWithValue("$cpu", value.Cpu); cmd.Parameters.AddWithValue("$gpu", value.Gpu);
            cmd.Parameters.AddWithValue("$process", value.Process); cmd.ExecuteNonQuery();
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = (previous is null || checkpoint is not null ? "" : "DELETE FROM coverage_checkpoint WHERE id=1;")
                + "DELETE FROM sampling_minute WHERE start < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff); cmd.ExecuteNonQuery();
        }
        if (previous is { } latest && HasClocks(latest) && bootMarker.IsCompletedSuccessfully && bootMarker.Result is { } boot)
        {
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO coverage_checkpoint VALUES(1,$time,$tick,$unbiased,$boot)";
            cmd.Parameters.AddWithValue("$time", ToSeconds(latest.Timestamp)); cmd.Parameters.AddWithValue("$tick", latest.TickCountMilliseconds!.Value);
            cmd.Parameters.AddWithValue("$unbiased", latest.UnbiasedTimeTicks!.Value); cmd.Parameters.AddWithValue("$boot", boot); cmd.ExecuteNonQuery();
        }
        // State changes are committed only after the enclosing energy transaction commits.
        committedTail = (nextTailId, nextTailEnd, nextTailKind);
    }

    private void CompactMinute(SqliteTransaction tx, long minute, ref long? nextTailId)
    {
        // Aggregate keeps exact recorded/missed totals, but their placement within the minute is unknown.
        var spans = new List<(long Id, double Start, double End, int Kind, double Missed, double Recorded)>();
        using (var read = db.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT id,start,end,kind,awake,sleep FROM coverage_span
                WHERE start >= $start AND start < $end AND end <= $end AND kind IN (1,2,4,5)
                ORDER BY start,end
                """;
            read.Parameters.AddWithValue("$start", minute); read.Parameters.AddWithValue("$end", minute + 60);
            using var rows = read.ExecuteReader();
            while (rows.Read()) spans.Add((rows.GetInt64(0), rows.GetDouble(1), rows.GetDouble(2), rows.GetInt32(3), rows.GetDouble(4), rows.GetDouble(5)));
        }
        if (spans.Count <= 3) return;
        double recorded = 0, missed = 0, lastEnd = double.NegativeInfinity;
        var overlap = false;
        foreach (var span in spans)
        {
            if (span.Start < lastEnd - .001 || span.Kind == AggregateConflict) overlap = true;
            lastEnd = Math.Max(lastEnd, span.End);
            if (span.Kind == Recorded) recorded += span.End - span.Start;
            else if (span.Kind == Unrecorded) missed += span.End - span.Start;
            else { recorded += span.Recorded; missed += span.Missed; }
            if (span.Id == nextTailId) nextTailId = null;
        }
        if (overlap) recorded = missed = 0;
        using var write = db.CreateCommand(); write.Transaction = tx;
        write.CommandText = """
            DELETE FROM coverage_span WHERE start >= $start AND start < $end AND end <= $end AND kind IN (1,2,4,5);
            INSERT INTO coverage_span(start,end,kind,awake,sleep) VALUES($first,$last,$kind,$missed,$recorded)
            """;
        write.Parameters.AddWithValue("$start", minute); write.Parameters.AddWithValue("$end", minute + 60);
        write.Parameters.AddWithValue("$first", spans[0].Start); write.Parameters.AddWithValue("$last", lastEnd);
        write.Parameters.AddWithValue("$kind", overlap ? AggregateConflict : Aggregate);
        write.Parameters.AddWithValue("$missed", missed); write.Parameters.AddWithValue("$recorded", recorded);
        write.ExecuteNonQuery();
    }

    private (long? Id, double End, int Kind) committedTail;
    internal void Committed()
    {
        (tailId, tailEnd, tailKind) = committedTail;
        pending.Clear(); diagnostics.Clear();
    }

    internal CoverageSummary Query(DateTimeOffset from, DateTimeOffset to, EnergyTotals totals)
    {
        if (to <= from) return new(0, 0, 0, 0, 0, CoverageConfidence.Unknown, false);
        var fromSeconds = ToSeconds(from); var toSeconds = ToSeconds(to);
        double recorded = 0, missed = 0, sleep = 0, lastEnd = double.NegativeInfinity;
        var overlap = false;
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT start,end,kind,awake,sleep FROM coverage_span WHERE start >= $bound AND start < $to AND end > $from
            UNION ALL
            SELECT start,end,kind,awake,sleep FROM coverage_span INDEXED BY coverage_sleep_end
            WHERE kind=3 AND start < $bound AND end > $from
            ORDER BY start,end
            """;
        cmd.Parameters.AddWithValue("$bound", fromSeconds - 86400);
        cmd.Parameters.AddWithValue("$from", fromSeconds); cmd.Parameters.AddWithValue("$to", toSeconds);
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            var start = rows.GetDouble(0); var end = rows.GetDouble(1); var kind = rows.GetInt32(2);
            var clippedStart = Math.Max(start, fromSeconds); var clippedEnd = Math.Min(end, toSeconds);
            if (clippedStart < lastEnd - .001) overlap = true;
            if (kind == AggregateConflict) overlap = true;
            lastEnd = Math.Max(lastEnd, clippedEnd);
            var duration = Math.Max(0, clippedEnd - clippedStart);
            if (kind == Recorded) recorded += duration;
            else if (kind == Unrecorded) missed += duration;
            else if (kind == MixedSleep && start >= fromSeconds && end <= toSeconds)
            { missed += rows.GetDouble(3); sleep += rows.GetDouble(4); }
            else if (kind == Aggregate && start >= fromSeconds && end <= toSeconds)
            { missed += rows.GetDouble(3); recorded += rows.GetDouble(4); }
        }
        var legacy = Math.Abs(totals.RecordedSeconds - recorded) > .1;
        var durationSeconds = (to - from).TotalSeconds;
        var timeConflict = overlap || totals.RecordedSeconds > durationSeconds + .001 || totals.CompleteSeconds > durationSeconds + .001;
        var unknown = legacy || timeConflict ? (double?)null : Math.Max(0, durationSeconds - recorded - missed - sleep);
        var confidence = unknown is null ? CoverageConfidence.Partial : unknown.Value >= durationSeconds - .1
            ? CoverageConfidence.Unknown : unknown.Value > .1 ? CoverageConfidence.Partial : CoverageConfidence.Observed;
        if (timeConflict) missed = sleep = 0;
        return new(Math.Min(totals.RecordedSeconds, durationSeconds), Math.Min(totals.CompleteSeconds, durationSeconds), missed, sleep, unknown, confidence, legacy)
            { HasTimeConflict = timeConflict };
    }

    internal SamplingDiagnostics QueryDiagnostics(DateTimeOffset from, DateTimeOffset to)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(samples),0),COALESCE(SUM(dropped),0),COALESCE(SUM(missing_power),0),
            COALESCE(SUM(clock_changes),0),COALESCE(SUM(basis_changes),0),COALESCE(MAX(max_interval),0),
            COALESCE(MAX(max_total),0),COALESCE(MAX(max_cpu),0),COALESCE(MAX(max_gpu),0),COALESCE(MAX(max_process),0)
            FROM sampling_minute WHERE start >= $from AND start < $to
            """;
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds() / 60 * 60);
        cmd.Parameters.AddWithValue("$to", ToSeconds(to));
        using var row = cmd.ExecuteReader(); row.Read();
        return new(row.GetInt32(0), row.GetInt32(1), row.GetInt32(2), row.GetInt32(3), row.GetInt32(4),
            row.GetDouble(5), row.GetDouble(6), row.GetDouble(7), row.GetDouble(8), row.GetDouble(9));
    }

    private static string? ReadBootMarker()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
            var id = key?.GetValue("BootId");
            if (id is null) return null;
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent(TimeSpan.FromSeconds(2));
            if (record?.RecordId is not { } recordId || record.TimeCreated is not { } created) return null;
            var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64());
            if (Math.Abs((new DateTimeOffset(created.ToUniversalTime()) - boot).TotalSeconds) > 10) return null;
            return string.Create(CultureInfo.InvariantCulture, $"{id}:{recordId}:{created.ToUniversalTime().Ticks}");
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        { return null; }
    }

    private static double ToSeconds(DateTimeOffset value) => (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10_000_000d;
    private static DateTimeOffset FromSeconds(double value) => DateTimeOffset.UnixEpoch.AddSeconds(value);
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
}
