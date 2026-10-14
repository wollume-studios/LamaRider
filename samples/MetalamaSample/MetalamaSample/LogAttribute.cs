using Metalama.Framework.Aspects;

namespace MetalamaSample;

public class LogAttribute : OverrideMethodAspect
{
    public override dynamic? OverrideMethod()
    {
        var name = meta.Target.Method.Name;
        Console.WriteLine($"Entering {name}");
        try
        {
            return meta.Proceed();
        }
        finally
        {
            Console.WriteLine($"Leaving {name}");
        }
    }
}
