namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Thrown for strict configuration failures in the live-parity lanes: a
/// missing/invalid profile path env var, a profile that fails strict
/// parsing or validation, or a missing/invalid named credential. Messages
/// name only field/variable names, never values, so this exception is safe
/// to surface verbatim in test output and TRX files.
/// </summary>
public sealed class LiveParityConfigurationException : Exception
{
    public LiveParityConfigurationException(string message)
        : base(message)
    {
    }

    public LiveParityConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
