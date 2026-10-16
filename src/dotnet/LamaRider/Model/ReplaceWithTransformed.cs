using System.Collections.Generic;
using System.Linq;

namespace LamaRider.Model;

/// <summary>Replacing one declaration's source with its transformed code (single-declaration divorce).</summary>
public static class ReplaceWithTransformed
{
    /// <summary>
    /// What the replacement does not carry over: aspects outside the declaration that transform it (they would transform the
    /// written-out code again) and code its aspects changed outside it (introduced members), which goes away with them.
    /// </summary>
    /// <param name="inside">Doc ids of the declaration and everything declared in it.</param>
    public static IReadOnlyList<string> Warnings(AspectModel model, ISet<string> inside)
    {
        bool In(string? id) => id != null && inside.Contains(AspectModel.Owner(id));
        var outside = model.Transformations.Where(t => t.InstanceTarget != null && In(t.Target) && !In(t.InstanceTarget))
            .Select(t => $"{model.AspectName(t.AspectClass)} on the containing declaration still transforms it: replace that declaration instead, or undo.");
        var leaking = model.Transformations.Where(t => t.InstanceTarget != null && In(t.InstanceTarget) && !In(t.Target))
            .Select(t => $"{model.AspectName(t.AspectClass)} also changed code outside it (e.g. introduced members); that code goes away with the aspect.");
        return outside.Concat(leaking).Distinct().ToList();
    }
}
