using System.IO;
using System.Text.Json;
using DevBar.Core;

namespace DevBar.Modules.Jarvis.Memory;

/// <summary>
/// Bulk-loads a profile: { "facts": [{ "text": "...", "pinned": true }],
/// "notes": [{ "name": "...", "title": "...", "path": "file.md" | "content": "..." }] }.
/// Facts are upserted (same text isn't duplicated); notes replace by name.
/// Writes a short report next to the input file.
/// </summary>
internal static class ProfileImport
{
    public static void Run(string jsonPath)
    {
        var report = new List<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;

        if (root.TryGetProperty("facts", out var facts))
            foreach (var f in facts.EnumerateArray())
            {
                var text = f.GetProperty("text").GetString()!;
                long id = MemoryStore.AddFact(text, "told");
                bool pinned = f.TryGetProperty("pinned", out var p) && p.GetBoolean();
                MemoryStore.SetPinned(id, pinned);
                report.Add($"fact {(pinned ? "pinned" : "")} #{id}: {text}");
            }

        if (root.TryGetProperty("notes", out var notes))
            foreach (var n in notes.EnumerateArray())
            {
                var name = n.GetProperty("name").GetString()!;
                var content = n.TryGetProperty("path", out var path)
                    ? File.ReadAllText(Path.IsPathRooted(path.GetString()!) ? path.GetString()! : Path.Combine(Path.GetDirectoryName(jsonPath)!, path.GetString()!))
                    : n.GetProperty("content").GetString()!;
                MemoryStore.SaveNote(name, n.GetProperty("title").GetString()!, content);
                report.Add($"note {name}: {content.Length} chars");
            }

        File.WriteAllLines(Path.ChangeExtension(jsonPath, ".report.txt"), report);
    }
}
