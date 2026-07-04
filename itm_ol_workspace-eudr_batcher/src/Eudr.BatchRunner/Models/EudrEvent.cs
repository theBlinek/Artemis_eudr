namespace Eudr.BatchRunner.Models;

public enum EudrEventType
{
    Rin = 1,
    Rpr = 2,
    Rzu = 100,
    Out = 110,
    Zwr = 200,
    Pks = 300,
}

public enum EudrDirection
{
    Forward,
    Reverse,
}

public record EudrEvent(
    int EventId,
    int MagDokNagId,
    EudrEventType EudrType,
    EudrDirection Direction,
    int SeriaId,
    int OccurrenceSequence,
    DateTime Timestamp
);
