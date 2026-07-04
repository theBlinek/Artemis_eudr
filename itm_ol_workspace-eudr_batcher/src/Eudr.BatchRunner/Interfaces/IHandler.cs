using System.Data;
using Eudr.BatchRunner.Models;

namespace Eudr.BatchRunner.Interfaces;

public record HandlerResult(RunItemOutcome Outcome, string? Notes = null);

/// <summary>
/// Contract for all forward and reverse event handlers.
/// Each handler receives an already-opened transaction; it reads, writes, and
/// adjusts FIFO counters within that transaction.  The batch runner commits or
/// rolls back based on the result / exception.
/// </summary>
public interface IHandler
{
    EudrEventType EventType { get; }
    EudrDirection Direction { get; }

    Task<HandlerResult> HandleAsync(EudrEvent evt, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default);
}
