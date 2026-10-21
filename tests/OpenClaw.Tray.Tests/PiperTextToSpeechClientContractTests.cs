namespace OpenClaw.Tray.Tests;

public sealed class PiperTextToSpeechClientContractTests
{
    private static string ReadSource() =>
        File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "TextToSpeech",
            "PiperTextToSpeechClient.cs"));

    [Fact]
    public void NativeLibrary_IsLoadedBeforeFinalizableWrapperConstruction()
    {
        var source = ReadSource();

        AssertInOrder(
            source,
            "EnsureNativeLibraryLoaded();",
            "_tts = new OfflineTts(config);");
        Assert.Contains("NativeLibrary.TryLoad(", source);
        Assert.Contains("typeof(OfflineTts).Assembly", source);
        Assert.Contains("DllImportSearchPath.SafeDirectories", source);
        Assert.Contains("out s_nativeLibraryHandle", source);
    }

    [Fact]
    public void Dispose_SuppressesFinalizerBeforeNativeCleanup()
    {
        var source = ReadSource();

        AssertInOrder(
            source,
            "GC.SuppressFinalize(_tts);",
            "_tts.Dispose();");
    }

    [Fact]
    public void Dispose_WaitsForTheGenerationGateBeforeFreeingTheSynthesizer()
    {
        var source = ReadSource();
        var generate = source.IndexOf("public async Task<byte[]> GenerateWavAsync", StringComparison.Ordinal);
        var dispose = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(generate >= 0);
        Assert.True(dispose > generate);

        var generateBody = source[generate..dispose];
        AssertInOrder(generateBody, "await _gate.WaitAsync(cancellationToken)", "ObjectDisposedException.ThrowIf(_disposed, this);");
        AssertInOrder(generateBody, "ObjectDisposedException.ThrowIf(_disposed, this);", "_tts.Generate(text, speed: speed, speakerId: 0);");

        var disposeBody = source[dispose..];
        AssertInOrder(disposeBody, "_gate.Wait();", "_disposed = true;");
        AssertInOrder(disposeBody, "_disposed = true;", "GC.SuppressFinalize(_tts);");
        AssertInOrder(disposeBody, "GC.SuppressFinalize(_tts);", "_tts.Dispose();");
        Assert.DoesNotContain("_gate.Dispose()", disposeBody, StringComparison.Ordinal);
    }

    private static void AssertInOrder(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);

        Assert.True(firstIndex >= 0, $"Missing expected source fragment: {first}");
        Assert.True(secondIndex > firstIndex, $"Expected '{first}' before '{second}'.");
    }
}
