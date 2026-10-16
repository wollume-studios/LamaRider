using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class ReplaceWithTransformedTests
{
    const string Type = "T:App.Calc";
    const string Add = "M:App.Calc.Add(System.Int32)";
    const string Field = "F:App.Calc._log";

    static AspectModel Model(params TransformationInfo[] transformations) => new()
    {
        AspectClasses = [new() { Id = "T:App.NotifyAttribute", Name = "Notify" }, new() { Id = "T:App.CacheAttribute", Name = "Cache" }],
        Transformations = transformations.ToList(),
    };

    static TransformationInfo Made(string aspect, string target, string instanceTarget) =>
        new() { AspectClass = aspect, Target = target, InstanceTarget = instanceTarget };

    [Fact]
    public void Aspects_elsewhere_that_still_transform_the_declaration_are_named()
    {
        // [Notify] on the type overrides the member: after the replacement it would transform the code a second time.
        var model = Model(Made("T:App.NotifyAttribute", Add, Type));
        Assert.Equal(["Notify on the containing declaration still transforms it: replace that declaration instead, or undo."],
            ReplaceWithTransformed.Warnings(model, new HashSet<string> { Add }));
        Assert.Empty(ReplaceWithTransformed.Warnings(model, new HashSet<string> { Type, Add }));
    }

    [Fact]
    public void Code_the_aspects_added_outside_the_declaration_is_named()
    {
        // [Cache] on Add introduces a field in the type: it goes away with the aspect.
        var model = Model(Made("T:App.CacheAttribute", Type, Add + ";Parameter=0"));
        Assert.Equal(["Cache also changed code outside it (e.g. introduced members); that code goes away with the aspect."],
            ReplaceWithTransformed.Warnings(model, new HashSet<string> { Add }));
    }

    [Fact]
    public void Transformations_inside_the_declaration_need_no_warning() =>
        Assert.Empty(ReplaceWithTransformed.Warnings(Model(Made("T:App.CacheAttribute", Field, Type)), new HashSet<string> { Type, Field }));
}
