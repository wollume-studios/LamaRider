namespace MetalamaSample.AspectTests.Log;
public class Target
{
  [MetalamaSample.Log]
  public int Add(int a, int b)
  {
    global::System.Console.WriteLine("Entering Add");
    try
    {
      return a + b;
    }
    finally
    {
      global::System.Console.WriteLine("Leaving Add");
    }
  }
}