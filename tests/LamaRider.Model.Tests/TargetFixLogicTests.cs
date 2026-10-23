using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class TargetFixLogicTests
{
    [Fact]
    public void Quoted_names_in_order() =>
        Assert.Equal(["Log", "Calculator.Add(int, int)"], TargetFixLogic.Quoted("The aspect 'Log' cannot be applied to 'Calculator.Add(int, int)'."));

    [Fact]
    public void Short_name_drops_container_parameters_and_type_arguments()
    {
        Assert.Equal("Add", TargetFixLogic.ShortName("Calculator.Add(int, int)"));
        Assert.Equal("ILogger", TargetFixLogic.ShortName("ILogger<OrderService>"));
    }

    [Fact]
    public void Type_aspect_gets_class_and_struct_targets() =>
        Assert.Equal("AttributeTargets.Class | AttributeTargets.Struct", TargetFixLogic.AttributeTargets(["INamedType"]));

    [Fact]
    public void Several_kinds_are_combined_in_a_fixed_order() =>
        Assert.Equal("AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field",
            TargetFixLogic.AttributeTargets(["IFieldOrProperty", "IMethod"]));

    [Fact]
    public void Kind_without_attribute_target_gives_none() => Assert.Null(TargetFixLogic.AttributeTargets(["IDeclaration"]));

    [Fact]
    public void Eligibility_message_gives_modifier_change() =>
        Assert.Equal([new TargetFixLogic.ModifierChange(false, "static", "Calculator.Add(int, int)")],
            TargetFixLogic.ModifierChanges("The aspect 'Log' cannot be applied to the method 'Calculator.Add(int, int)' because 'Calculator.Add(int, int)' must not be static."));

    [Fact]
    public void Other_conditions_give_no_modifier_change() =>
        Assert.Empty(TargetFixLogic.ModifierChanges("The aspect 'Log' cannot be applied to 'X.Y()' because 'X.Y()' must be readable."));

    [Fact]
    public void Meta_target_suggestion_comes_first()
    {
        var r = TargetFixLogic.MetaTargetReplacements("The advice 'LogReadAttribute.OverrideProperty.get' cannot use 'meta.Target.Field' in an advice applied to method 'Customer.Name.get' because there is no 'IField'. Consider using meta.Target.FieldOrProperty instead.");
        Assert.Equal("Field", r!.Value.Used);
        Assert.Equal(["FieldOrProperty", "FieldOrPropertyOrIndexer", "Member", "Declaration"], r.Value.Replacements);
    }

    [Fact]
    public void Target_expression_follows_the_advised_kind()
    {
        Assert.Equal("meta.Target.Method", TargetFixLogic.TargetExpression("Cannot reference 'this' in an advice applied to method 'C.M()' because the target method is static."));
        Assert.Equal("meta.Target.Member", TargetFixLogic.TargetExpression("Cannot reference 'this' in an advice applied to property 'C.P' because it is static."));
    }

    [Fact]
    public void Interpolation_hole_is_removed_with_its_word() =>
        Assert.Equal("Console.WriteLine( $\"Executing {meta.Target.Method.Name}.\" );",
            TargetFixLogic.WithoutHole("Console.WriteLine( $\"Executing {meta.Target.Method.Name} on {meta.This}.\" );", "meta.This"));

    [Fact]
    public void Other_uses_cannot_be_removed() => Assert.Null(TargetFixLogic.WithoutHole("Log( meta.This );", "meta.This"));

    [Fact]
    public void Guard_has_both_branches_at_the_statement_indent() =>
        Assert.Equal("if ( c )\n        {\n            a();\n        }\n        else\n        {\n            b();\n        }",
            TargetFixLogic.Guard("c", "a();", "b();", "        "));

    [Fact]
    public void Run_time_order_puts_the_contributor_after_the_weaver() =>
        Assert.Equal([1, 0, 2], TargetFixLogic.OrderBeforeWeavers(3, 0, [1], runTime: true)!);

    [Fact]
    public void Compile_time_order_puts_the_contributor_before_the_weaver() =>
        Assert.Equal([1, 0, 2], TargetFixLogic.OrderBeforeWeavers(3, 1, [0], runTime: false)!);

    [Fact]
    public void Order_already_right_gives_null() => Assert.Null(TargetFixLogic.OrderBeforeWeavers(3, 2, [1], runTime: true));

    [Fact]
    public void Renamed_variable_from_message() =>
        Assert.Equal(("id", "id_1"), TargetFixLogic.Renamed("The lambda parameter 'id' is renamed to 'id_1' in the compiled code, because it hides the parameter 'id' of 'P.Process(int, int[])'"));

    [Theory]
    [InlineData("items", "item")]
    [InlineData("entries", "entry")]
    [InlineData("boxes", "box")]
    [InlineData("class", null)]
    [InlineData("id", null)]
    public void Singular_of_collection_names(string plural, string? singular) => Assert.Equal(singular, TargetFixLogic.Singular(plural));
}
