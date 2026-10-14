using Metalama.Patterns.Contracts;

namespace MetalamaSample;

public class Links
{
    public string Open([Url] string address) => address;
}
