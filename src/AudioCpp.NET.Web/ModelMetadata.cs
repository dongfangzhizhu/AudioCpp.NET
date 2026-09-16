using System.Text.Json;
using AudioCpp.NET;

/// <summary>Reads the upstream model_specs/*.json shipped next to the executable.
/// Specs use their own task vocabulary (music / clone / design / …), so both the
/// raw tokens and their canonical equivalents are reported.</summary>
internal sealed record ModelMetadata(string? Family, string Task, string[] Languages, string[] Tasks, string[] CanonicalTasks)
{
    internal static ModelMetadata Resolve(string? packageId)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "model_specs");
        if (packageId is not null && Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.json"))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var spec = document.RootElement;
                if (!spec.TryGetProperty("packages", out var packages) ||
                    !packages.EnumerateArray().Any(p => p.GetProperty("id").GetString() == packageId)) continue;
                var tasks = Strings(spec, "tasks");
                var canonical = tasks.Select(AudioCppTaskKinds.Normalize).Where(task => task is not null).Select(task => task!).Distinct().ToArray();
                var task = canonical.Contains(AudioCppTaskKinds.AudioGeneration) ? AudioCppTaskKinds.AudioGeneration
                    : canonical.Contains(AudioCppTaskKinds.Asr) ? AudioCppTaskKinds.Asr
                    : canonical.Contains(AudioCppTaskKinds.Tts) ? AudioCppTaskKinds.Tts
                    : canonical.FirstOrDefault() ?? "unknown";
                return new(spec.GetProperty("family").GetString(), task, Strings(spec, "languages"), tasks, canonical);
            }
        }
        return new(null, "unknown", [], [], []);
    }

    private static string[] Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(v => v.GetString() ?? "").ToArray() : [];
}
