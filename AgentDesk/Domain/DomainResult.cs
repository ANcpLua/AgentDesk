namespace AgentDesk.Domain;

public sealed record Succeeded<T>(T Value);
public sealed record InvalidInput(string Message);
public sealed record StateConflict(string Message);

public readonly union DomainResult<T>(Succeeded<T>, InvalidInput, StateConflict);

public static class DomainResults
{
    public static DomainResult<TNext> Map<T, TNext>(this DomainResult<T> result, Func<T, TNext> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return result switch
        {
            Succeeded<T> success => new Succeeded<TNext>(map(success.Value)),
            InvalidInput invalid => invalid,
            StateConflict conflict => conflict,
            null => throw new InvalidOperationException("Domain result is not initialized.")
        };
    }

    public static DomainResult<TNext> Bind<T, TNext>(this DomainResult<T> result, Func<T, DomainResult<TNext>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return result switch
        {
            Succeeded<T> success => bind(success.Value),
            InvalidInput invalid => invalid,
            StateConflict conflict => conflict,
            null => throw new InvalidOperationException("Domain result is not initialized.")
        };
    }
}
