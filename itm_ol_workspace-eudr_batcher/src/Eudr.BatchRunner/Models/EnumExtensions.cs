namespace Eudr.BatchRunner.Models;

public static class EnumExtensions
{
    public static string ToTransactionTypeString(this EudrEventType type) => type switch
    {
        EudrEventType.Rin => "RIN",
        EudrEventType.Rpr => "RPR",
        EudrEventType.Rzu => "RZU",
        EudrEventType.Out => "OUT",
        EudrEventType.Zwr => "ZWR",
        EudrEventType.Pks => "PKS",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static string ToDbString(this RunStatus status) => status switch
    {
        RunStatus.Running => "RUNNING",
        RunStatus.Done => "DONE",
        RunStatus.DoneWithErrors => "DONE_WITH_ERRORS",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static string ToDbString(this RunItemOutcome outcome) => outcome switch
    {
        RunItemOutcome.Ok => "OK",
        RunItemOutcome.Shortage => "SHORTAGE",
        RunItemOutcome.Skipped => "SKIPPED",
        RunItemOutcome.BusinessException => "BUSINESS_EXCEPTION",
        RunItemOutcome.TechnicalFailure => "TECHNICAL_FAILURE",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };
}
