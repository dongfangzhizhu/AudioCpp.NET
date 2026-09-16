using System.Text.Json;

namespace AudioCpp.NET;

public sealed record ModelValidationIssue(string Kind, string Path, string Detail);

public sealed record ModelValidationReport(string Directory, string? PackageId, bool ManifestPresent,
    int CheckedFiles, ulong CheckedBytes, IReadOnlyList<ModelValidationIssue> Issues)
{
    public bool Complete
    {
        get { return Issues.Count == 0; }
    }
}

/// <summary>Verifies installed model packages against the per-package manifest written
/// by the native package manager (.audiocpp-package-&lt;id&gt;.json). The manifest lists
/// every expected file with its byte size and SHA-256 etag, so an interrupted or resumed
/// download can be detected before inference fails with "missing file".</summary>
public static class ModelValidator
{
    private static readonly string ManifestPrefix = ".audiocpp-package-";
    private static readonly string ManifestSuffix = ".json";

    /// <summary>Validates one model directory. Directories without a manifest are
    /// considered complete unless they are empty; installed packages are checked
    /// against their manifest for missing files and size mismatches.</summary>
    public static ModelValidationReport Validate(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!Directory.Exists(modelPath))
            throw new ArgumentException($"Model path is not a directory: {Path.GetFullPath(modelPath)}", nameof(modelPath));

        var issues = new List<ModelValidationIssue>();
        string? packageId = null;
        var manifestPresent = false;
        var checkedFiles = 0;
        ulong checkedBytes = 0;

        foreach (var manifest in Directory.EnumerateFiles(modelPath))
        {
            var name = Path.GetFileName(manifest);
            if (!name.StartsWith(ManifestPrefix) || !name.EndsWith(ManifestSuffix)) continue;
            var scan = ScanManifest(manifest);
            manifestPresent = true;
            if (string.IsNullOrWhiteSpace(packageId)) packageId = scan.PackageId;
            checkedFiles += scan.CheckedFiles;
            checkedBytes += scan.CheckedBytes;
            foreach (var issue in scan.Issues) issues.Add(issue);
        }

        if (!manifestPresent && IsEmpty(modelPath))
            issues.Add(new ModelValidationIssue("empty", Path.GetFullPath(modelPath), "no package manifest and no model files"));

        return new ModelValidationReport(Path.GetFullPath(modelPath), packageId, manifestPresent, checkedFiles, checkedBytes, issues);
    }

    /// <summary>Human-readable summary of a report's issues, for exception messages
    /// and the console/Web UI.</summary>
    public static string FormatIssues(ModelValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Complete) return "";
        var messages = new List<string>();
        foreach (var issue in report.Issues)
            messages.Add(issue.Kind == "empty"
                ? $"directory is empty ({issue.Path})"
                : $"{issue.Kind}: {issue.Path}{(issue.Detail.Length == 0 ? "" : $" ({issue.Detail})")}");
        return string.Join("; ", messages);
    }

    /// <summary>Locates the directory of an installed package inside a models root,
    /// by looking for its .audiocpp-package-&lt;id&gt;.json manifest.</summary>
    public static string? FindPackageDirectory(string modelsDirectory, string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (!Directory.Exists(modelsDirectory)) return null;
        var manifestName = ManifestPrefix + packageId + ManifestSuffix;
        foreach (var file in Directory.EnumerateFiles(modelsDirectory))
            if (Path.GetFileName(file) == manifestName) return modelsDirectory;
        foreach (var directory in Directory.EnumerateDirectories(modelsDirectory))
            foreach (var file in Directory.EnumerateFiles(directory))
                if (Path.GetFileName(file) == manifestName) return directory;
        return null;
    }

    /// <summary>Enumerates the IDs of all installed packages under a models root.</summary>
    public static IReadOnlyList<string> FindPackageIds(string modelsDirectory)
    {
        var result = new List<string>();
        if (!Directory.Exists(modelsDirectory)) return result;
        foreach (var directory in Directory.EnumerateDirectories(modelsDirectory))
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var id = PackageIdFromManifestName(Path.GetFileName(file));
                if (id is not null && !result.Contains(id)) result.Add(id);
            }
        return result;
    }

    /// <summary>Infers the model family from a package ID by longest-prefix matching
    /// against the given family list. Callers pass the families reported by the
    /// native loader catalog (<see cref="AudioCppRuntime.LoaderFamilies"/>); there is
    /// deliberately no built-in family list, because a hard-coded one silently rots
    /// as soon as the shim is built with a different model set.</summary>
    public static string? DeriveFamily(string packageId, IEnumerable<string> families)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(families);
        string? best = null;
        foreach (var family in families)
        {
            if (family.Length == 0) continue;
            if (packageId == family || packageId.StartsWith(family + "_"))
                if (best is null || family.Length > best.Length) best = family;
        }
        return best;
    }

    private static ModelValidationScan ScanManifest(string manifestPath)
    {
        string json;
        using (var stream = File.OpenRead(manifestPath))
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            json = new string(reader.ReadChars((int)stream.Length));
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var issues = new List<ModelValidationIssue>();
        var checkedFiles = 0;
        ulong checkedBytes = 0;
        var packageId = PackageIdFromManifestName(Path.GetFileName(manifestPath));

        if (root.ValueKind != JsonValueKind.Object) return new ModelValidationScan(packageId, checkedFiles, checkedBytes, issues);

        var packageDir = Path.GetFullPath(Path.GetDirectoryName(manifestPath)!);
        var modelsRoot = Path.GetDirectoryName(packageDir) ?? packageDir;
        var packageDirPrefix = Path.GetFileName(packageDir);

        foreach (var member in root.EnumerateObject())
        {
            if (member.Name != "files" || member.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var file in member.Value.EnumerateObject())
            {
                var relative = file.Name;
                ulong? expectedSize = file.Value.ValueKind == JsonValueKind.Object && file.Value.GetProperty("size").ValueKind != JsonValueKind.Null
                    ? file.Value.GetProperty("size").GetUInt64()
                    : null;

                var candidates = new List<string> { Path.Combine(modelsRoot, relative) };
                if (relative.StartsWith(packageDirPrefix + "/") || relative.StartsWith(packageDirPrefix + "\\"))
                    candidates.Add(Path.Combine(packageDir, relative.Substring(packageDirPrefix.Length + 1)));
                candidates.Add(Path.Combine(packageDir, relative));

                string? actual = null;
                foreach (var candidate in candidates)
                    if (File.Exists(candidate)) { actual = candidate; break; }

                if (actual is null)
                {
                    issues.Add(new ModelValidationIssue("missing", relative,
                        expectedSize is null ? "" : $"expected {expectedSize} byte(s)"));
                    continue;
                }

                checkedFiles++;
                if (expectedSize is null) continue;

                ulong actualSize;
                using (var stream = File.OpenRead(actual))
                {
                    actualSize = (ulong)stream.Length;
                }
                checkedBytes += actualSize;
                if (actualSize != expectedSize)
                    issues.Add(new ModelValidationIssue("size", relative, $"expected {expectedSize} byte(s), found {actualSize}"));
            }
        }
        return new ModelValidationScan(packageId, checkedFiles, checkedBytes, issues);
    }

    private static bool IsEmpty(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory)) return false;
        foreach (var sub in Directory.EnumerateDirectories(directory)) return false;
        return true;
    }

    private static string? PackageIdFromManifestName(string fileName)
    {
        if (!fileName.StartsWith(ManifestPrefix) || !fileName.EndsWith(ManifestSuffix)) return null;
        return fileName.Substring(ManifestPrefix.Length, fileName.Length - ManifestPrefix.Length - ManifestSuffix.Length);
    }
}

internal sealed record ModelValidationScan(string? PackageId, int CheckedFiles, ulong CheckedBytes, IReadOnlyList<ModelValidationIssue> Issues);