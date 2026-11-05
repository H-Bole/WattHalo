using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WattMonitor.Core;

/// <summary>保存当时的计量口径；分钟明细保留90天，旧数据只保留日汇总。</summary>
public sealed class EnergyStore : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection db;
    private readonly CoverageTracker coverage;
    private readonly Dictionary<(long, string), Bucket> pending = [];
    private TelemetrySnapshot? previous;
    private AppSettings? previousSettings;
    private long lastFlush = Stopwatch.GetTimestamp();
    private DateTime lastPrune;
    private bool disposed;
    private bool baselineReset;
    private sealed class Bucket
    {
        public double Component, Estimated, Seconds, Complete;
        public double First = double.PositiveInfinity, Last = double.NegativeInfinity;
        public decimal Cost;
    }

    public EnergyStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS energy_minute (
              start INTEGER NOT NULL, basis TEXT NOT NULL,
              component REAL NOT NULL, estimated REAL NOT NULL, cost TEXT NOT NULL,
              seconds REAL NOT NULL, complete REAL NOT NULL, covered_from REAL NOT NULL, covered_to REAL NOT NULL, PRIMARY KEY(start,basis));
            CREATE TABLE IF NOT EXISTS energy_day (
              start INTEGER NOT NULL, basis TEXT NOT NULL,
              component REAL NOT NULL, estimated REAL NOT NULL, cost TEXT NOT NULL,
              seconds REAL NOT NULL, complete REAL NOT NULL, covered_from REAL NOT NULL, covered_to REAL NOT NULL, PRIMARY KEY(start,basis));
            """;
        cmd.ExecuteNonQuery();
        foreach (var table in new[] { "energy_minute", "energy_day" })
        {
            using var info = db.CreateCommand(); info.CommandText = $"PRAGMA table_info({table})";
            var columns = new HashSet<string>();
            using (var reader = info.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
            if (columns.Contains("covered_from") && columns.Contains("covered_to")) continue;
            using var migration = db.BeginTransaction();
            using var migrate = db.CreateCommand();
            migrate.Transaction = migration;
            migrate.CommandText = (columns.Contains("covered_from") ? "" : $"ALTER TABLE {table} ADD COLUMN covered_from REAL NOT NULL DEFAULT 0;")
                + (columns.Contains("covered_to") ? "" : $"ALTER TABLE {table} ADD COLUMN covered_to REAL NOT NULL DEFAULT 0;")
                + $"UPDATE {table} SET " + string.Join(",", new[]
                {
                    columns.Contains("covered_from") ? null : "covered_from=start",
                    columns.Contains("covered_to") ? null : $"covered_to=start+{(table == "energy_minute" ? 60 : 86400)}"
                }.Where(value => value is not null));
            migrate.ExecuteNonQuery();
            migration.Commit();
        }
        coverage = new CoverageTracker(db);
        using var version = db.CreateCommand(); version.CommandText = "PRAGMA user_version=3"; version.ExecuteNonQuery();
    }

    public void Record(TelemetrySnapshot snapshot, AppSettings settings)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var old = previous;
            var basis = previousSettings;
            previous = snapshot;
            previousSettings = settings;
            var integrated = false;
            var missingPower = false;
            var basisChanged = baselineReset;
            baselineReset = false;
            if (old != null && basis != null)
            {
                var seconds = (snapshot.Timestamp - old.Timestamp).TotalSeconds;
                // A setting change starts a new baseline, avoiding interpolation across incompatible estimates.
                var sameBasis = old.Mode == snapshot.Mode && old.SourceSession == snapshot.SourceSession &&
                    basis.OtherPartsWatts == settings.OtherPartsWatts && basis.PsuEfficiency == settings.PsuEfficiency;
                basisChanged = !sameBasis;
                missingPower = Valid(old.ComponentWatts) == null || Valid(snapshot.ComponentWatts) == null || !old.PowerComplete || !snapshot.PowerComplete;
                if (seconds > 0 && seconds <= Math.Max(5, settings.RefreshSeconds * 1.5) && sameBasis && CoverageTracker.CanIntegrate(old, snapshot))
                    integrated = Integrate(old, snapshot, basis, seconds);
            }
            coverage.Record(snapshot, integrated, missingPower, basisChanged);
            if (Stopwatch.GetElapsedTime(lastFlush).TotalSeconds >= 10 || pending.Count >= 120) FlushLocked();
        }
    }

    public void ResetBaseline()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            baselineReset |= previous is not null;
            previous = null;
            previousSettings = null;
        }
    }

    private static double? Valid(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
    private bool Integrate(TelemetrySnapshot a, TelemetrySnapshot b, AppSettings settings, double duration)
    {
        var c0 = Valid(a.ComponentWatts); var c1 = Valid(b.ComponentWatts);
        var e0 = Valid(a.EstimatedHostWatts); var e1 = Valid(b.EstimatedHostWatts);
        if (c0 == null || c1 == null) return false;
        var tariffs = settings.Tariffs.Where(t => t.YuanPerKwh >= 0).OrderBy(t => t.EffectiveFrom).ToArray();
        var basis = JsonSerializer.Serialize(new { settings.OtherPartsWatts, settings.PsuEfficiency, a.Mode, a.PowerComplete, settings.Tariffs });
        var cursor = a.Timestamp;
        while (cursor < b.Timestamp)
        {
            var minute = cursor.ToUnixTimeSeconds() / 60 * 60;
            var end = DateTimeOffset.FromUnixTimeSeconds(minute + 60);
            if (end > b.Timestamp) end = b.Timestamp;
            var nextTariff = tariffs.FirstOrDefault(t => t.EffectiveFrom > cursor);
            if (nextTariff != null && nextTariff.EffectiveFrom < end) end = nextTariff.EffectiveFrom;
            var price = tariffs.LastOrDefault(t => t.EffectiveFrom <= cursor)?.YuanPerKwh ?? 1m;
            var f0 = (cursor - a.Timestamp).TotalSeconds / duration;
            var f1 = (end - a.Timestamp).TotalSeconds / duration;
            var seconds = (end - cursor).TotalSeconds;
            double Kwh(double x, double y) => ((x + (y - x) * f0) + (x + (y - x) * f1)) * .5 * seconds / 3_600_000;
            var component = Kwh(c0.Value, c1.Value);
            var estimated = e0 != null && e1 != null ? Kwh(e0.Value, e1.Value) : 0;
            var key = (minute, basis);
            if (!pending.TryGetValue(key, out var bucket)) pending[key] = bucket = new();
            bucket.Component += component;
            bucket.Estimated += estimated;
            bucket.Cost += (decimal)estimated * price;
            bucket.Seconds += seconds;
            bucket.First = Math.Min(bucket.First, cursor.ToUnixTimeMilliseconds() / 1000d);
            bucket.Last = Math.Max(bucket.Last, end.ToUnixTimeMilliseconds() / 1000d);
            if (a.PowerComplete && b.PowerComplete) bucket.Complete += seconds;
            cursor = end;
        }
        return true;
    }

    public void Flush() { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); FlushLocked(); } }
    private void FlushLocked()
    {
        using var tx = db.BeginTransaction();
        foreach (var (key, value) in pending) Add("energy_minute", key.Item1, key.Item2, value, tx);
        var today = DateTime.Today;
        var cutoff = new DateTimeOffset(today.AddDays(-90)).ToUnixTimeSeconds();
        coverage.Flush(tx, cutoff);
        if (lastPrune != today || pending.Keys.Any(key => key.Item1 < cutoff))
        {
            // Roll up complete local days, keeping the same accounting boundary as the UI.
            using var read = db.CreateCommand(); read.Transaction = tx;
            read.CommandText = "SELECT start,basis,component,estimated,cost,seconds,complete,covered_from,covered_to FROM energy_minute WHERE start < $cutoff";
            read.Parameters.AddWithValue("$cutoff", cutoff);
            var days = new Dictionary<(long, string), Bucket>();
            using (var rows = read.ExecuteReader())
                while (rows.Read())
                {
                    var localDate = DateTimeOffset.FromUnixTimeSeconds(rows.GetInt64(0)).LocalDateTime.Date;
                    var key = (new DateTimeOffset(localDate).ToUnixTimeSeconds(), rows.GetString(1));
                    if (!days.TryGetValue(key, out var d)) days[key] = d = new();
                    d.Component += rows.GetDouble(2); d.Estimated += rows.GetDouble(3);
                    d.Cost += decimal.Parse(rows.GetString(4), CultureInfo.InvariantCulture);
                    d.Seconds += rows.GetDouble(5); d.Complete += rows.GetDouble(6);
                    d.First = Math.Min(d.First, rows.GetDouble(7)); d.Last = Math.Max(d.Last, rows.GetDouble(8));
                }
            foreach (var (key, value) in days) Add("energy_day", key.Item1, key.Item2, value, tx);
            using var delete = db.CreateCommand(); delete.Transaction = tx;
            delete.CommandText = "DELETE FROM energy_minute WHERE start < $cutoff";
            delete.Parameters.AddWithValue("$cutoff", cutoff); delete.ExecuteNonQuery();
        }
        tx.Commit();
        coverage.Committed();
        pending.Clear(); lastFlush = Stopwatch.GetTimestamp(); lastPrune = today;
    }

    private void Add(string table, long start, string basis, Bucket value, SqliteTransaction tx)
    {
        // Costs are decimal text; avoid SQLite REAL arithmetic for money.
        using var select = db.CreateCommand(); select.Transaction = tx;
        select.CommandText = $"SELECT cost FROM {table} WHERE start=$s AND basis=$b";
        select.Parameters.AddWithValue("$s", start); select.Parameters.AddWithValue("$b", basis);
        var existing = select.ExecuteScalar() as string;
        var cost = value.Cost + (existing == null ? 0 : decimal.Parse(existing, CultureInfo.InvariantCulture));
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {table}(start,basis,component,estimated,cost,seconds,complete,covered_from,covered_to) VALUES($s,$b,$c,$e,$y,$t,$f,$first,$last)
            ON CONFLICT(start,basis) DO UPDATE SET component=component+excluded.component,
            estimated=estimated+excluded.estimated,cost=excluded.cost,seconds=seconds+excluded.seconds,complete=complete+excluded.complete,
            covered_from=MIN(covered_from,excluded.covered_from),covered_to=MAX(covered_to,excluded.covered_to)
            """;
        cmd.Parameters.AddWithValue("$s", start); cmd.Parameters.AddWithValue("$b", basis);
        cmd.Parameters.AddWithValue("$c", value.Component); cmd.Parameters.AddWithValue("$e", value.Estimated);
        cmd.Parameters.AddWithValue("$y", cost.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", value.Seconds); cmd.Parameters.AddWithValue("$f", value.Complete);
        cmd.Parameters.AddWithValue("$first", value.First); cmd.Parameters.AddWithValue("$last", value.Last);
        cmd.ExecuteNonQuery();
    }

    public EnergyTotals GetTotals(DateTimeOffset from, DateTimeOffset to)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (to <= from) return new(0, 0, 0, 0, 0);
            FlushLocked();
            return GetTotalsLocked(from, to);
        }
    }

    private EnergyTotals GetTotalsLocked(DateTimeOffset from, DateTimeOffset to)
    {
            if (to <= from) return new(0, 0, 0, 0, 0);
            var fromSeconds = from.ToUnixTimeMilliseconds() / 1000d;
            var toSeconds = to.ToUnixTimeMilliseconds() / 1000d;
            using var cmd = RangeCommand(fromSeconds, toSeconds);
            using var rows = cmd.ExecuteReader();
            double component = 0, estimated = 0, seconds = 0, complete = 0;
            decimal cost = 0;
            while (rows.Read())
            {
                var fraction = CoverageFraction(rows.GetDouble(1), rows.GetDouble(2), fromSeconds, toSeconds);
                component += rows.GetDouble(3) * fraction;
                estimated += rows.GetDouble(4) * fraction;
                cost += decimal.Parse(rows.GetString(5), CultureInfo.InvariantCulture) * (decimal)fraction;
                seconds += rows.GetDouble(6) * fraction;
                complete += rows.GetDouble(7) * fraction;
            }
            return new(component, estimated, cost, seconds, complete);
    }

    public CoverageSummary GetCoverage(DateTimeOffset from, DateTimeOffset to) => GetCoverageBatch([(from, to)])[0];

    public IReadOnlyList<CoverageSummary> GetCoverageBatch(IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> ranges)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            FlushLocked();
            return ranges.Select(range => coverage.Query(range.From, range.To, GetTotalsLocked(range.From, range.To))).ToArray();
        }
    }

    public SamplingDiagnostics GetSamplingDiagnostics(DateTimeOffset from, DateTimeOffset to)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            FlushLocked();
            return coverage.QueryDiagnostics(from, to);
        }
    }

    public IReadOnlyList<HistoryPoint> GetHistory(DateTimeOffset from, DateTimeOffset to)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (to <= from) return [];
            FlushLocked();
            var fromSeconds = from.ToUnixTimeMilliseconds() / 1000d;
            var toSeconds = to.ToUnixTimeMilliseconds() / 1000d;
            using var cmd = RangeCommand(fromSeconds, toSeconds);
            var result = new SortedDictionary<long, HistoryPoint>();
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                var start = rows.GetInt64(0); var coveredFrom = rows.GetDouble(1); var coveredTo = rows.GetDouble(2);
                // Queries cutting through a stored bucket use its proportional share.
                var fraction = CoverageFraction(coveredFrom, coveredTo, fromSeconds, toSeconds);
                result.TryGetValue(start, out var existing);
                result[start] = new(DateTimeOffset.FromUnixTimeSeconds(start), (existing?.ComponentKwh ?? 0) + rows.GetDouble(3) * fraction,
                    (existing?.EstimatedKwh ?? 0) + rows.GetDouble(4) * fraction,
                    (existing?.CostYuan ?? 0) + decimal.Parse(rows.GetString(5), CultureInfo.InvariantCulture) * (decimal)fraction,
                    (existing?.RecordedSeconds ?? 0) + rows.GetDouble(6) * fraction,
                    (existing?.CompleteSeconds ?? 0) + rows.GetDouble(7) * fraction)
                    { IsDailyAggregate = existing?.IsDailyAggregate == true || rows.GetInt32(8) != 0 };
            }
            return result.Values.ToArray();
        }
    }

    private SqliteCommand RangeCommand(double from, double to)
    {
        var cmd = db.CreateCommand();
        // Bound the leading primary-key column before applying exact coverage clipping.
        // A local day can span 25 hours across a daylight-saving transition.
        cmd.CommandText = """
            SELECT start,covered_from,covered_to,component,estimated,cost,seconds,complete,0 AS is_daily FROM energy_minute
            WHERE start >= $minuteStart AND start < $to AND covered_from < $to AND covered_to > $from
            UNION ALL
            SELECT start,covered_from,covered_to,component,estimated,cost,seconds,complete,1 AS is_daily FROM energy_day
            WHERE start >= $dayStart AND start < $to AND covered_from < $to AND covered_to > $from
            """;
        cmd.Parameters.AddWithValue("$minuteStart", from - 60);
        cmd.Parameters.AddWithValue("$dayStart", from - 90_000);
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);
        return cmd;
    }

    private static double CoverageFraction(double coveredFrom, double coveredTo, double from, double to) =>
        Math.Max(0, Math.Min(coveredTo, to) - Math.Max(coveredFrom, from)) / (coveredTo - coveredFrom);

    public void ExportCsv(string path, DateTimeOffset from, DateTimeOffset to)
    {
        var rows = GetHistory(from, to);
        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".wattmonitor-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                writer.WriteLine("时间UTC,部件电量(kWh),估算整机电量(kWh),估算电费(元),有效采样秒,完整功率秒,功率覆盖状态(按有效采样秒)");
                foreach (var p in rows)
                {
                    var hasCompletePower = p.CompleteSeconds > 0;
                    var status = !hasCompletePower ? "未知" : p.CompleteSeconds < p.RecordedSeconds ? "部分" : "完整";
                    writer.WriteLine(FormattableString.Invariant($"{p.Start:O},{p.ComponentKwh:R},{(hasCompletePower ? p.EstimatedKwh : (double?)null):R},{(hasCompletePower ? p.CostYuan : (decimal?)null)},{p.RecordedSeconds:R},{p.CompleteSeconds:R},{status}"));
                }
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("无法清理导出临时文件：" + ex.Message); }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            try { FlushLocked(); } finally { db.Dispose(); disposed = true; }
        }
    }
}
