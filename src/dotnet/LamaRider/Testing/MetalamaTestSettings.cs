using System;
using System.Linq;
using LamaRider.Model;
using System.Collections.Immutable;
using JetBrains.Application.Settings.Implementation;
using JetBrains.Application.Parts;
using JetBrains.Application.Settings;
using JetBrains.Application.Settings.Storage.DefaultFileStorages;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.DataContext;
using JetBrains.ProjectModel.Settings.Storages;
using JetBrains.ReSharper.UnitTestFramework.Exploration.Artifacts;
using JetBrains.ReSharper.UnitTestFramework.Settings;
using JetBrains.ReSharper.UnitTestProvider.Xunit;

namespace LamaRider.Testing;

/// <summary>
/// The Rider settings Metalama's docs ask for when a solution has aspect tests (Metalama.Testing.AspectTesting):
/// xUnit discovery by Test Runner (metadata discovery misses aspect tests), and no orphaned-process prompt for
/// diff tools that failing aspect tests open.
/// </summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public sealed class MetalamaTestSettings(ISolution solution, ISettingsStore settingsStore)
{
    public const string Shared = "Shared", Personal = "Personal", Global = "Global";

    // Diff tools DiffEngine (used by aspect tests) launches; their windows outlive the test run.
    static readonly string[] DiffToolProcesses =
        ["BCompare", "WinMergeU", "Code", "rider64", "devenv", "kdiff3", "Meld", "p4merge", "TortoiseMerge", "DiffMerge", "idea64"];

    ContextRange SolutionContext => ContextRange.Smart(solution.ToDataContext2());

    public bool IsConfigured() =>
        settingsStore.BindToContextTransient(SolutionContext).GetValue((XunitProviderSettings s) => s.TestDiscoveryFromArtifactsMethod)
        == DiscoveryMethod.TestRunner;

    /// <summary>Effective values (all layers) for the solution.</summary>
    public (bool Configured, string Text) Status(bool? usesAspectTests)
    {
        var store = settingsStore.BindToContextTransient(SolutionContext);
        var ignored = DiffToolProcesses.Count(p =>
            store.GetIndexedValue((UnitTestRunnerSettings s) => s.SpawnedProcessesKnownList, p) == SpawnedProcessesResponse.DoNothing);
        return TestSettingsStatus.Describe(IsConfigured(), ignored, DiffToolProcesses.Length, usesAspectTests);
    }

    /// <returns>Human-readable description of where the settings went.</returns>
    public string Apply(string scope)
    {
        var (store, where) = scope switch
        {
            // Explicit value: teammates may have Metadata in their own global layer; a smart write would skip a value equal to ours.
            Shared => (ExplicitWriter(solution.GetComponent<SolutionSettingsStorageShared>().MountPoint), $"{solution.Name}.sln.DotSettings (team-shared)"),
            Personal => (Writer(solution.GetComponent<SolutionSettingsStoragePersonal>().MountPoint, SolutionContext), $"{solution.Name}.sln.DotSettings.user (personal)"),
            Global => (settingsStore.BindToContextTransient(ContextRange.ApplicationWide), "this computer (global)"),
            _ => throw new ArgumentException($"Unknown settings scope '{scope}'", nameof(scope)),
        };
        store.SetValue((XunitProviderSettings s) => s.TestDiscoveryFromArtifactsMethod, DiscoveryMethod.TestRunner);
        foreach (var process in DiffToolProcesses)
            store.SetIndexedValue((UnitTestRunnerSettings s) => s.SpawnedProcessesKnownList, process, SpawnedProcessesResponse.DoNothing);
        return where;
    }

    IContextBoundSettingsStore Writer(ISettingsStorageMountPoint mountPoint, ContextRange context) =>
        GlobalSettings.BindForSmartWritingToMountPointOverrideReadonly(settingsStore, mountPoint, context)();

    // Bound to this layer only: values are compared with schema defaults, not with the user's other layers.
    // Same writable wrapping as GlobalSettings.BindForSmartWritingToMountPointOverrideReadonly (solution layers are read-only mounts).
    IContextBoundSettingsStore ExplicitWriter(ISettingsStorageMountPoint mountPoint)
    {
        var writable = mountPoint;
        if ((mountPoint.Flags & MountPointFlags.IsWritable) == 0)
        {
            writable = new SettingsStorageMountPoint(mountPoint.Storage, mountPoint.PathInStorage, MountPointFlags.IsWritable, mountPoint.Priority, mountPoint.IsAvailable, mountPoint.Name);
            foreach (var property in mountPoint.Metadata.Properties.Values)
                writable.Metadata.AsUntyped.Set(property.Id, property.GetValue(null));
        }
        return settingsStore.BindToMountPointsTransient(new ImmutableContextBoundMountPoints(ImmutableArray.Create(writable), writable));
    }
}
