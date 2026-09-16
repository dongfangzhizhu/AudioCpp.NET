using System.Text.Json;
using AudioCpp.NET;

/// <summary>
/// Index over the upstream model_specs/*.json shipped next to the executable.
///
/// Specs use their own task vocabulary (music / clone / design / …) so both the raw
/// tokens and their canonical equivalents are reported. The optional <c>category</c>
/// field is what the workbench groups model lists by: with 74 families and 225
/// packages a flat list is unusable, and the category is the only grouping the
/// upstream specs actually guarantee across every family.
/// </summary>
internal sealed record ModelMetadata(string? Family, string Category, string Task, string[] Languages, string[] Tasks, string[] CanonicalTasks)
{
    /// <summary>Used when a spec omits <c>category</c> or when a package id is not
    /// present in any spec (a hand-assembled model directory, for instance).</summary>
    internal const string UnknownCategory = "unknown";

    private static readonly Lazy<SpecIndex> LazyIndex = new(SpecIndex.Load);
    private static SpecIndex Index => LazyIndex.Value;

    internal static ModelMetadata Resolve(string? packageId) => Index.Resolve(packageId);

    /// <summary>Category declared by the spec that owns this family.</summary>
    internal static string CategoryOfFamily(string? family) => Index.CategoryOfFamily(family);

    /// <summary>Family declared by the spec that owns this package id.</summary>
    internal static string? FamilyOfPackage(string? packageId) => Index.FamilyOfPackage(packageId);

    private sealed class SpecIndex
    {
        /// <summary>The two built-in VAD loaders are linked into every build and have no
        /// model_spec of their own. Classifying them here keeps them out of "unknown",
        /// which is reserved for genuinely unrecognised families.</summary>
        private static readonly Dictionary<string, string> BuiltinFamilies = new(StringComparer.OrdinalIgnoreCase)
        {
            ["silero_vad"] = "speech_analysis",
            ["marblenet_vad"] = "speech_analysis",
        };

        private readonly Dictionary<string, Entry> _byPackageId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _categoryByFamily = new(StringComparer.OrdinalIgnoreCase);

        private sealed record Entry(string? Family, string Category, string Task, string[] Languages, string[] Tasks, string[] CanonicalTasks);

        private static readonly Entry Unknown = new(null, UnknownCategory, "unknown", [], [], []);

        internal static SpecIndex Load()
        {
            var index = new SpecIndex();
            var root = Path.Combine(AppContext.BaseDirectory, "model_specs");
            if (!Directory.Exists(root)) return index;

            foreach (var path in Directory.EnumerateFiles(root, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(File.ReadAllText(path));
                }
                catch (JsonException)
                {
                    // A malformed spec must not take the whole workbench down; the
                    // affected families simply fall back to the unknown category.
                    continue;
                }

                using (document)
                {
                    var spec = document.RootElement;
                    var family = spec.TryGetProperty("family", out var familyValue) ? familyValue.GetString() : null;
                    var category = spec.TryGetProperty("category", out var categoryValue) && categoryValue.ValueKind == JsonValueKind.String
                        ? categoryValue.GetString() ?? UnknownCategory
                        : UnknownCategory;

                    if (family is not null && !index._categoryByFamily.ContainsKey(family))
                        index._categoryByFamily[family] = category;

                    var tasks = Strings(spec, "tasks");
                    var canonical = tasks.Select(AudioCppTaskKinds.Normalize)
                        .Where(task => task is not null).Select(task => task!).Distinct().ToArray();
                    var task = canonical.Contains(AudioCppTaskKinds.AudioGeneration) ? AudioCppTaskKinds.AudioGeneration
                        : canonical.Contains(AudioCppTaskKinds.Asr) ? AudioCppTaskKinds.Asr
                        : canonical.Contains(AudioCppTaskKinds.Tts) ? AudioCppTaskKinds.Tts
                        : canonical.FirstOrDefault() ?? "unknown";

                    foreach (var package in Packages(spec))
                        index._byPackageId[package] = new Entry(family, category, task, Strings(spec, "languages"), tasks, canonical);
                }
            }

            return index;
        }

        internal ModelMetadata Resolve(string? packageId)
        {
            if (packageId is null || !_byPackageId.TryGetValue(packageId, out var entry)) return Metadata(Unknown);
            return Metadata(entry);
        }

        internal string CategoryOfFamily(string? family)
        {
            if (string.IsNullOrWhiteSpace(family)) return UnknownCategory;
            if (_categoryByFamily.TryGetValue(family, out var category)) return category;
            return BuiltinFamilies.TryGetValue(family, out var builtin) ? builtin : UnknownCategory;
        }

        internal string? FamilyOfPackage(string? packageId) =>
            packageId is not null && _byPackageId.TryGetValue(packageId, out var entry) ? entry.Family : null;

        private static ModelMetadata Metadata(Entry entry) =>
            new(entry.Family, entry.Category, entry.Task, entry.Languages, entry.Tasks, entry.CanonicalTasks);
    }

    private static string[] Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(value => value.GetString() ?? "").ToArray() : [];

    private static IEnumerable<string> Packages(JsonElement spec)
    {
        if (!spec.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array) yield break;
        foreach (var package in packages.EnumerateArray())
        {
            if (package.ValueKind != JsonValueKind.Object) continue;
            if (!package.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            if (id.GetString() is { Length: > 0 } value) yield return value;
        }
    }
}
