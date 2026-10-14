using Metalama.Framework.Aspects;

namespace MetalamaSample;

public class IdAttribute : TypeAspect
{
    [Introduce]
    public Guid Id { get; } = Guid.NewGuid();
}
