using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace konstitech.DotNetDeck.Foundation.Tests;

public class FoundationContractTests
{
    private const string Prefix = "konstitech.DotNetDeck.";
    private const string Plan = "Projektplan.NETDeck.v1.0-Lean-MVP.md";
    private const string OldPlan = "Projektplan: .NETDeck (v1.0 Lean MVP)";
    private const string LicenseHash = "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4";
    private const string PlanHash = "032c80168aa4304f1b6e2caa96d30137bc6ef63e45a53bee32f474cb040410de";
    private const string TestCommand = "dotnet run --project tests/konstitech.DotNetDeck.Foundation.Tests/konstitech.DotNetDeck.Foundation.Tests.csproj -c Release --no-restore -- --treenode-filter \"/*/konstitech.DotNetDeck.Foundation.Tests/*/*\"";
    private static readonly string[] Projects = ["Domain", "Application", "Infrastructure", "Tui", "Foundation.Tests"];
    private static string Root => FindRoot();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static IGitProcess Real(ProcessStartInfo info) => new RealGitProcess(info);

    [Test]
    public async Task ProjectsRespectLockedDependencyGraph()
    {
        var solution = XDocument.Load(Path.Combine(Root, "dotnet-deck.slnx"));
        var expectedPaths = Projects.Select(ProjectPath).Order().ToArray();
        var actualPaths = solution.Descendants("Project").Select(e => (string)e.Attribute("Path")!).Order().ToArray();
        await Assert.That(expectedPaths.SequenceEqual(actualPaths)).IsTrue();
        await Assert.That(solution.Descendants("Folder").Select(e => (string)e.Attribute("Name")!).SequenceEqual(new[] { "/src/", "/tests/" })).IsTrue();
        for (int i = 0; i < Projects.Length; i++)
        {
            var project = LoadProject(Projects[i]);
            await Assert.That((string?)project.Root!.Attribute("Sdk")).IsEqualTo("Microsoft.NET.Sdk");
            await Assert.That(Value(project, "RootNamespace")).IsEqualTo(Prefix + Projects[i]);
            var references = project.Descendants("ProjectReference").Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/'))).ToArray();
            await Assert.That(references.SequenceEqual(i == 0 ? [] : new[] { Prefix + Projects[i == 4 ? 3 : i - 1] })).IsTrue();
            await Assert.That(project.Descendants("FrameworkReference")).IsEmpty();
            await Assert.That(Value(project, "OutputType")).IsEqualTo(i >= 3 ? "Exe" : null);
            var packages = project.Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!).Order().ToArray();
            var expected = i == 3 ? new[] { "Terminal.Gui" } : i == 4 ? new[] { "NSubstitute", "TUnit" } : Array.Empty<string>();
            await Assert.That(packages.SequenceEqual(expected.Order())).IsTrue();
        }
        var source = Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/')).Order().ToArray();
        await Assert.That(source.SequenceEqual(new[] { "Program.cs", "TerminalSessionOutcome.cs", "TuiApplication.cs" }.Select(n => "src/" + Prefix + "Tui/" + n).Order())).IsTrue();
    }

    [Test]
    public async Task CentralConfigurationMatchesPlan()
    {
        var props = XDocument.Load(Path.Combine(Root, "Directory.Build.props"));
        var expected = new Dictionary<string, string> { ["TargetFramework"] = "net10.0", ["LangVersion"] = "14.0", ["Nullable"] = "enable", ["ImplicitUsings"] = "enable", ["TreatWarningsAsErrors"] = "true", ["Deterministic"] = "true", ["IsPackable"] = "false", ["Authors"] = "konstitech", ["Product"] = ".NETDeck", ["Copyright"] = "Copyright 2026 konstitech", ["PackageLicenseExpression"] = "Apache-2.0" };
        var groups = props.Root!.Elements("PropertyGroup").ToArray();
        await Assert.That(groups).HasSingleItem();
        var group = groups.Single();
        await Assert.That(group.Attribute("Condition")).IsNull();
        await Assert.That(group.Elements().Count()).IsEqualTo(expected.Count);
        foreach (var pair in expected) await Assert.That(Value(props, pair.Key)).IsEqualTo(pair.Value);
        var packages = XDocument.Load(Path.Combine(Root, "Directory.Packages.props"));
        await Assert.That(Value(packages, "ManagePackageVersionsCentrally")).IsEqualTo("true");
        var pins = packages.Descendants("PackageVersion").ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!);
        var expectedPins = new Dictionary<string, string> { ["Terminal.Gui"] = "2.5.0", ["TUnit"] = "1.85.0", ["NSubstitute"] = "5.3.0" };
        await Assert.That(pins.OrderBy(p => p.Key).SequenceEqual(expectedPins.OrderBy(p => p.Key))).IsTrue();
        using var global = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "global.json")));
        var sdk = global.RootElement.GetProperty("sdk");
        await Assert.That(sdk.GetProperty("version").GetString()).IsEqualTo("10.0.115");
        await Assert.That(sdk.GetProperty("rollForward").GetString()).IsEqualTo("latestPatch");
        await Assert.That(sdk.GetProperty("allowPrerelease").GetBoolean()).IsFalse();
        foreach (var name in Projects)
        {
            var project = LoadProject(name);
            foreach (var reference in project.Descendants("PackageReference"))
            {
                await Assert.That(reference.Attribute("Version")).IsNull();
                await Assert.That(reference.Attribute("VersionOverride")).IsNull();
                await Assert.That(reference.Element("Version")).IsNull();
                await Assert.That(reference.Element("VersionOverride")).IsNull();
            }
            foreach (var forbidden in new[] { "NoWarn", "WarningsNotAsErrors", "SuppressTrimAnalysisWarnings", "EnableTrimAnalyzer" }) await Assert.That(Value(project, forbidden)).IsNull();
        }
        var tui = LoadProject("Tui");
        foreach (var pair in new Dictionary<string, string> { ["AssemblyName"] = "dotnet-deck", ["PublishAot"] = "true", ["ILLinkTreatWarningsAsErrors"] = "true", ["IlcTreatWarningsAsErrors"] = "true", ["TrimmerSingleWarn"] = "false" }) await Assert.That(Value(tui, pair.Key)).IsEqualTo(pair.Value);
        var visible = tui.Descendants("InternalsVisibleTo").ToArray();
        await Assert.That(visible).HasSingleItem();
        await Assert.That((string?)visible.Single().Attribute("Include")).IsEqualTo(Prefix + "Foundation.Tests");
        var test = LoadProject("Foundation.Tests");
        foreach (var pair in new Dictionary<string, string> { ["OutputType"] = "Exe", ["IsTestProject"] = "true", ["IsPackable"] = "false", ["IsAotCompatible"] = "false", ["PublishAot"] = "false" }) await Assert.That(Value(test, pair.Key)).IsEqualTo(pair.Value);
        var runners = test.Descendants("PackageReference").Where(e => (string?)e.Attribute("Include") == "TUnit").ToArray();
        await Assert.That(runners).HasSingleItem();
        await Assert.That((string?)runners.Single().Attribute("PrivateAssets") ?? (string?)runners.Single().Element("PrivateAssets")).IsEqualTo("all");
    }

    [Test]
    public async Task ExistingPlanAndLicenseAreUnchanged()
    {
        await Assert.That(Digest(File.ReadAllBytes(Path.Combine(Root, "LICENSE")))).IsEqualTo(LicenseHash);
        await Assert.That(File.Exists(Path.Combine(Root, Plan))).IsTrue();
        await Assert.That(Digest(File.ReadAllBytes(Path.Combine(Root, Plan)))).IsEqualTo(PlanHash);
        await Assert.That(File.Exists(Path.Combine(Root, OldPlan)) || Directory.Exists(Path.Combine(Root, OldPlan))).IsFalse();
    }

    [Test]
    public async Task DocumentationAndWorkflowDescribeActualFoundation()
    {
        var readme = File.ReadAllText(Path.Combine(Root, "README.md"));
        var workflow = File.ReadAllText(Path.Combine(Root, ".github/workflows/native-aot.yml")).Replace("\r\n", "\n");
        foreach (var section in new[] { "Status", "Prerequisites", "Build and run", "NativeAOT", "Verification", "Plan review", "License" }) await Assert.That(readme.Contains("## " + section, StringComparison.Ordinal)).IsTrue();
        foreach (var text in new[] { "10.0.115", "C# 14", "Terminal.Gui 2.5.0", "TUnit 1.85.0", "not implemented", "unmeasured", "unverified", "value-based", Plan, "dotnet restore dotnet-deck.slnx", TestCommand, "dotnet run --project src/konstitech.DotNetDeck.Tui/", "linux-x64", "osx-arm64", "win-x64", "Linux linux-x64 NativeAOT publish was observed before this portability remediation; macOS osx-arm64 and Windows win-x64 publish remain unverified.", "The pre-remediation foundation suite passed 28 tests; the portability remediation has not yet been verified." }) await Assert.That(readme.Contains(text, StringComparison.Ordinal)).IsTrue();
        foreach (var text in new[] { "name: NativeAOT", "push:", "pull_request:", "workflow_dispatch:", "contents: read", "cancel-in-progress: true", "${{ github.workflow }}-${{ github.ref }}", "timeout-minutes: 20", "fail-fast: false", "os: ubuntu-24.04\n            rid: linux-x64", "os: macos-15\n            rid: osx-arm64", "os: windows-2022\n            rid: win-x64", "actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683", "actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9", "persist-credentials: false", "dotnet-version: 10.0.115", "dotnet restore dotnet-deck.slnx", TestCommand, "dotnet publish src/konstitech.DotNetDeck.Tui/" }) await Assert.That(workflow.Contains(text, StringComparison.Ordinal)).IsTrue();
        foreach (var forbidden in new[] { "continue-on-error", "upload-artifact", "gh release", "dotnet nuget push", "dotnet test tests/konstitech.DotNetDeck.Foundation.Tests/", "FullyQualifiedName~" }) await Assert.That(workflow.Contains(forbidden, StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(readme.Contains("Projektplan%3A", StringComparison.Ordinal)).IsFalse();
        await Assert.That(readme.Contains("dotnet test tests/konstitech.DotNetDeck.Foundation.Tests/", StringComparison.Ordinal)).IsFalse();
        await Assert.That(File.ReadAllText(Path.Combine(Root, ".gitattributes")).Replace("\r\n", "\n")).IsEqualTo("/" + Plan + " -text\n/LICENSE -text\n");
        var paths = Directory.GetFiles(Path.Combine(Root, "src"), "*", SearchOption.AllDirectories).Concat(Directory.GetFiles(Path.Combine(Root, "tests"), "*", SearchOption.AllDirectories)).Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")).Concat(new[] { "README.md", "Directory.Build.props", "Directory.Packages.props", "global.json", "dotnet-deck.slnx", Plan, ".github/workflows/native-aot.yml" }.Select(p => Path.Combine(Root, p)));
        foreach (var path in paths)
        {
            var content = File.ReadAllText(path);
            foreach (var old in new[] { "Kost" + "eash", "kost" + "eash", "Konsti" + "tech", "konsti" + "teck" }) await Assert.That(content.Contains(old, StringComparison.Ordinal)).IsFalse();
        }
    }

    [Test]
    public async Task RepositoryPathsArePortableAcrossSupportedPlatforms()
    {
        var candidates = await ReadGitPathsAsync(Root, ["-c", "core.attributesFile=", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], Real);
        var deleted = await ReadGitPathsAsync(Root, ["-c", "core.attributesFile=", "ls-files", "-z", "--deleted"], Real);
        var paths = candidates.Except(deleted, StringComparer.Ordinal).ToArray();
        await Assert.That(paths.Length > 0).IsTrue();
        await Assert.That(paths.Contains(Plan, StringComparer.Ordinal)).IsTrue();
        await Assert.That(paths.Contains(OldPlan, StringComparer.Ordinal)).IsFalse();
        foreach (var path in paths) await Assert.That(IsPortableRepositoryPath(path)).IsTrue().Because($"Nonportable repository path: {path}");
    }

    [Test]
    [TUnit.Core.Arguments("Projektplan.NETDeck.v1.0-Lean-MVP.md", true)]
    [TUnit.Core.Arguments(".github/workflows/native-aot.yml", true)]
    [TUnit.Core.Arguments("src/konstitech.DotNetDeck.Tui/Program.cs", true)]
    [TUnit.Core.Arguments("folder/COM10.txt", true)]
    [TUnit.Core.Arguments("Projektplan: .NETDeck (v1.0 Lean MVP)", false)]
    [TUnit.Core.Arguments("folder/bad<name>.md", false)]
    [TUnit.Core.Arguments("folder/bad\"name.md", false)]
    [TUnit.Core.Arguments("folder/bad|name.md", false)]
    [TUnit.Core.Arguments("folder/bad?name.md", false)]
    [TUnit.Core.Arguments("folder/bad*name.md", false)]
    [TUnit.Core.Arguments("folder\\name.md", false)]
    [TUnit.Core.Arguments("folder/trailing.", false)]
    [TUnit.Core.Arguments("folder/trailing ", false)]
    [TUnit.Core.Arguments("folder/CON.md", false)]
    [TUnit.Core.Arguments("folder/aux", false)]
    [TUnit.Core.Arguments("folder/NUL.tar.gz", false)]
    [TUnit.Core.Arguments("folder/PrN.log", false)]
    [TUnit.Core.Arguments("folder/COM1.txt", false)]
    [TUnit.Core.Arguments("folder/LPT9.txt", false)]
    [TUnit.Core.Arguments("folder/COM¹.txt", false)]
    [TUnit.Core.Arguments("folder/LPT².txt", false)]
    [TUnit.Core.Arguments("folder/COM³.txt", false)]
    [TUnit.Core.Arguments("folder/control\u001f.md", false)]
    [TUnit.Core.Arguments("folder/null\u0000.md", false)]
    [TUnit.Core.Arguments("", false)]
    [TUnit.Core.Arguments("/absolute.md", false)]
    [TUnit.Core.Arguments("folder//name.md", false)]
    [TUnit.Core.Arguments("./name.md", false)]
    [TUnit.Core.Arguments("folder/../name.md", false)]
    public async Task RepositoryPathRulesRejectNonPortableNames(string path, bool expected) => await Assert.That(IsPortableRepositoryPath(path)).IsEqualTo(expected);

    private static bool IsPortableRepositoryPath(string path)
    {
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part[^1] is ' ' or '.') return false;
            if (part.Any(c => c < 32 || "<>:\"|?*\\".Contains(c, StringComparison.Ordinal))) return false;
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) return false;
            if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string ProjectPath(string name) => (name == "Foundation.Tests" ? "tests/" : "src/") + Prefix + name + "/" + Prefix + name + ".csproj";
    [Test]
    public async Task FreshCheckoutPreservesProtectedDocumentBytesWithAutoCrlf()
    {
        var parent = Path.Combine(Path.GetTempPath(), "netdeck-checkout-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(parent, "source");
        var checkout = Path.Combine(parent, "checkout");
        var hooks = Path.Combine(parent, "empty-hooks");
        var attributes = File.ReadAllBytes(Path.Combine(Root, ".gitattributes"));
        var license = File.ReadAllBytes(Path.Combine(Root, "LICENSE"));
        var plan = File.ReadAllBytes(Path.Combine(Root, Plan));
        var control = Utf8.GetBytes("first\nsecond\n");
        await Assert.That(attributes.SequenceEqual(Utf8.GetBytes("/" + Plan + " -text\n/LICENSE -text\n"))).IsTrue();
        await Assert.That(Digest(license)).IsEqualTo(LicenseHash);
        await Assert.That(Digest(plan)).IsEqualTo(PlanHash);
        try
        {
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(hooks);
            async Task<GitCommandOutput> Git(string cwd, params string[] args) =>
                await RunGitAsync(cwd, new[] { "-c", "core.attributesFile=", "-c", "core.hooksPath=" + hooks }.Concat(args).ToArray(), Real);
            await Git(parent, "-c", "init.templateDir=", "init", "--initial-branch=fixture", source);
            File.WriteAllBytes(Path.Combine(source, ".gitattributes"), attributes);
            File.WriteAllBytes(Path.Combine(source, "LICENSE"), license);
            File.WriteAllBytes(Path.Combine(source, Plan), plan);
            File.WriteAllBytes(Path.Combine(source, "conversion-control.txt"), control);
            await Git(source, "config", "--local", "core.autocrlf", "false");
            await Git(source, "add", "--", ".gitattributes", "LICENSE", Plan, "conversion-control.txt");
            await Git(source, "-c", "user.name=Foundation Fixture", "-c", "user.email=foundation@example.invalid", "-c", "commit.gpgSign=false", "commit", "-m", "checkout-fixture");
            var names = new[] { ".gitattributes", "LICENSE", Plan, "conversion-control.txt" };
            var inputs = new[] { attributes, license, plan, control };
            var blobs = new byte[names.Length][];
            for (int i = 0; i < names.Length; i++)
            {
                blobs[i] = (await Git(source, "cat-file", "blob", "HEAD:" + names[i])).StandardOutput;
                await Assert.That(blobs[i].SequenceEqual(inputs[i])).IsTrue();
            }
            await Assert.That(Digest(blobs[1])).IsEqualTo(LicenseHash);
            await Assert.That(Digest(blobs[2])).IsEqualTo(PlanHash);
            await Git(parent, "clone", "--no-local", "--no-checkout", "--", source, checkout);
            await Assert.That(File.Exists(Path.Combine(checkout, "LICENSE"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(checkout, Plan))).IsFalse();
            await Git(checkout, "config", "--local", "core.autocrlf", "true");
            await Git(checkout, "config", "--local", "core.eol", "crlf");
            await Git(checkout, "config", "--local", "core.safecrlf", "false");
            await Git(checkout, "checkout", "--force", "fixture");
            await Assert.That(Utf8.GetString((await Git(checkout, "config", "--local", "--get", "core.autocrlf")).StandardOutput).Trim()).IsEqualTo("true");
            for (int i = 1; i <= 2; i++)
            {
                var actual = File.ReadAllBytes(Path.Combine(checkout, names[i]));
                await Assert.That(actual.SequenceEqual(blobs[i])).IsTrue();
                await Assert.That(actual.SequenceEqual(inputs[i])).IsTrue();
                await Assert.That(Digest(actual)).IsEqualTo(Digest(blobs[i]));
                await Assert.That(Digest(actual)).IsEqualTo(i == 1 ? LicenseHash : PlanHash);
            }
            var converted = File.ReadAllBytes(Path.Combine(checkout, names[3]));
            await Assert.That(converted.SequenceEqual(Utf8.GetBytes("first\r\nsecond\r\n"))).IsTrue();
            await Assert.That(converted.SequenceEqual(blobs[3])).IsFalse();
            await Assert.That(File.ReadAllText(Path.Combine(checkout, ".gitattributes")).Replace("\r\n", "\n")).IsEqualTo(Utf8.GetString(attributes));
            await Assert.That(File.Exists(Path.Combine(checkout, OldPlan))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                foreach (var file in Directory.EnumerateFiles(parent, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(parent, true);
            }
        }
    }

    [Test]
    public async Task GitStartFailureFailsAndDisposesWithoutReading()
    {
        var fake = new ScriptedGitProcess { StartFailure = new Win32Exception(2) };
        ProcessStartInfo? captured = null;
        int count = 0;
        await Assert.That(async () => await RunGitAsync(Root, ["ls-files", "-z"], info => { count++; captured = info; return fake; })).ThrowsExactly<GitCommandFailureException>().WithMessage("GIT001: Git start failed.");
        await Assert.That(count).IsEqualTo(1);
        await Assert.That(fake.Calls.SequenceEqual(new[] { "start", "dispose" })).IsTrue();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.FileName).IsEqualTo("git");
        await Assert.That(captured.WorkingDirectory).IsEqualTo(Root);
        await Assert.That(captured.ArgumentList.SequenceEqual(new[] { "ls-files", "-z" })).IsTrue();
        await Assert.That(captured.UseShellExecute).IsFalse();
        await Assert.That(captured.RedirectStandardOutput && captured.RedirectStandardError && captured.CreateNoWindow).IsTrue();
        await Assert.That(captured.StandardErrorEncoding!.DecoderFallback is DecoderExceptionFallback).IsTrue();
        await Assert.That(captured.Environment["GIT_CONFIG_NOSYSTEM"]).IsEqualTo("1");
        await Assert.That(captured.Environment["GIT_CONFIG_GLOBAL"]).IsEqualTo(OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
        await Assert.That(captured.Environment["GIT_TERMINAL_PROMPT"]).IsEqualTo("0");
        await Assert.That(captured.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).Order().SequenceEqual(new[] { "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_TERMINAL_PROMPT" })).IsTrue();
    }

    [Test]
    public async Task GitNonzeroExitIncludesStderrAndDisposes()
    {
        var fake = new ScriptedGitProcess { Output = Utf8.GetBytes("ignored.md\0"), Error = "fatal: fixture failure", ConfiguredExitCode = 7 };
        await Assert.That(async () => await RunGitAsync(Root, [], _ => fake)).ThrowsExactly<GitCommandFailureException>().WithMessage("GIT002: Git exited with code 7. stderr: fatal: fixture failure");
        await Assert.That(fake.Calls.SequenceEqual(new[] { "start", "stdout", "stderr", "wait", "exit-code", "dispose" })).IsTrue();
        await Assert.That(fake.ObservedTimeout).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    [TUnit.Core.Arguments("", new string[0])]
    [TUnit.Core.Arguments("one.md\0", new[] { "one.md" })]
    [TUnit.Core.Arguments("one.md\0two.md\0", new[] { "one.md", "two.md" })]
    public async Task GitNulOutputParsesCompleteRecords(string output, string[] expected) => await Assert.That(ParseGitNulPaths(output).SequenceEqual(expected)).IsTrue();

    [Test]
    [TUnit.Core.Arguments("one.md")]
    [TUnit.Core.Arguments("one.md\0two.md")]
    [TUnit.Core.Arguments("\0")]
    [TUnit.Core.Arguments("one.md\0\0")]
    public async Task GitMalformedNulOutputFailsAfterDisposal(string output)
    {
        var fake = new ScriptedGitProcess { Output = Utf8.GetBytes(output) };
        await Assert.That(async () => await ReadGitPathsAsync(Root, [], _ => fake)).ThrowsExactly<GitCommandFailureException>().WithMessage("GIT003: Malformed NUL-delimited Git path output.");
        await Assert.That(fake.Calls.SequenceEqual(new[] { "start", "stdout", "stderr", "wait", "exit-code", "dispose" })).IsTrue();
    }

    [Test]
    [TUnit.Core.Arguments(false)]
    [TUnit.Core.Arguments(true)]
    public async Task GitTimeoutKillsAndDisposesWithoutAwaitingUnfinishedPipes(bool killThrows)
    {
        var fake = new ScriptedGitProcess { Completes = false, KillFailure = killThrows ? new InvalidOperationException("fixture kill failure") : null };
        await Assert.That(async () => await RunGitAsync(Root, [], _ => fake)).ThrowsExactly<GitCommandFailureException>().WithMessage("GIT004: Git timed out after 10 seconds." + (killThrows ? " Kill failed: InvalidOperationException." : ""));
        await Assert.That(fake.Calls.SequenceEqual(new[] { "start", "stdout", "stderr", "wait", "kill:true", "dispose" })).IsTrue();
        await Assert.That(fake.ObservedTimeout).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    [TUnit.Core.Arguments("stdout-sync", false)]
    [TUnit.Core.Arguments("stdout-sync", true)]
    [TUnit.Core.Arguments("stderr-sync", false)]
    [TUnit.Core.Arguments("stderr-sync", true)]
    [TUnit.Core.Arguments("stdout-task", false)]
    [TUnit.Core.Arguments("stdout-task", true)]
    [TUnit.Core.Arguments("stderr-task", false)]
    [TUnit.Core.Arguments("stderr-task", true)]
    [TUnit.Core.Arguments("wait", false)]
    [TUnit.Core.Arguments("wait", true)]
    [TUnit.Core.Arguments("exit-code", false)]
    [TUnit.Core.Arguments("exit-code", true)]
    public async Task GitProcessFaultsPreserveClassificationAndDisposeOnce(string faultSite, bool disposeThrows)
    {
        var failure = new IOException("fixture I/O failure");
        var fake = new ScriptedGitProcess {
            StandardOutputFailure = faultSite.StartsWith("stdout", StringComparison.Ordinal) ? failure : null,
            StandardErrorFailure = faultSite.StartsWith("stderr", StringComparison.Ordinal) ? failure : null,
            ReadFailuresAreSynchronous = faultSite.EndsWith("sync", StringComparison.Ordinal),
            WaitFailure = faultSite == "wait" ? failure : null,
            ExitCodeFailure = faultSite == "exit-code" ? failure : null,
            DisposeFailure = disposeThrows ? new InvalidOperationException("fixture disposal failure") : null
        };
        int factories = 0;
        await Assert.That(async () => await RunGitAsync(Root, [], _ => { factories++; return fake; })).ThrowsExactly<GitCommandFailureException>().WithMessage("GIT005: Git process I/O failed." + (disposeThrows ? " Dispose failed: InvalidOperationException." : ""));
        string[] calls = faultSite switch {
            "stdout-sync" => ["start", "stdout", "dispose"],
            "stderr-sync" => ["start", "stdout", "stderr", "dispose"],
            "exit-code" => ["start", "stdout", "stderr", "wait", "exit-code", "dispose"],
            _ => ["start", "stdout", "stderr", "wait", "dispose"]
        };
        await Assert.That(fake.Calls.SequenceEqual(calls)).IsTrue();
        await Assert.That(fake.ObservedTimeout).IsEqualTo(calls.Contains("wait") ? TimeSpan.FromSeconds(10) : null);
        await Assert.That(factories).IsEqualTo(1);
    }

    [Test]
    public async Task GitDisposalFailureAfterSuccessFailsOnce()
    {
        var fake = new ScriptedGitProcess { DisposeFailure = new InvalidOperationException("fixture disposal failure") };
        int count = 0;
        await Assert.That(async () => await RunGitAsync(Root, [], _ => { count++; return fake; })).ThrowsExactly<GitCommandFailureException>().WithMessage("GIT005: Git process disposal failed.");
        await Assert.That(fake.Calls.SequenceEqual(new[] { "start", "stdout", "stderr", "wait", "exit-code", "dispose" })).IsTrue();
        await Assert.That(fake.ObservedTimeout).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    [TUnit.Core.Arguments("start")]
    [TUnit.Core.Arguments("exit")]
    [TUnit.Core.Arguments("timeout")]
    [TUnit.Core.Arguments("timeout-kill")]
    public async Task GitPrimaryFailureSurvivesDisposalFailure(string primary)
    {
        var fake = new ScriptedGitProcess {
            StartFailure = primary == "start" ? new Win32Exception(2) : null,
            ConfiguredExitCode = primary == "exit" ? 7 : 0,
            Error = primary == "exit" ? "fatal: fixture failure" : "",
            Completes = !primary.StartsWith("timeout", StringComparison.Ordinal),
            KillFailure = primary == "timeout-kill" ? new InvalidOperationException("fixture kill failure") : null,
            DisposeFailure = new InvalidOperationException("fixture disposal failure")
        };
        var message = primary switch {
            "start" => "GIT001: Git start failed.",
            "exit" => "GIT002: Git exited with code 7. stderr: fatal: fixture failure",
            "timeout-kill" => "GIT004: Git timed out after 10 seconds. Kill failed: InvalidOperationException.",
            _ => "GIT004: Git timed out after 10 seconds."
        };
        int count = 0;
        await Assert.That(async () => await RunGitAsync(Root, [], _ => { count++; return fake; })).ThrowsExactly<GitCommandFailureException>().WithMessage(message + " Dispose failed: InvalidOperationException.");
        string[] calls = primary switch {
            "start" => ["start", "dispose"],
            "exit" => ["start", "stdout", "stderr", "wait", "exit-code", "dispose"],
            _ => ["start", "stdout", "stderr", "wait", "kill:true", "dispose"]
        };
        await Assert.That(fake.Calls.SequenceEqual(calls)).IsTrue();
        await Assert.That(fake.ObservedTimeout).IsEqualTo(primary == "start" ? null : TimeSpan.FromSeconds(10));
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    [TUnit.Core.Arguments("invalid-utf8")]
    [TUnit.Core.Arguments("leading-bom")]
    public async Task GitInvalidUtf8OrBomFailsAfterDisposal(string payload)
    {
        var fake = new ScriptedGitProcess { Output = payload == "invalid-utf8" ? [0xC3, 0x28, 0x00] : [0xEF, 0xBB, 0xBF, 0x61, 0x00] };
        int count = 0;
        await Assert.That(async () => await ReadGitPathsAsync(Root, [], _ => { count++; return fake; })).ThrowsExactly<GitCommandFailureException>().WithMessage("GIT003: Malformed NUL-delimited Git path output.");
        await Assert.That(fake.Calls.SequenceEqual(new[] { "start", "stdout", "stderr", "wait", "exit-code", "dispose" })).IsTrue();
        await Assert.That(fake.ObservedTimeout).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(count).IsEqualTo(1);
    }

    private sealed record GitCommandOutput(byte[] StandardOutput, string StandardError);
    private sealed class GitCommandFailureException(string message) : Exception(message);

    private interface IGitProcess : IDisposable
    {
        void Start();
        Task<byte[]> ReadStandardOutputAsync();
        Task<string> ReadStandardErrorAsync();
        Task<bool> WaitForCompletionAsync(TimeSpan timeout);
        int ExitCode { get; }
        void Kill(bool entireProcessTree);
    }

    private sealed class RealGitProcess(ProcessStartInfo startInfo) : IGitProcess
    {
        private readonly Process process = new() { StartInfo = startInfo };
        private Task<byte[]>? stdout;
        private Task<string>? stderr;
        private Task? completion;
        public void Start() { if (!process.Start()) throw new InvalidOperationException(); }
        public Task<byte[]> ReadStandardOutputAsync() => stdout ??= Observe(ReadBytesAsync());
        private async Task<byte[]> ReadBytesAsync()
        {
            using var buffer = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(buffer).ConfigureAwait(false);
            return buffer.ToArray();
        }
        public Task<string> ReadStandardErrorAsync() => stderr ??= Observe(process.StandardError.ReadToEndAsync());
        private static Task<T> Observe<T>(Task<T> task)
        {
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
        public async Task<bool> WaitForCompletionAsync(TimeSpan timeout)
        {
            completion = Task.WhenAll(process.WaitForExitAsync(), stdout!, stderr!);
            _ = completion.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try { await completion.WaitAsync(timeout).ConfigureAwait(false); return true; }
            catch (TimeoutException) { return false; }
        }
        public int ExitCode => process.ExitCode;
        public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);
        public void Dispose() => process.Dispose();
    }

    private static async Task<GitCommandOutput> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, Func<ProcessStartInfo, IGitProcess> createProcess)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Utf8, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        IGitProcess process;
        try { process = createProcess(info); }
        catch (Exception) { throw new GitCommandFailureException("GIT001: Git start failed."); }
        string? failure = null;
        GitCommandOutput? result = null;
        try
        {
            try { process.Start(); }
            catch (Exception) { failure = "GIT001: Git start failed."; }
            if (failure is null)
            {
                try
                {
                    var stdout = process.ReadStandardOutputAsync();
                    var stderr = process.ReadStandardErrorAsync();
                    if (!await process.WaitForCompletionAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
                    {
                        failure = "GIT004: Git timed out after 10 seconds.";
                        try { process.Kill(true); }
                        catch (Exception ex) { failure += $" Kill failed: {ex.GetType().Name}."; }
                    }
                    else
                    {
                        var output = await stdout.ConfigureAwait(false);
                        var error = await stderr.ConfigureAwait(false);
                        int code = process.ExitCode;
                        if (code != 0) failure = $"GIT002: Git exited with code {code.ToString(CultureInfo.InvariantCulture)}. stderr: {error}";
                        else result = new(output, error);
                    }
                }
                catch (Exception) { failure = "GIT005: Git process I/O failed."; }
            }
        }
        finally
        {
            try { process.Dispose(); }
            catch (Exception ex) { failure = failure is null ? "GIT005: Git process disposal failed." : failure + $" Dispose failed: {ex.GetType().Name}."; }
        }
        if (failure is not null) throw new GitCommandFailureException(failure);
        return result!;
    }

    private static async Task<string[]> ReadGitPathsAsync(string workingDirectory, IReadOnlyList<string> arguments, Func<ProcessStartInfo, IGitProcess> createProcess)
    {
        var output = await RunGitAsync(workingDirectory, arguments, createProcess).ConfigureAwait(false);
        try
        {
            var text = Utf8.GetString(output.StandardOutput);
            if (text.StartsWith('\uFEFF')) throw new GitCommandFailureException("GIT003: Malformed NUL-delimited Git path output.");
            return ParseGitNulPaths(text);
        }
        catch (DecoderFallbackException) { throw new GitCommandFailureException("GIT003: Malformed NUL-delimited Git path output."); }
    }

    private static string[] ParseGitNulPaths(string output)
    {
        if (output.Length == 0) return [];
        var parts = output.Split('\0');
        if (parts[^1].Length != 0 || parts.Take(parts.Length - 1).Any(p => p.Length == 0))
            throw new GitCommandFailureException("GIT003: Malformed NUL-delimited Git path output.");
        return parts[..^1];
    }

    private sealed class ScriptedGitProcess : IGitProcess
    {
        internal List<string> Calls { get; } = [];
        internal Exception? StartFailure { get; init; }
        internal Exception? KillFailure { get; init; }
        internal Exception? StandardOutputFailure { get; init; }
        internal Exception? StandardErrorFailure { get; init; }
        internal bool ReadFailuresAreSynchronous { get; init; }
        internal Exception? WaitFailure { get; init; }
        internal Exception? ExitCodeFailure { get; init; }
        internal Exception? DisposeFailure { get; init; }
        internal byte[] Output { get; init; } = [];
        internal string Error { get; init; } = "";
        internal int ConfiguredExitCode { get; init; }
        internal bool Completes { get; init; } = true;
        internal TimeSpan? ObservedTimeout { get; private set; }
        private Task<byte[]>? stdout;
        private Task<string>? stderr;
        public void Start() { Calls.Add("start"); if (StartFailure is not null) throw StartFailure; }
        public Task<byte[]> ReadStandardOutputAsync()
        {
            Calls.Add("stdout");
            if (StandardOutputFailure is not null && ReadFailuresAreSynchronous) throw StandardOutputFailure;
            return stdout ??= StandardOutputFailure is not null ? Task.FromException<byte[]>(StandardOutputFailure) : Completes ? Task.FromResult(Output) : new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
        public Task<string> ReadStandardErrorAsync()
        {
            Calls.Add("stderr");
            if (StandardErrorFailure is not null && ReadFailuresAreSynchronous) throw StandardErrorFailure;
            return stderr ??= StandardErrorFailure is not null ? Task.FromException<string>(StandardErrorFailure) : Completes ? Task.FromResult(Error) : new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
        public async Task<bool> WaitForCompletionAsync(TimeSpan timeout)
        {
            Calls.Add("wait");
            ObservedTimeout = timeout;
            if (WaitFailure is not null) throw WaitFailure;
            if (!Completes) return false;
            await Task.WhenAll(stdout!, stderr!).ConfigureAwait(false);
            return true;
        }
        public int ExitCode { get { Calls.Add("exit-code"); if (ExitCodeFailure is not null) throw ExitCodeFailure; return ConfiguredExitCode; } }
        public void Kill(bool entireProcessTree) { Calls.Add($"kill:{entireProcessTree.ToString().ToLowerInvariant()}"); if (KillFailure is not null) throw KillFailure; }
        public void Dispose() { Calls.Add("dispose"); if (DisposeFailure is not null) throw DisposeFailure; }
    }

    private static XDocument LoadProject(string name) => XDocument.Load(Path.Combine(Root, ProjectPath(name)));
    private static string? Value(XDocument doc, string name) => doc.Descendants(name).SingleOrDefault()?.Value;
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "dotnet-deck.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate dotnet-deck.slnx above test output.");
    }
}
