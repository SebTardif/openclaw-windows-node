namespace OpenClaw.Tray.Tests;

public class MsixSideloadCertificateTeardownTests
{
    [Fact]
    public void TeardownRemovesOnlyTheCertificateImportedByThisRun()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "scripts",
            "validate-msix-storage-paths.ps1"));

        Assert.Contains("$script:ImportedSideloadThumbprint = $thumbprint", source);
        Assert.Contains("if (-not $alreadyPresent)", source);
        Assert.Contains("Where-Object { $_.Thumbprint -eq $script:ImportedSideloadThumbprint }", source);
        Assert.Contains("Remove-Item -LiteralPath $item.PSPath -Force", source);
        Assert.DoesNotContain("Remove-Item -Path 'Cert:\\LocalMachine\\TrustedPeople'", source);
    }
}
