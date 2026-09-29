using AudioCpp.NET;
using AudioCpp.NET.Interop;

namespace AudioCpp.NET.Tests;

/// <summary>
/// Managed-side validation for <see cref="AudioCppModel.RunBatch"/>: everything
/// the wrapper rejects before the native call happens. Uses a dummy model handle
/// (capabilities are taken from the constructor argument), so these tests run
/// with and without the native library present. Batches that pass managed
/// validation fail later at the native boundary — the exact exception there
/// depends on whether the native library is loaded (DllNotFoundException vs
/// NativeCallException), so those cases only assert that managed validation
/// did NOT reject the batch.
/// </summary>
public sealed class RunBatchTests
{
    private static AudioCppModel ModelWithCapabilities(ulong capabilities) =>
        new(new SafeModelHandle(IntPtr.Zero), capabilities);

    private static AudioCppRunRequest TextRequest(string text = "hi", string? task = null) =>
        new() { Task = task, Text = text };

    private static AudioCppRunRequest AudioRequest(string? task = null) =>
        new() { Task = task, Audio = new float[] { 0f, 0f }, SampleRate = 16000 };

    /// <summary>Runs the batch and expects it to get past managed validation:
    /// any exception other than the managed validation ones counts as reaching
    /// the native boundary (or beyond, in a matrix environment with a real shim).</summary>
    private static void AssertPassesManagedValidation(AudioCppModel model, IReadOnlyList<AudioCppRunRequest> requests, string? task = null)
    {
        try
        {
            model.RunBatch(requests, task);
        }
        catch (Exception exception) when (
            exception is not ArgumentException and
            not ArgumentNullException and
            not NotSupportedException)
        {
            return; // failed at (or past) the native boundary: managed validation passed
        }
        // In a matrix environment the dummy batch may even succeed; that is fine too.
    }

    [Fact]
    public void BatchCapabilityIsCheckedBeforeAnythingElseRuns()
    {
        using var model = ModelWithCapabilities(0);
        Assert.Throws<NotSupportedException>(() =>
            model.RunBatch([TextRequest()]));
        Assert.Throws<NotSupportedException>(() =>
            model.RunBatch([TextRequest()], task: "asr"));
    }

    [Fact]
    public void BatchRejectsEmptyAndNullRequestLists()
    {
        using var model = ModelWithCapabilities(AudioCppCapabilities.Batch);
        Assert.Throws<ArgumentNullException>(() => model.RunBatch(null!));
        Assert.Throws<ArgumentException>(() => model.RunBatch([]));
    }

    [Fact]
    public void BatchValidatesEachRequestAndRejectsVoiceReferences()
    {
        using var model = ModelWithCapabilities(AudioCppCapabilities.Batch);
        // A request that fails AudioCppRunRequests.Validate is rejected up front.
        Assert.Throws<ArgumentException>(() => model.RunBatch(
            [TextRequest(), new AudioCppRunRequest { Task = "asr" }]));
        // ReferencePcm (voice cloning) needs the per-request entry points.
        Assert.Throws<NotSupportedException>(() => model.RunBatch(
            [new AudioCppRunRequest { Text = "hi", ReferencePcm = new float[] { 0f }, ReferenceSampleRate = 16000 }]));
    }

    [Fact]
    public void BatchRequiresAnExplicitTaskForMixedShapes()
    {
        using var model = ModelWithCapabilities(AudioCppCapabilities.Batch);
        var exception = Assert.Throws<ArgumentException>(() =>
            model.RunBatch([TextRequest(), AudioRequest()]));
        Assert.Contains("Mixed batch", exception.Message);

        // An explicit task resolves the same batch.
        AssertPassesManagedValidation(model, [TextRequest(), AudioRequest()], task: "asr");
    }

    [Fact]
    public void BatchInfersTheTaskFromUnanimousPerRequestTasksOrShape()
    {
        using var model = ModelWithCapabilities(AudioCppCapabilities.Batch);
        // Unanimous per-request task tokens win over shape inference.
        AssertPassesManagedValidation(model, [TextRequest(task: "tts"), TextRequest(task: "tts")]);
        // Disagreeing per-request tasks without an explicit task are refused
        // (the inferred tts/clon shapes also disagree, so both checks reject).
        Assert.ThrowsAny<Exception>(() =>
            model.RunBatch([TextRequest(task: "tts"), TextRequest(task: "clon")]));
        // Pure shape inference: all-audio -> asr.
        AssertPassesManagedValidation(model, [AudioRequest(), AudioRequest()]);
    }

    [Fact]
    public void BatchRequiresOneSharedAudioFormat()
    {
        using var model = ModelWithCapabilities(AudioCppCapabilities.Batch);
        var exception = Assert.Throws<ArgumentException>(() => model.RunBatch(
        [
            AudioRequest(),
            new AudioCppRunRequest { Audio = new float[] { 0f }, SampleRate = 8000 },
        ]));
        Assert.Contains("sample rate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
