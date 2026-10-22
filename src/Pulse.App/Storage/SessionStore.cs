using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Pulse.Core.Sessions;

namespace Pulse.App.Storage;

internal sealed record StoredSession(long Id, SessionSummary Summary, bool Complete);

/// <summary>
/// Session history in SQLite (%LocalAppData%\Pulse\pulse.db, WAL). Raw frame times are kept as Brotli-compressed
/// float32 chunks, so any statistic can be recomputed later without loss.
/// </summary>
internal sealed class SessionStore
{
    private readonly string _connectionString;

    public SessionStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sessions(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                exe TEXT NOT NULL,
                started_utc TEXT NOT NULL,
                ended_utc TEXT,
                duration_s REAL, frames INTEGER, avg_fps REAL, low1 REAL, low01 REAL, p99_ms REAL, stutters INTEGER,
                max_cpu_temp REAL, max_gpu_temp REAL, max_gpu_power REAL, max_vram_mb REAL, avg_cpu_load REAL, avg_gpu_load REAL,
                min_pcie_gen REAL, min_pcie_width REAL, pcie_replays INTEGER, whea_events INTEGER, pct_gpu_bound REAL, pct_cpu_bound REAL);
            CREATE TABLE IF NOT EXISTS samples(
                session_id INTEGER NOT NULL, t REAL NOT NULL, fps REAL, cpu_load REAL, cpu_temp REAL, cpu_power REAL,
                gpu_load REAL, gpu_temp REAL, gpu_power REAL, gpu_clock REAL, vram_mb REAL, ram_gb REAL, pcie_gen REAL, pcie_width REAL);
            CREATE INDEX IF NOT EXISTS samples_session ON samples(session_id, t);
            CREATE TABLE IF NOT EXISTS frames(session_id INTEGER NOT NULL, chunk INTEGER NOT NULL, data BLOB NOT NULL, PRIMARY KEY(session_id, chunk));
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static object Db(double v) => double.IsFinite(v) ? v : DBNull.Value;

    public long CreateSession(string exe, DateTime startedUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions(exe, started_utc) VALUES($exe, $start); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$exe", exe);
        cmd.Parameters.AddWithValue("$start", startedUtc.ToString("O"));
        return (long)cmd.ExecuteScalar()!;
    }

    public void Append(long sessionId, IReadOnlyList<SensorSample> samples, int chunk, float[] frames)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (samples.Count > 0)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO samples(session_id, t, fps, cpu_load, cpu_temp, cpu_power, gpu_load, gpu_temp, gpu_power, gpu_clock, vram_mb, ram_gb, pcie_gen, pcie_width)
                VALUES($id, $t, $fps, $cl, $ct, $cp, $gl, $gt, $gp, $gc, $vram, $ram, $gen, $width)
                """;
            string[] names = ["$id", "$t", "$fps", "$cl", "$ct", "$cp", "$gl", "$gt", "$gp", "$gc", "$vram", "$ram", "$gen", "$width"];
            foreach (string n in names)
                cmd.Parameters.Add(new SqliteParameter { ParameterName = n });
            foreach (var s in samples)
            {
                object[] values =
                [
                    sessionId, s.ElapsedSeconds, Db(s.Fps), Db(s.CpuLoad), Db(s.CpuTempC), Db(s.CpuPowerW), Db(s.GpuLoad), Db(s.GpuTempC),
                    Db(s.GpuPowerW), Db(s.GpuClockMhz), Db(s.VramUsedMb), Db(s.RamUsedGb), Db(s.PcieGen), Db(s.PcieWidth),
                ];
                for (int i = 0; i < values.Length; i++)
                    cmd.Parameters[i].Value = values[i];
                cmd.ExecuteNonQuery();
            }
        }
        if (frames.Length > 0)
        {
            Exec(c, "INSERT OR REPLACE INTO frames(session_id, chunk, data) VALUES($id, $chunk, $data)",
                ("$id", sessionId), ("$chunk", chunk), ("$data", Compress(frames)));
        }
        tx.Commit();
    }

    public void Finish(long id, SessionSummary s)
    {
        using var c = Open();
        Exec(c, """
            UPDATE sessions SET ended_utc=$end, duration_s=$dur, frames=$frames, avg_fps=$avg, low1=$low1, low01=$low01, p99_ms=$p99,
                stutters=$stutters, max_cpu_temp=$mct, max_gpu_temp=$mgt, max_gpu_power=$mgp, max_vram_mb=$mvram,
                avg_cpu_load=$acl, avg_gpu_load=$agl, min_pcie_gen=$gen, min_pcie_width=$width, pcie_replays=$replays,
                whea_events=$whea, pct_gpu_bound=$gpub, pct_cpu_bound=$cpub
            WHERE id=$id
            """,
            ("$id", id), ("$end", s.EndedUtc.ToString("O")), ("$dur", s.DurationSeconds), ("$frames", s.Frames), ("$avg", Db(s.AvgFps)),
            ("$low1", Db(s.Low1Fps)), ("$low01", Db(s.Low01Fps)), ("$p99", Db(s.P99FrameTimeMs)), ("$stutters", s.Stutters),
            ("$mct", Db(s.MaxCpuTempC)), ("$mgt", Db(s.MaxGpuTempC)), ("$mgp", Db(s.MaxGpuPowerW)), ("$mvram", Db(s.MaxVramMb)),
            ("$acl", Db(s.AvgCpuLoad)), ("$agl", Db(s.AvgGpuLoad)), ("$gen", Db(s.MinPcieGen)), ("$width", Db(s.MinPcieWidth)),
            ("$replays", s.PcieReplays), ("$whea", s.WheaEvents), ("$gpub", Db(s.PctGpuBound)), ("$cpub", Db(s.PctCpuBound)));
    }

    public void Delete(long id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM frames WHERE session_id=$id", ("$id", id));
        Exec(c, "DELETE FROM samples WHERE session_id=$id", ("$id", id));
        Exec(c, "DELETE FROM sessions WHERE id=$id", ("$id", id));
        tx.Commit();
    }

    public List<StoredSession> List(int limit = 500)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM sessions ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<StoredSession>();
        while (r.Read())
        {
            double D(string col) => r[col] is DBNull ? double.NaN : Convert.ToDouble(r[col], CultureInfo.InvariantCulture);
            long L(string col) => r[col] is DBNull ? 0 : Convert.ToInt64(r[col], CultureInfo.InvariantCulture);
            DateTime started = DateTime.Parse((string)r["started_utc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            bool complete = r["ended_utc"] is not DBNull;
            DateTime ended = complete ? DateTime.Parse((string)r["ended_utc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : started;
            list.Add(new StoredSession(L("id"), new SessionSummary(
                (string)r["exe"], started, ended, D("duration_s"), (int)L("frames"), D("avg_fps"), D("low1"), D("low01"), D("p99_ms"),
                L("stutters"), D("max_cpu_temp"), D("max_gpu_temp"), D("max_gpu_power"), D("max_vram_mb"), D("avg_cpu_load"),
                D("avg_gpu_load"), D("min_pcie_gen"), D("min_pcie_width"), L("pcie_replays"), L("whea_events"), D("pct_gpu_bound"),
                D("pct_cpu_bound")), complete));
        }
        return list;
    }

    public float[] LoadFrames(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM frames WHERE session_id=$id ORDER BY chunk";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        var all = new List<float>();
        while (r.Read())
            all.AddRange(Decompress((byte[])r[0]));
        return [.. all];
    }

    public List<SensorSample> LoadSamples(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT t, fps, cpu_load, cpu_temp, cpu_power, gpu_load, gpu_temp, gpu_power, gpu_clock, vram_mb, ram_gb, pcie_gen, pcie_width FROM samples WHERE session_id=$id ORDER BY t";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        var list = new List<SensorSample>();
        while (r.Read())
        {
            double D(int i) => r.IsDBNull(i) ? double.NaN : r.GetDouble(i);
            list.Add(new SensorSample(D(0), D(1), D(2), D(3), D(4), D(5), D(6), D(7), D(8), D(9), D(10), D(11), D(12), false));
        }
        return list;
    }

    public void ExportCsv(long id, string path)
    {
        var sb = new StringBuilder("t_s,fps,cpu_load,cpu_temp_c,cpu_power_w,gpu_load,gpu_temp_c,gpu_power_w,gpu_clock_mhz,vram_mb,ram_gb,pcie_gen,pcie_width\n");
        foreach (var s in LoadSamples(id))
        {
            sb.AppendJoin(',', new[] { s.ElapsedSeconds, s.Fps, s.CpuLoad, s.CpuTempC, s.CpuPowerW, s.GpuLoad, s.GpuTempC, s.GpuPowerW, s.GpuClockMhz, s.VramUsedMb, s.RamUsedGb, s.PcieGen, s.PcieWidth }
                .Select(v => double.IsFinite(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : ""));
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString());

        string framesPath = Path.ChangeExtension(path, null) + "-frametimes.csv";
        var fb = new StringBuilder("frame,frametime_ms\n");
        float[] frames = LoadFrames(id);
        for (int i = 0; i < frames.Length; i++)
            fb.Append(i).Append(',').Append(frames[i].ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        File.WriteAllText(framesPath, fb.ToString());
    }

    private static byte[] Compress(float[] values)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal))
            brotli.Write(MemoryMarshal.AsBytes(values.AsSpan()));
        return output.ToArray();
    }

    private static float[] Decompress(byte[] data)
    {
        using var input = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return MemoryMarshal.Cast<byte, float>(output.GetBuffer().AsSpan(0, (int)output.Length)).ToArray();
    }
}
