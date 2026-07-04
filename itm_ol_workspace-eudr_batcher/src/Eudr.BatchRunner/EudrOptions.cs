namespace Eudr.BatchRunner;

public sealed class EudrOptions
{
    public const string Section = "Eudr";

    /// <summary>GS1 company prefix used to generate labels for RIN rows (§9.1).</summary>
    public string Gs1Prefix { get; set; } = "INT";

    /// <summary>CRON expression for the nightly run (Portainer / cron container).</summary>
    public string Schedule { get; set; } = "0 2 * * *";
}
