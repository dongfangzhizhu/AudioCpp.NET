using System.Text.Json;

internal sealed record ModelMetadata(string? Family, string Task, string[] Languages, string[] Tasks)
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
                var task = tasks.Contains("music") ? "music" : tasks.Contains("asr") ? "asr" :
                    tasks.Contains("tts") ? "tts" : tasks.FirstOrDefault() ?? "unknown";
                return new(spec.GetProperty("family").GetString(), task, Strings(spec, "languages"), tasks);
            }
        }
        return new(null, "unknown", [], []);
    }

    private static string[] Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(v => v.GetString() ?? "").ToArray() : [];
}