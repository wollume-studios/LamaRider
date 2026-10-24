"""Builds samples/MetalamaFixSamples from Metalama's public-domain documentation samples (the Metalama skill's
code/Metalama.Documentation.SampleCode.Errors): per diagnostic, a project with the code that reports it (compiled) and
the fixed code (.expected, not compiled). LamaRider's fix self-test applies each quick-fix and compares the result with
the .expected files.

Usage: python tools/make-fix-samples.py <path to Metalama.Documentation.SampleCode.Errors>
"""
import os
import re
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sln_writer import write_solution  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "samples", "MetalamaFixSamples")
VERSION = "2026.1.30"
# Not available for Metalama 2026.1 or not buildable on Linux: interceptors, WPF, SDK weavers.
SKIP_USINGS = ("Metalama.Extensions.Interceptors", "Metalama.Patterns.Wpf", "Metalama.Framework.Engine")
GROUPS = {
    "Core": ["Metalama.Framework"],
    "Patterns": ["Metalama.Framework", "Metalama.Patterns.Contracts", "Metalama.Patterns.Observability", "Metalama.Patterns.Caching.Aspects",
                 "Metalama.Patterns.Immutability", "Metalama.Extensions.DependencyInjection"],
    "Architecture": ["Metalama.Framework", "Metalama.Extensions.Architecture"],
}
PROPS = """<Project>
  <!-- Every sample reports a diagnostic on purpose: its build is expected to fail. -->
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
"""
PROJECT = """<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
{refs}
  </ItemGroup>

</Project>
"""


def group_of(text):
    if "Metalama.Extensions.Architecture" in text:
        return "Architecture"
    if "Metalama.Patterns." in text or "Metalama.Extensions.DependencyInjection" in text:
        return "Patterns"
    return "Core"


def write(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as out:
        out.write(text)


BATCH = 30


def main(source):
    os.makedirs(ROOT, exist_ok=True)
    for entry in os.listdir(ROOT):  # the folder itself may be in use (a shell's working directory)
        path = os.path.join(ROOT, entry)
        shutil.rmtree(path) if os.path.isdir(path) else os.remove(path)
    samples = []
    for sample in sorted(os.listdir(source)):
        folder = os.path.join(source, sample)
        if not re.fullmatch(r"LAMA[0-9]{4}", sample) or not os.path.isdir(folder):
            continue
        files = [f for f in os.listdir(folder) if f.endswith(".cs") and not f.endswith(".t.cs")]
        error = [f for f in files if f.startswith(sample + "_Error")]
        fixed = [f for f in files if f.startswith(sample + "_Fixed")]
        if not error or not fixed:
            continue
        text = "".join(open(os.path.join(folder, f), encoding="utf-8").read() for f in error)
        if any(u in text for u in SKIP_USINGS):
            continue
        # One project per sample: an error in one sample's compile-time code stops Metalama for its whole project.
        target = os.path.join(ROOT, sample)
        os.makedirs(target)
        for f in error:
            shutil.copy(os.path.join(folder, f), os.path.join(target, f))
        for f in fixed:
            shutil.copy(os.path.join(folder, f), os.path.join(target, f + ".expected"))
        refs = "\n".join(f'    <PackageReference Include="{p}" Version="{VERSION}" />' for p in GROUPS[group_of(text)])
        write(os.path.join(target, sample + ".csproj"), PROJECT.format(refs=refs))
        samples.append(sample)

    write(os.path.join(ROOT, "Directory.Build.props"), PROPS)
    # One solution of everything, and batches of BATCH projects: Rider watches every project's folder, and Linux allows
    # only 128 inotify instances per user by default, so the self-test opens one batch at a time.
    write_solution(os.path.join(ROOT, "MetalamaFixSamples.sln"), samples, 0)
    for n in range(0, len(samples), BATCH):
        write_solution(os.path.join(ROOT, "Batch%02d.sln" % (n // BATCH + 1)), samples[n:n + BATCH], n)
    print("samples:", len(samples))


if __name__ == "__main__":
    main(sys.argv[1])
