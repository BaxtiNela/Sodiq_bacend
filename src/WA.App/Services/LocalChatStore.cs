using System.IO;
using Microsoft.Data.Sqlite;

namespace WA.App.Services;

/// <summary>Suhbat tarixini SQLite da saqlaydi — sessiyalar orasida tiklanadi</summary>
public class LocalChatStore
{
    private readonly string _connStr;

    public LocalChatStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _connStr = $"Data Source={Path.Combine(dataDir, "chat.db")}";
        InitDb();
    }

    private void InitDb()
    {
        try
        {
            using var conn = Open();
            Exec(conn, @"
                CREATE TABLE IF NOT EXISTS sessions (
                    id      TEXT PRIMARY KEY,
                    title   TEXT,
                    last_ts INTEGER DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS messages (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT    NOT NULL,
                    role       TEXT    NOT NULL,
                    text       TEXT    NOT NULL,
                    model      TEXT,
                    ts         INTEGER NOT NULL
                );");
        }
        catch { }
    }

    public void SaveMessage(string sessionId, string role, string text, string? model = null)
    {
        try
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var title = text.Length > 40 ? text[..40] + "…" : text;
            using var conn = Open();

            var s = conn.CreateCommand();
            s.CommandText = @"INSERT OR IGNORE INTO sessions(id,title,last_ts) VALUES($id,$t,$ts);
                              UPDATE sessions SET last_ts=$ts WHERE id=$id;";
            s.Parameters.AddWithValue("$id", sessionId);
            s.Parameters.AddWithValue("$t",  title);
            s.Parameters.AddWithValue("$ts", ts);
            s.ExecuteNonQuery();

            var m = conn.CreateCommand();
            m.CommandText = "INSERT INTO messages(session_id,role,text,model,ts) VALUES($s,$r,$tx,$mo,$ts);";
            m.Parameters.AddWithValue("$s",  sessionId);
            m.Parameters.AddWithValue("$r",  role);
            m.Parameters.AddWithValue("$tx", text);
            m.Parameters.AddWithValue("$mo", model ?? (object)DBNull.Value);
            m.Parameters.AddWithValue("$ts", ts);
            m.ExecuteNonQuery();
        }
        catch { }
    }

    public List<(string Role, string Text, string? Model)> LoadSession(string sessionId)
    {
        var list = new List<(string, string, string?)>();
        try
        {
            using var conn = Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT role,text,model FROM messages WHERE session_id=$s ORDER BY ts,id;";
            cmd.Parameters.AddWithValue("$s", sessionId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }
        catch { }
        return list;
    }

    public string? GetLastSessionId()
    {
        try
        {
            using var conn = Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM sessions ORDER BY last_ts DESC LIMIT 1;";
            return cmd.ExecuteScalar() as string;
        }
        catch { return null; }
    }

    public List<(string Id, string Title, long LastTs)> GetRecentSessions(int n = 30)
    {
        var list = new List<(string, string, long)>();
        try
        {
            using var conn = Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,title,last_ts FROM sessions ORDER BY last_ts DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", n);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.GetString(1), r.GetInt64(2)));
        }
        catch { }
        return list;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
