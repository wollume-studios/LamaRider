# Quick-fixes for Metalama diagnostics

Put the caret on a Metalama diagnostic (error, warning or info) and press Alt+Enter. 199 diagnostic ids have fixes specific to them; they are listed below. Back to [Using LamaRider](usage.md#quick-fixes-for-metalama-diagnostics).

**Every diagnostic**
- *Look up LAMA#### online*: opens the Metalama article when there is one, otherwise a web search.
- Warnings and infos: *Suppress LAMA#### here (#pragma)* and *Suppress LAMA#### in project (NoWarn)*.
- Errors where an aspect does not fit its target (LAMA0003, 0037, 0114, 0247, 0287, 0534, 0700, 0703, 0750, 5111): *Remove [Aspect] from this declaration*.

**What the fixes change**: your code or the aspect's source, the project file (`LangVersion`, MSBuild properties, `NoWarn`), or they open a crash report or start a build. When the aspect comes from a NuGet package, only fixes on the target side are offered.

**Limits**
- A fix is offered only when the code has the shape it expects.
- Fixes in aspect code for errors reported on the aspect's target need the aspect's source in the solution.
- Some fixes (LINQ rewrites, constructor conversion, moves) handle only simple shapes.
- Fixes appear where the diagnostic is located in a file; project-level diagnostics show on the aspect class.
- Diagnostics update after each analysis (a few seconds after saving); changes to compile-time code pause Metalama until a build.

"Options" means several fixes are offered; pick one. Names in quotes stand for the declaration involved.

- [Aspect targets and eligibility](#aspect-targets-and-eligibility)
- [Template authoring and compile-time scope](#template-authoring-and-compile-time-scope)
- [Introducing members and interfaces](#introducing-members-and-interfaces)
- [Metalama.Patterns](#metalamapatterns)
- [Analyzers, architecture, interceptors](#analyzers-architecture-interceptors)
- [Project settings and language version](#project-settings-and-language-version)
- [Crashes and licensing](#crashes-and-licensing)

## Aspect targets and eligibility

| Id | What the fix does |
|---|---|
| LAMA0003 | Options: move the aspect attribute to the containing type; add `[AttributeUsage]` to the aspect; both |
| LAMA0021 | Remove an `[AspectOrder]` attribute of the cycle |
| LAMA0022 | Options: swap the two aspects in `[AspectOrder]`; add `[assembly: AspectOrder]` for the parent before the child |
| LAMA0025 | Use `nameof(template)` instead of the string |
| LAMA0035 | Options: add `[assembly: AspectOrder(CompileTime, A, B)]` keeping the current order, or the reverse order |
| LAMA0037 | Options: make the target (or a containing type) have or lose the modifier the eligibility demands; use `AddAspectIfEligible` instead of `AddAspect` in the fabric |
| LAMA0039 | Use `AddAspectIfEligible` |
| LAMA0042 | Options: order the weaver before or after all layers of the aspect (`[assembly: AspectOrder]`) |
| LAMA0048 | Make the type and its containing types partial |
| LAMA0063 | Use an `InvokerOptions` value allowed for the member (`Default` or `Final` for `Base` or `Current`) |
| LAMA0091 | Order the aspect before the low-level aspects in `[AspectOrder]` |
| LAMA0114 | Options: use `meta.This`/`meta.Base` only when the target isn't static; add `MustNotBeStatic()` to the aspect's eligibility |
| LAMA0115 | Options: `meta.Target.` member that exists for every target |
| LAMA0223 | Use `meta.Proceed()` |
| LAMA0247 | Add `ReturnType().MustBe<T>()` to the aspect's eligibility |
| LAMA0287 | Options: use `meta.Receiver` only when the target has a receiver; add `MustNotBeStatic()` to the eligibility |
| LAMA0401 | Add `[RunTimeOrCompileTime]` to the type |
| LAMA0535 | Options: make the type partial; remove `IsPartial = true` from the template |
| LAMA0650 | Use `InvokerOptions.Final` |
| LAMA0652 | Options: remove `virtual` from the property; make it `sealed` |
| LAMA0661 | Rename the variable so it doesn't hide the parameter |
| LAMA0700 | Remove the aspect from this declaration |
| LAMA0702 | Make the dependency member non-static (references get `this.`) |
| LAMA0703 | Remove the aspect from this declaration |
| LAMA0704 | Options: make the dependency type public; make the target type internal |
| LAMA0750 | Options: move the aspect to the type that uses the enum; remove the aspect |
| LAMA5111 | Remove the aspect from this declaration |

## Template authoring and compile-time scope

| Id | What the fix does |
|---|---|
| LAMA0020 | Use the reflection name in the string |
| LAMA0032 | Rename the template |
| LAMA0033 | Add `[Template]` to the member the advice names |
| LAMA0036 | Options: override the base template; rename the template |
| LAMA0040 | Options: make the fabric's parameterless constructor public; add one |
| LAMA0045 | Remove `SuggestAsLiveTemplate = true` |
| LAMA0060 | Return the member by value |
| LAMA0101 | Use LINQ method calls instead of the query |
| LAMA0104 | Options: wrap the value in `meta.RunTime(...)`; make the variable compile-time with `meta.CompileTime(...)`; wrap the value in `meta.CompileTime(...)` |
| LAMA0105 | Declare the variables separately (one declaration per variable) |
| LAMA0108 | Make the variable run-time (remove `meta.CompileTime`) |
| LAMA0117 | Add `[CompileTime]` to the member or helper type |
| LAMA0119 | Add `using Metalama.Framework.Aspects;` |
| LAMA0205 | Options: make the base type implement `ICompileTimeSerializable`; add a protected parameterless constructor |
| LAMA0207 | Add a public parameterless constructor to the serializer |
| LAMA0208 | Remove the duplicate serializer |
| LAMA0210 | Add a serializer to the record |
| LAMA0221 | Use `meta.This` |
| LAMA0224 | Options: declare the variable where it is first assigned; initialize it with `meta.Default(...)` |
| LAMA0225 | Options: initialize with `meta.Default(...)`; declare the variable as `object?` |
| LAMA0227 | Use `object` instead of `dynamic` in the type |
| LAMA0229 | Move the nested type to the namespace level |
| LAMA0230 | Make the nested type private |
| LAMA0231 | Options: derive the nested type from `TypeFabric`; move it to the namespace level |
| LAMA0232 | Options: use a regular string literal; remove `MetalamaTemplateLanguageVersion` |
| LAMA0233 | Use the replacement the message names (e.g. `ExpressionFactory.This()`) |
| LAMA0236 | Options: introduce the member into the target type (`[Introduce]`); mark the referenced type `[RunTimeOrCompileTime]` |
| LAMA0237 | Options: make the property virtual with a default implementation; mark its type `[RunTimeOrCompileTime]` |
| LAMA0238 | Options: change the member's type from `dynamic`; introduce it into the target type (`[Introduce]`) |
| LAMA0242 | Use `.Value` |
| LAMA0244 | Options: remove the base type; mark it `[RunTimeOrCompileTime]` |
| LAMA0245 | Remove `meta.CompileTime(...)` around the value |
| LAMA0246 | Use `object?` instead of `dynamic` in the local function |
| LAMA0248 | Remove `unsafe` |
| LAMA0249 | Remove `unsafe` |
| LAMA0250 | Options: remove the cast to `dynamic`; remove `as dynamic`; use `meta.Default(...)` |
| LAMA0251 | Declare the variable with `var` |
| LAMA0252 | Merge the partial template into one method |
| LAMA0253 | Options: pass a run-time member of the object (e.g. its name or value) |
| LAMA0254 | Options: `ConfigureAwait( true )`; `ConfigureAwait( false )`; choose the literal with a compile-time `if` |
| LAMA0256 | Options: cast the argument to `IExpression`; pass the qualifier instead |
| LAMA0260 | Options: remove the scope attribute from the type; remove the type fabric |
| LAMA0261 | Options: remove one of the duplicate template or advice attributes |
| LAMA0262 | Options: move the attribute from the accessor to the property or event; remove it from the accessor |
| LAMA0263 | Options: cast the lambda body to `object?`; cast it to `IExpression` |
| LAMA0264 | Remove `[CompileTime]` from the parameter |
| LAMA0267 | Replace the method group with a lambda |
| LAMA0272 | Remove the redundant `return` |
| LAMA0274 | Make the type implement `ITemplateProvider` |
| LAMA0275 | Pass all arguments to the template |
| LAMA0276 | Mark the type parameters `[CompileTime]` |
| LAMA0278 | Make the struct a class |
| LAMA0279 | Options: make the method virtual with an empty default implementation; remove `IsEmpty` from `[Template]` |
| LAMA0281 | Options: remove the attribute; replace an `[InlineArray]` struct with an array; remove the scope attribute (keep it run-time) |
| LAMA0282 | Replace the raw string literals with regular ones |
| LAMA0283 | Replace the primary constructor with an explicit constructor |
| LAMA0284 | Options: make the lambda run-time or compile-time with `meta.RunTime(...)` / `meta.CompileTime(...)` |
| LAMA0285 | Options: move the local function to a method of the type; remove its attribute |
| LAMA0286 | Use `ExpressionFactory.Capture( ... )` |
| LAMA0291 | Options: add `[CompileTime]`; set `MetalamaRoslynIsCompileTimeOnly = false` in the project |
| LAMA0292 | Options: move the scope attribute from the member to the type; mark the type `[CompileTime]` |
| LAMA0294 | Options: replace an `[InlineArray]` struct with an array; remove the scope attribute (keep it run-time) |

## Introducing members and interfaces

`WhenExists` options set the strategy on the `[Introduce]` attribute or the advice call (`OverrideStrategy`); *Remove WhenExists* restores the default, which fails.

| Id | What the fix does |
|---|---|
| LAMA0500 | Options: `WhenExists` = Override, Ignore, New (only when the member is inherited) |
| LAMA0502 | Options: make the inherited member virtual (or remove `sealed`); `WhenExists` = New, Ignore |
| LAMA0503 | Change the template's type to the existing member's type |
| LAMA0504 | Rename the existing member |
| LAMA0505 | Options: introduce the member as static; make the target class non-static |
| LAMA0506 | Remove the existing member |
| LAMA0507 | Options: don't make the member virtual; introduce it as an instance member |
| LAMA0508 | Options: don't make the member sealed; introduce it as an instance member |
| LAMA0509 | Options: `WhenExists` = Override, Ignore, New (only when the member is inherited) |
| LAMA0511 | Change the template's type to the interface member's type |
| LAMA0512 | Options: `WhenExists` = Ignore, Override |
| LAMA0514 | Options: `WhenExists` = Ignore, MakeExplicit (interface member override strategy) |
| LAMA0516 | Options: `WhenExists` = Ignore, Override; remove WhenExists |
| LAMA0517 | Make the template an automatic property with the missing accessor |
| LAMA0518 | Options: remove the extra accessor; implement implicitly (remove `IsExplicit`) |
| LAMA0519 | Options: make the template public; implement explicitly |
| LAMA0521 | Options: `WhenExists` = Ignore, Override, New on `IntroduceAttribute`; remove the attribute from the target |
| LAMA0522 | Options: `WhenExists` = Override, Ignore; remove WhenExists |
| LAMA0527 | Introduce the indexer as an instance member |
| LAMA0528 | Make the existing member public |
| LAMA0529 | Options: make the inherited member virtual (or remove `sealed`); `WhenExists` = Ignore |
| LAMA0531 | Rename the introduced type |
| LAMA0532 | Make the target type abstract |
| LAMA0533 | Options: `WhenExists` = Ignore; remove WhenExists |
| LAMA0534 | Move the aspect to the types implementing the interface (they become partial) |
| LAMA0540 | Make the class static |
| LAMA0542 | Don't make the indexer init-only |
| LAMA0543 | Options: make the template public; remove the modifier from the template |
| LAMA0550 | Options: make `Initialize` public virtual; make the class sealed |
| LAMA0551 | Add an `InitializationContext` parameter to the base constructor |

## Metalama.Patterns

### Contracts

| Id | What the fix does |
|---|---|
| LAMA5001 | Change the member's type to one the contract supports |
| LAMA5002 | Options: make the member non-nullable; remove the contract |
| LAMA5003 | Options: remove the redundant contract; replace it with `[StrictlyPositive]` |
| LAMA5004 | Options: enable invariant suspension in a project fabric; remove `[SuspendInvariants]` |
| LAMA5005 | Options: remove `[SuspendInvariants]`; add an `[Invariant]` method to the type |
| LAMA5006 | Options: remove the redundant contract; replace it with `[StrictlyPositive]` |
| LAMA5007 | Options: replace the contract with its strict or non-strict variant |

### Immutability

| Id | What the fix does |
|---|---|
| LAMA5020 | Make the field readonly |
| LAMA5021 | Options: remove the setter; replace `set` with `init` |
| LAMA5022 | Options: use an immutable collection type; mark the member's type deeply immutable; make the type shallowly immutable; remove `[Immutable]` |

### Caching

| Id | What the fix does |
|---|---|
| LAMA5100 | Options: add `[Cache]` to the method; remove `[InvalidateCache]` |
| LAMA5101 | Set `IgnoreThisParameter = true` on `[Cache]` |
| LAMA5102 | Rename the parameter to the name the cached method uses |
| LAMA5103 | Change the parameter's type to the cached method's type |
| LAMA5104 | Use `nameof(...)` of the cached method |
| LAMA5105 | Use `nameof(...)` of the cached method |
| LAMA5106 | Use `nameof(...)` of the cached method |
| LAMA5107 | Set `AllowMultipleOverloads = true` |
| LAMA5110 | Options: set `UseDependencyInjection = false`; make the method non-static |
| LAMA5112 | Remove the duplicate `[Cache]` (keep the fabric's) |

### Observability

| Id | What the fix does |
|---|---|
| LAMA5150 | Options: make the method (protected) virtual; add `protected virtual void OnPropertyChanged(string)`; remove `[Observable]` |
| LAMA5151 | Options: remove `InpcDiagnosticCommentVerbosity` from the project; set it to 0 |
| LAMA5154 | Options: remove `virtual` from the property; add `[NotObservable]` |
| LAMA5156 | Add the `OnPropertyChanged(string)` method to the type |
| LAMA5161 | Options: add `[Observable]`; add `[Immutable]`; add `[SuppressObservabilityWarnings]` to the computed member |
| LAMA5162 | Options: add `[Constant]` to the method; make it static |
| LAMA5164 | Options: replace the field with an auto-property; make it private; make it readonly |
| LAMA5166 | Options: add `[Observable]`; add `[Immutable]`; add `[SuppressObservabilityWarnings]` to the computed member |

### Commands (WPF)

| Id | What the fix does |
|---|---|
| LAMA5201 | Remove the property from `[Command]` |
| LAMA5202 | Options: make the property public; set `EnableINotifyPropertyChangedIntegration = false` on `[Command]` |
| LAMA5203 | Remove the member the aspect generates |
| LAMA5206 | Options: use `nameof(...)` of an existing member; remove the property from the attribute |
| LAMA5208 | Options: set `Background = true` on `[Command]`; remove the `CancellationToken` parameter |

## Analyzers, architecture, interceptors

| Id | What the fix does |
|---|---|
| LAMA0870 | Options: mark the type `[Durable]`; mark the member `[Durable]` (interfaces, `object`, delegates) |
| LAMA0871 | Remove `[Durable]` from the member that receives the value |
| LAMA0872 | Remove `[Durable]` from the parameter |
| LAMA0873 | Options: mark the base class `[Durable]`; remove `[Durable]` from the type |
| LAMA0874 | Remove the redundant `[Durable]` |
| LAMA0875 | Rename the member to end with `Dangerous` |
| LAMA0876 | Mark the type or the member `[Durable]` |
| LAMA0877 | Options: make the field readonly or private; remove or make private the setter |
| LAMA0878 | Make the lambda static |
| LAMA0880 | Options: make the field readonly or private; remove or make private the setter |
| LAMA0881 | Options: make the field readonly or private; replace `set` with `init`; remove or make private the setter |
| LAMA0882 | Options: mark the type `[ImmutableType]`; declare it immutable in the project (`MetalamaImmutableType`) |
| LAMA0883 | Options: mark the base class `[ImmutableType]`; declare it immutable in the project |
| LAMA0884 | Options: mark the type `[ImmutableType]`; declare it immutable in the project |
| LAMA0886 | Remove the redundant `[ImmutableType]` |
| LAMA0900 | Mark the declaration `[Experimental]` |
| LAMA0901 | Options: move the implementation to the interface's project; remove `[InternalOnlyImplement]` |
| LAMA0902 | Options: use the regular expression equivalent; use the star-pattern variant |
| LAMA0903 | Options: rename the declaration to match; use the star pattern |
| LAMA0906 | Options: rename the declaration to match; use the star pattern |
| LAMA0907 | Options: make the constructor public; add a constructor taking `ReferencePredicateBuilder` |
| LAMA0908 | Options: make the constructor public; add a constructor taking `ReferencePredicateBuilder` |
| LAMA1008 | Change the method name to the closest existing one |
| LAMA1017 | Place the interceptor in the calling type (`.PlaceInCallingType()`) |

## Project settings and language version

| Id | What the fix does |
|---|---|
| LAMA0051 | Options: set `LangVersion` to a supported version; set `MetalamaAllowPreviewLanguageFeatures` to true |
| LAMA0052 | Options: set the language version property to a supported version |
| LAMA0053 | Options: append to `$(DefineConstants)` instead of overwriting it; set `MetalamaEnabled` to False |
| LAMA0066 | Options: remove a formatting property from the project; set `MetalamaFormatOutput` to False |
| LAMA0080 | Options: append to `$(DefineConstants)` instead of overwriting it; set `MetalamaEnabled` to False |
| LAMA0083 | Set the timeout property to 600000 (10 minutes) |
| LAMA0084 | Remove `MetalamaCompileTimeTargetFrameworks` from the project |

LAMA0232 and LAMA0282 (raw string literals in templates) are under [Template authoring](#template-authoring-and-compile-time-scope).

## Crashes and licensing

| Id | What the fix does |
|---|---|
| LAMA0001 | Options: open the crash report; rebuild the project |
| LAMA0041 | Options: open the crash report; rebuild the project |
| LAMA0049 | Options: open the crash report; rebuild the project |
| LAMA0064 | Options: open the crash report; rebuild the project |
| LAMA0073 | Options: open the crash report; rebuild the project |
| LAMA0074 | Options: open the crash report; rebuild the project |
| LAMA0088 | Options: open the crash report; rebuild the project |
| LAMA0099 | Options: open the crash report; rebuild the project |
| LAMA0109 | Options: open the crash report; rebuild the project |
| LAMA0118 | Build the project to resume Metalama analysis |
| LAMA0222 | Options: open `errors.txt` of the named directory; rebuild the project |
| LAMA0295 | Options: open the crash report; rebuild the project |
| LAMA0405 | Options: open the crash report; rebuild the project |
| LAMA0524 | Options: open the crash report; rebuild the project |
| LAMA0806 | How to register a Metalama license (message with `metalama license register`/`try`) |
| LAMA0807 | How to fix the ignored license key (message with `metalama license list`) |
