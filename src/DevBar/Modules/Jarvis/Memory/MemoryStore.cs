using System.IO;
using DevBar.Core;
using Microsoft.Data.Sqlite;

namespace DevBar.Modules.Jarvis.Memory;

internal sealed record Fact(long Id, string Text, string Source, DateTime Updated, bool Pinned = false);

/// <summary>A longer reference document (a playbook, a resume) Jarvis opens only when relevant.</summary>
internal sealed record Note(string Name, string Title, string Content, DateTime Updated);

internal sealed record Reminder(long Id, DateTime DueLocal, string Text);

/// <summary>
/// Jarvis's long-term memory: %LOCALAPPDATA%\DevBar\jarvis.db, local only.
///   facts      — durable things about the user ("works on ClientPulse", "prefers tea").
///                source = 'told' (you said "remember…") or 'learned' (picked up after a chat).
///   reminders  — survive restarts; one timer is armed for the next one due.
///   turns      — recent conversation lines, the raw material for learning; pruned to 30 days.
///   notes      — reference documents, read on demand (never all pasted into every prompt).
/// Pinned facts are always in the prompt; the rest fill up to a budget, newest first.
/// Every call opens and closes its own connection: usage is a handful of
/// queries per conversation, and nothing stays open while DevBar idles.
/// </summary>
internal static class MemoryStore
{
    private static string DbPath => Path.Combine(Config.Dir, "jarvis.db");
    private static bool _initialized;
    private static readonly object Gate = new();

    private static SqliteConnection Open()
    {
        Directory.CreateDirectory(Config.Dir);
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        lock (Gate)
        {
            if (!_initialized)
            {
                Exec(conn, """
                    CREATE TABLE IF NOT EXISTS facts(
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        text TEXT NOT NULL,
                        source TEXT NOT NULL DEFAULT 'told',
                        created TEXT NOT NULL,
                        updated TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS reminders(
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        due_utc TEXT NOT NULL,
                        text TEXT NOT NULL,
                        done INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE IF NOT EXISTS turns(
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts TEXT NOT NULL,
                        role TEXT NOT NULL,
                        text TEXT NOT NULL,
                        learned INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE IF NOT EXISTS notes(
                        name TEXT PRIMARY KEY,
                        title TEXT NOT NULL,
                        content TEXT NOT NULL,
                        updated TEXT NOT NULL);
                    DELETE FROM turns WHERE ts < datetime('now', '-30 days');
                    """);
                // v2: pinned facts. ALTER fails harmlessly if the column already exists.
                try { Exec(conn, "ALTER TABLE facts ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { }
                _initialized = true;
            }
        }
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql, params (string, object?)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static string Now => DateTime.UtcNow.ToString("o");

    // ---------------- facts ----------------

    public static List<Fact> Facts(int limit = 500)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, text, source, updated, pinned FROM facts ORDER BY pinned DESC, updated DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<Fact>();
        while (r.Read())
            list.Add(new Fact(r.GetInt64(0), r.GetString(1), r.GetString(2), DateTime.Parse(r.GetString(3)).ToLocalTime(), r.GetInt64(4) == 1));
        return list;
    }

    public static void SetPinned(long id, bool pinned)
    {
        using var conn = Open();
        Exec(conn, "UPDATE facts SET pinned=$p WHERE id=$id", ("$p", pinned ? 1 : 0), ("$id", id));
    }

    // ---------------- notes ----------------

    public static void SaveNote(string name, string title, string content)
    {
        using var conn = Open();
        Exec(conn, "INSERT INTO notes(name, title, content, updated) VALUES($n, $t, $c, $u) " +
                   "ON CONFLICT(name) DO UPDATE SET title=$t, content=$c, updated=$u",
            ("$n", name), ("$t", title), ("$c", content), ("$u", Now));
    }

    public static List<Note> Notes()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, title, content, updated FROM notes ORDER BY name";
        using var r = cmd.ExecuteReader();
        var list = new List<Note>();
        while (r.Read()) list.Add(new Note(r.GetString(0), r.GetString(1), r.GetString(2), DateTime.Parse(r.GetString(3)).ToLocalTime()));
        return list;
    }

    public static void DeleteNote(string name)
    {
        using var conn = Open();
        Exec(conn, "DELETE FROM notes WHERE name=$n", ("$n", name));
    }

    public static long AddFact(string text, string source)
    {
        text = text.Trim();
        using var conn = Open();
        // Don't store the same thing twice.
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT id FROM facts WHERE lower(text) = lower($t)";
            check.Parameters.AddWithValue("$t", text);
            if (check.ExecuteScalar() is long existing)
            {
                Exec(conn, "UPDATE facts SET updated=$u WHERE id=$id", ("$u", Now), ("$id", existing));
                return existing;
            }
        }
        Exec(conn, "INSERT INTO facts(text, source, created, updated) VALUES($t, $s, $c, $c)",
            ("$t", text), ("$s", source), ("$c", Now));
        using var id = conn.CreateCommand();
        id.CommandText = "SELECT last_insert_rowid()";
        return (long)id.ExecuteScalar()!;
    }

    public static void UpdateFact(long id, string text)
    {
        using var conn = Open();
        Exec(conn, "UPDATE facts SET text=$t, updated=$u WHERE id=$id", ("$t", text.Trim()), ("$u", Now), ("$id", id));
    }

    public static void DeleteFact(long id)
    {
        using var conn = Open();
        Exec(conn, "DELETE FROM facts WHERE id=$id", ("$id", id));
    }

    public static void ForgetEverything()
    {
        using var conn = Open();
        Exec(conn, "DELETE FROM facts; DELETE FROM turns; DELETE FROM notes;");
    }

    /// <summary>Facts whose text contains any of the query's words, best matches first.</summary>
    public static List<Fact> Search(string query, int limit = 12)
    {
        var words = query.ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', '?', '!', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !StopWords.Contains(w)).Distinct().ToList();
        var all = Facts();
        if (words.Count == 0) return all.Take(limit).ToList();
        return all
            .Select(f => (f, score: words.Count(w => f.Text.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenByDescending(x => x.f.Updated)
            .Take(limit).Select(x => x.f).ToList();
    }

    private static readonly HashSet<string> StopWords = new()
    {
        "the", "and", "what", "who", "where", "when", "how", "does", "did", "you", "know", "about", "my", "me", "is", "are", "was",
        "remember", "tell", "do", "have", "any", "that", "this", "with", "for",
    };

    // ---------------- conversation log (for learning) ----------------

    public static void LogTurn(string role, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        using var conn = Open();
        Exec(conn, "INSERT INTO turns(ts, role, text) VALUES($ts, $r, $t)", ("$ts", Now), ("$r", role), ("$t", text));
    }

    /// <summary>Lines not yet processed by the learner, oldest first, and their max id for <see cref="MarkLearned"/>.</summary>
    public static (List<(string Role, string Text)> Lines, long MaxId) UnlearnedTurns(int limit = 60)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, role, text FROM turns WHERE learned=0 ORDER BY id LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var lines = new List<(string, string)>();
        long max = 0;
        while (r.Read())
        {
            max = r.GetInt64(0);
            lines.Add((r.GetString(1), r.GetString(2)));
        }
        return (lines, max);
    }

    public static void MarkLearned(long maxId)
    {
        using var conn = Open();
        Exec(conn, "UPDATE turns SET learned=1 WHERE id <= $m", ("$m", maxId));
    }

    // ---------------- reminders ----------------

    public static long AddReminder(DateTime dueLocal, string text)
    {
        using var conn = Open();
        Exec(conn, "INSERT INTO reminders(due_utc, text) VALUES($d, $t)", ("$d", dueLocal.ToUniversalTime().ToString("o")), ("$t", text));
        using var id = conn.CreateCommand();
        id.CommandText = "SELECT last_insert_rowid()";
        return (long)id.ExecuteScalar()!;
    }

    public static List<Reminder> PendingReminders()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, due_utc, text FROM reminders WHERE done=0 ORDER BY due_utc";
        using var r = cmd.ExecuteReader();
        var list = new List<Reminder>();
        while (r.Read())
            list.Add(new Reminder(r.GetInt64(0), DateTime.Parse(r.GetString(1)).ToLocalTime(), r.GetString(2)));
        return list;
    }

    public static void CompleteReminder(long id)
    {
        using var conn = Open();
        Exec(conn, "UPDATE reminders SET done=1 WHERE id=$id", ("$id", id));
    }
}
