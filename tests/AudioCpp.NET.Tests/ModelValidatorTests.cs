using AudioCpp.NET;

namespace AudioCpp.NET.Tests;

public sealed class ModelValidatorTests
{
    private static int _sequence;

    [Fact]
    public void CompletePackageIsVerifiedClean()
    {
        var fixture = new Fixture();
        var packageDir = fixture.Package("Citrinet-ASR-GGUF", "citrinet_asr_q8_0",
            "{\"package_id\":\"citrinet_asr_q8_0\",\"files\":{\"Citrinet-ASR-GGUF/citrinet-asr-q8_0.gguf\":{\"size\":12}}}"u8);
        WriteFile(Path.Combine(packageDir, "citrinet-asr-q8_0.gguf"), 12);

        var report = ModelValidator.Validate(packageDir);
        Assert.True(report.Complete);
        Assert.Equal("citrinet_asr_q8_0", report.PackageId);
        Assert.Equal(1, report.CheckedFiles);
    }

    [Fact]
    public void MissingFileIsReported()
    {
        var fixture = new Fixture();
        var packageDir = fixture.Package("Citrinet-ASR-GGUF", "citrinet_asr_q8_0",
            "{\"package_id\":\"citrinet_asr_q8_0\",\"files\":{\"Citrinet-ASR-GGUF/citrinet-asr-q8_0.gguf\":{\"size\":12},\"Citrinet-ASR-GGUF/missing.bin\":{\"size\":4}}}"u8);
        WriteFile(Path.Combine(packageDir, "citrinet-asr-q8_0.gguf"), 12);

        var report = ModelValidator.Validate(packageDir);
        Assert.False(report.Complete);
        Assert.Single(report.Issues);
        Assert.Equal("missing", report.Issues[0].Kind);
        Assert.Contains("missing.bin", report.Issues[0].Path);
    }

    [Fact]
    public void SizeMismatchIsReported()
    {
        var fixture = new Fixture();
        var packageDir = fixture.Package("Qwen3-TTS-New", "qwen3_tts_test_pkg",
            "{\"package_id\":\"qwen3_tts_test_pkg\",\"files\":{\"Qwen3-TTS-New/model.gguf\":{\"size\":99}}}"u8);
        WriteFile(Path.Combine(packageDir, "model.gguf"), 10);

        var report = ModelValidator.Validate(packageDir);
        Assert.False(report.Complete);
        Assert.Equal("size", report.Issues[0].Kind);
    }

    [Fact]
    public void ManifestKeysRelativeToPackageDirAreSupported()
    {
        var fixture = new Fixture();
        var packageDir = fixture.Package("Audio8-ASR-Bare", "audio8_bare_pkg",
            "{\"package_id\":\"audio8_bare_pkg\",\"files\":{\"config.json\":{\"size\":5},\"model.safetensors\":{\"size\":8}}}"u8);
        WriteFile(Path.Combine(packageDir, "config.json"), 5);
        WriteFile(Path.Combine(packageDir, "model.safetensors"), 8);

        var report = ModelValidator.Validate(packageDir);
        Assert.True(report.Complete);
        Assert.Equal(2, report.CheckedFiles);
    }

    [Fact]
    public void UnmanagedDirectoryWithFilesIsComplete()
    {
        var fixture = new Fixture();
        var dir = fixture.NewDirectory("Audio8-ASR-0.1B-hf");
        WriteFile(Path.Combine(dir, "model.safetensors"), 16);

        var report = ModelValidator.Validate(dir);
        Assert.True(report.Complete);
        Assert.False(report.ManifestPresent);
    }

    [Fact]
    public void EmptyDirectoryIsReported()
    {
        var report = ModelValidator.Validate(new Fixture().NewDirectory("empty"));
        Assert.False(report.Complete);
        Assert.Equal("empty", report.Issues[0].Kind);
    }

    [Fact]
    public void ValidateRejectsNonDirectory()
    {
        var fixture = new Fixture();
        var dir = fixture.NewDirectory("dir-with-file");
        WriteFile(Path.Combine(dir, "x.bin"), 4);
        Assert.Throws<ArgumentException>(() => ModelValidator.Validate(Path.Combine(dir, "x.bin")));
    }

    [Fact]
    public void FindPackageDirectoryLocatesPackage()
    {
        var fixture = new Fixture();
        var packageDir = fixture.Package("Citrinet-ASR-GGUF", "citrinet_asr_q8_0", "{\"package_id\":\"citrinet_asr_q8_0\",\"files\":{}}"u8);
        WriteFile(Path.Combine(packageDir, "citrinet-asr-q8_0.gguf"), 4);

        Assert.Equal(Path.GetFullPath(packageDir), Path.GetFullPath(ModelValidator.FindPackageDirectory(fixture.Root, "citrinet_asr_q8_0")!));
        Assert.Null(ModelValidator.FindPackageDirectory(fixture.Root, "unknown_pkg"));
    }

    [Fact]
    public void FindPackageIdsListsInstalledPackages()
    {
        var fixture = new Fixture();
        fixture.Package("Citrinet-ASR-GGUF", "citrinet_asr_q8_0", "{\"package_id\":\"citrinet_asr_q8_0\",\"files\":{}}"u8);
        fixture.Package("Qwen3-TTS-New", "qwen3_tts_test_pkg", "{\"package_id\":\"qwen3_tts_test_pkg\",\"files\":{}}"u8);

        var ids = ModelValidator.FindPackageIds(fixture.Root);
        Assert.Equal(2, ids.Count);
        Assert.Contains("citrinet_asr_q8_0", ids);
        Assert.Contains("qwen3_tts_test_pkg", ids);
    }

    [Fact]
    public void DeriveFamilyUsesLongestPrefixMatch()
    {
        var families = new[] { "tts", "qwen3_tts", "citrinet_asr" };
        Assert.Equal("qwen3_tts", ModelValidator.DeriveFamily("qwen3_tts_0_6b_base_q8_0", families));
        Assert.Equal("citrinet_asr", ModelValidator.DeriveFamily("citrinet_asr_q8_0", families));
        // The loader catalog is the only source of families now, so an id whose
        // family is not installed resolves to null instead of a compiled-in guess.
        Assert.Null(ModelValidator.DeriveFamily("audio8_asr_0_1b_safetensors", families));
        Assert.Null(ModelValidator.DeriveFamily("unknown_pkg_x", families));
    }

    [Fact]
    public void DeriveFamilyMatchesEverySuppliedFamily()
    {
        var families = new[] { "audio8_asr", "tts" };
        Assert.Equal("audio8_asr", ModelValidator.DeriveFamily("audio8_asr_0_1b_safetensors", families));
        Assert.Equal("tts", ModelValidator.DeriveFamily("tts", families));
        Assert.Throws<ArgumentNullException>(() => ModelValidator.DeriveFamily("tts", null!));
    }

    private static void WriteFile(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(new byte[size]);
    }

    private static void WriteBytes(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(bytes);
    }

    private sealed class Fixture
    {
        internal readonly string Root = Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? ".", $"audiocpp-mv-{++_sequence}");

        internal string NewDirectory(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        internal string Package(string directoryName, string packageId, ReadOnlySpan<byte> manifestJson)
        {
            var dir = NewDirectory(directoryName);
            WriteBytes(Path.Combine(dir, $".audiocpp-package-{packageId}.json"), manifestJson);
            return dir;
        }
    }

    // __TESTS2__
}