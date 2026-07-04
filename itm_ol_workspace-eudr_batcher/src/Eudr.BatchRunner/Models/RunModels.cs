namespace Eudr.BatchRunner.Models;

public enum RunStatus
{
    Running,
    Done,
    DoneWithErrors,
}

public enum RunItemOutcome
{
    Ok,
    Shortage,
    Skipped,
    BusinessException,
    TechnicalFailure,
}

public record EudrRun(
    int RunId,
    DateOnly BusinessDate,
    RunStatus Status,
    DateTime StartedAt
);

public record EudrRunItem(
    int RunId,
    int EventId,
    int MagDokNagId,
    EudrEventType EventType,
    EudrDirection Direction,
    RunItemOutcome Outcome,
    string? Notes
);

public record RunReport(
    int RunId,
    DateOnly BusinessDate,
    int OkCount,
    int ShortageCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<EudrRunItem> Failures,
    IReadOnlyList<int> JournalGaps
)
{
    public static RunReport AlreadyDone(DateOnly businessDate) =>
        new(0, businessDate, 0, 0, 0, 0, [], []);
}
