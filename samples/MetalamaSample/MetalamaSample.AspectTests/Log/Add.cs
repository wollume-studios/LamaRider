namespace MetalamaSample.AspectTests.Log;

public class Target
{
    [MetalamaSample.Log]
    public int Add(int a, int b) => a + b;
}
