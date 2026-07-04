using Eudr.BatchRunner.Interfaces;
using Eudr.BatchRunner.Models;

namespace Eudr.BatchRunner;

/// <summary>
/// Routes (EudrEventType, EudrDirection) pairs to the registered handler.
/// All handlers are registered at startup; an unknown pair is a configuration error.
/// </summary>
public sealed class Dispatcher(IEnumerable<IHandler> handlers)
{
    private readonly IReadOnlyDictionary<(EudrEventType, EudrDirection), IHandler> _map =
        handlers.ToDictionary(h => (h.EventType, h.Direction));

    public IHandler Resolve(EudrEvent evt)
    {
        var key = (evt.EudrType, evt.Direction);
        if (!_map.TryGetValue(key, out var handler))
            throw new InvalidOperationException(
                $"No handler registered for event type {evt.EudrType} direction {evt.Direction}");
        return handler;
    }
}
