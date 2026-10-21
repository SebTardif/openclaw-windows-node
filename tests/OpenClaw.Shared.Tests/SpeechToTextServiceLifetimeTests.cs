using OpenClaw.Shared.Audio;

namespace OpenClaw.Shared.Tests;

public sealed class SpeechToTextServiceLifetimeTests
{
    [Fact]
    public void LoadAndUnload_WaitForTheTranscriptionGateBeforeReplacingTheFactory()
    {
        var source = File.ReadAllText(Path.Combine(
            ProductionSourceFiles.FindRepoRoot(),
            "src",
            "OpenClaw.Shared",
            "Audio",
            "SpeechToTextService.cs"));

        var transcribe = source.IndexOf("public async Task<List<TranscriptionResult>> TranscribeAsync", StringComparison.Ordinal);
        var dispose = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(transcribe >= 0);
        Assert.True(dispose > transcribe);

        var transcribeBody = source[transcribe..dispose];
        AssertOccursBefore(transcribeBody, "await _gate.WaitAsync(cancellationToken);", "ObjectDisposedException.ThrowIf(_disposed, this);");
        AssertOccursBefore(transcribeBody, "ObjectDisposedException.ThrowIf(_disposed, this);", "if (_factory == null)");
        AssertOccursBefore(transcribeBody, "if (_factory == null)", "_factory.CreateBuilder()");

        var load = MethodBody(source, "public void LoadModel(string modelPath)");
        AssertOccursBefore(load, "_gate.Wait();", "_factory = null;");
        AssertOccursBefore(load, "_factory = null;", "previous?.Dispose();");
        AssertOccursBefore(load, "_loadedModelPath = null;", "WhisperFactory.FromPath(modelPath);");
        AssertOccursBefore(load, "previous?.Dispose();", "_factory = WhisperFactory.FromPath(modelPath);");

        var unload = MethodBody(source, "public void UnloadModel()");
        AssertOccursBefore(unload, "_gate.Wait();", "_factory = null;");
        AssertOccursBefore(unload, "_factory = null;", "previous?.Dispose();");

        var disposeBody = source[dispose..];
        AssertOccursBefore(disposeBody, "_gate.Wait();", "_factory = null;");
        AssertOccursBefore(disposeBody, "_factory = null;", "previous?.Dispose();");
        AssertOccursBefore(disposeBody, "_loadedModelPath = null;", "previous?.Dispose();");
        AssertOccursBefore(disposeBody, "_disposed = true;", "previous?.Dispose();");
        Assert.DoesNotContain("_gate.Dispose()", source, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = source.IndexOf("public ", start + signature.Length, StringComparison.Ordinal);
        Assert.True(next > start);
        return source[start..next];
    }

    private static void AssertOccursBefore(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, $"Missing expected source fragment: {first}");
        Assert.True(secondIndex > firstIndex, $"Expected '{first}' before '{second}'.");
    }
}
