using Metalama.Framework.Aspects;
using Metalama.Framework.Fabrics;

namespace MetalamaSample;

public class Fabric : ProjectFabric
{
    public override void AmendProject(IProjectAmender amender) =>
        amender.SelectMany(c => c.Types)
            .Where(t => t.Name == nameof(Calculator))
            .SelectMany(t => t.Methods)
            .Where(m => m.Name == nameof(Calculator.Multiply))
            .AddAspect<LogAttribute>();
}
