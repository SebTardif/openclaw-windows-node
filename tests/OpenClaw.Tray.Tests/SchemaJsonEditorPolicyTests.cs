using System.Text.Json;
using OpenClawTray.Controls;

namespace OpenClaw.Tray.Tests;

public class SchemaJsonEditorPolicyTests
{
    [Fact]
    public void EditorTextOmitsNestedCredentialAndSaveRestoresIt()
    {
        using var original = JsonDocument.Parse("""
            {
              "name": "primary",
              "token": "stored-credential-value",
              "nested": { "api_key": "nested-credential-value", "label": "keep" }
            }
            """);
        var editorText = SchemaJsonEditorPolicy.TextForEditor(original.RootElement, expectObject: true);

        Assert.DoesNotContain("stored-credential-value", editorText);
        Assert.DoesNotContain("nested-credential-value", editorText);
        Assert.Contains("primary", editorText);

        var edited = editorText.Replace("primary", "renamed", StringComparison.Ordinal);
        Assert.True(SchemaJsonEditorPolicy.TryStage(edited, original.RootElement, expectObject: true, out var staged, out var error), error);
        Assert.Equal("renamed", staged.GetProperty("name").GetString());
        Assert.Equal("stored-credential-value", staged.GetProperty("token").GetString());
        Assert.Equal("nested-credential-value", staged.GetProperty("nested").GetProperty("api_key").GetString());
        Assert.Equal("keep", staged.GetProperty("nested").GetProperty("label").GetString());
    }

    [Fact]
    public void ExplicitReplacementKeepsTheNewSecret()
    {
        using var original = JsonDocument.Parse("""{"token":"stored-credential-value"}""");
        Assert.True(SchemaJsonEditorPolicy.TryStage(
            """{"token":"replacement-credential-value"}""",
            original.RootElement,
            expectObject: true,
            out var staged,
            out var error), error);
        Assert.Equal("replacement-credential-value", staged.GetProperty("token").GetString());
    }

    [Fact]
    public void RedactionMarkerIsNotStaged()
    {
        using var original = JsonDocument.Parse("""{"name":"primary"}""");
        Assert.False(SchemaJsonEditorPolicy.TryStage(
            """{"name":"[REDACTED]"}""",
            original.RootElement,
            expectObject: true,
            out _,
            out var error));
        Assert.Equal("A redaction marker cannot be saved.", error);
    }

    [Fact]
    public void WrongJsonKindIsRejected()
    {
        using var original = JsonDocument.Parse("""{"name":"primary"}""");
        Assert.False(SchemaJsonEditorPolicy.TryStage("[]", original.RootElement, expectObject: true, out _, out var error));
        Assert.Equal("Must be a JSON object.", error);
    }

    [Fact]
    public void ArrayEditRestoresSecretsWhenTheItemCountStaysTheSame()
    {
        using var original = JsonDocument.Parse("""
            [{"name":"primary","token":"stored-credential-value"}]
            """);
        var editorText = SchemaJsonEditorPolicy.TextForEditor(original.RootElement, expectObject: false);
        Assert.DoesNotContain("stored-credential-value", editorText);

        var edited = editorText.Replace("primary", "renamed", StringComparison.Ordinal);
        Assert.True(SchemaJsonEditorPolicy.TryStage(edited, original.RootElement, expectObject: false, out var staged, out var error), error);
        Assert.Equal("renamed", staged[0].GetProperty("name").GetString());
        Assert.Equal("stored-credential-value", staged[0].GetProperty("token").GetString());
    }

    [Fact]
    public void ChangingAnArrayThatHoldsASecretIsRejected()
    {
        using var original = JsonDocument.Parse("""
            [{"name":"primary","token":"stored-credential-value"}]
            """);
        Assert.False(SchemaJsonEditorPolicy.TryStage(
            "[]",
            original.RootElement,
            expectObject: false,
            out _,
            out var error));
        Assert.Equal("Changing the number of items would drop a stored secret.", error);
    }

    [Fact]
    public void JsonFieldsStageTheOriginalValue()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Controls",
            "SchemaConfigEditor.xaml.cs"));

        Assert.Contains("SchemaJsonEditorPolicy.TryStage(textBox.Text, original, expectObject: true", source);
        Assert.Contains("SchemaJsonEditorPolicy.TryStage(textBox.Text, original, expectObject: false", source);
    }
}
