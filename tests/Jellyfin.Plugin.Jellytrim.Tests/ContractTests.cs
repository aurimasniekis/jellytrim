using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Jellytrim.Api;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Jellyfin.Plugin.Jellytrim.Tests;

/// <summary>Guards that catch a future change quietly weakening a safety property.</summary>
public class ContractTests
{
    private static readonly string RepoRoot = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellytrim.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static IEnumerable<MethodInfo> Actions() => typeof(JellytrimController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static IEnumerable<string> SourceFiles() => new[] { "src" }
        .SelectMany(d => Directory.EnumerateFiles(Path.Combine(RepoRoot, d), "*.cs", SearchOption.AllDirectories))
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void Controller_requires_elevation()
    {
        var attribute = typeof(JellytrimController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(Policies.RequiresElevation, attribute?.Policy);
    }

    [Fact]
    public void Plugin_types_stay_out_of_the_servers_flat_openapi_schema_namespace() =>
        Assert.True(typeof(JellytrimController).GetCustomAttribute<ApiExplorerSettingsAttribute>()?.IgnoreApi);

    [Fact]
    public void No_action_opts_out_of_authorization()
    {
        Assert.NotEmpty(Actions());
        foreach (var action in Actions())
        {
            Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.Null(action.GetCustomAttribute<AuthorizeAttribute>());
        }

        Assert.Null(typeof(JellytrimController).GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void The_plugin_exposes_exactly_one_controller() =>
        Assert.Equal([typeof(JellytrimController)], typeof(JellytrimController).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t)));

    [Fact]
    public void Requests_never_carry_a_path_as_deletion_authority()
    {
        // Plan requests take ids and filters. The only string search is a title match, and nothing named "path" is accepted.
        var request = typeof(global::Jellytrim.Core.Cleanup.PlanRequest).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(request, n => n.Contains("Path", StringComparison.OrdinalIgnoreCase));
        var execute = typeof(global::Jellytrim.Core.Cleanup.ExecuteRequest).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(execute, n => n.Contains("Path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Jellyfin_file_deletion_is_never_requested()
    {
        var hits = SourceFiles().Where(f => Regex.IsMatch(File.ReadAllText(f), @"DeleteFileLocation\s*=\s*true")).ToList();
        Assert.Empty(hits);
        var unsafeFast = SourceFiles().Where(f => File.ReadAllText(f).Contains("DeleteItemsUnsafeFast", StringComparison.Ordinal)).ToList();
        Assert.Empty(unsafeFast);
    }

    [Fact]
    public void There_is_no_recursive_delete_anywhere()
    {
        var hits = SourceFiles().Where(f => Regex.IsMatch(File.ReadAllText(f), @"Directory\.Delete\([^)]*(true|recursive:\s*true)")).ToList();
        Assert.Empty(hits);
    }

    [Fact]
    public void Only_the_file_deleter_unlinks_files()
    {
        var hits = SourceFiles().Where(f => File.ReadAllText(f).Contains("File.Delete(", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();
        Assert.Equal(["CleanupCoordinator.cs"], hits);
    }

    [Fact]
    public void Core_stays_free_of_Jellyfin_references()
    {
        var core = typeof(global::Jellytrim.Core.Policies.PolicyEvaluator).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain(core, n => n!.StartsWith("Jellyfin", StringComparison.Ordinal) || n.StartsWith("MediaBrowser", StringComparison.Ordinal));
    }

    [Fact]
    public void No_scheduled_task_can_delete()
    {
        var task = File.ReadAllText(Path.Combine(RepoRoot, "src/Jellyfin.Plugin.Jellytrim/Runtime/ScanTask.cs"));
        Assert.DoesNotContain("CleanupCoordinator", task, StringComparison.Ordinal);
        var tasks = typeof(JellytrimController).Assembly.GetTypes().Where(t => typeof(MediaBrowser.Model.Tasks.IScheduledTask).IsAssignableFrom(t));
        Assert.Equal(["ScanTask"], tasks.Select(t => t.Name));
    }

    [Fact]
    public void Plugin_identity_matches_the_packaging_script()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "scripts/package.py"));
        Assert.Contains(Plugin.PluginGuid, script, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiled_against_the_same_Jellyfin_version_as_the_target_abi()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot, "Directory.Packages.props"));
        var version = Regex.Match(props, "<JellyfinVersion>([^<]+)<").Groups[1].Value;
        Assert.Contains($"TARGET_ABI = \"{version}.0\"", File.ReadAllText(Path.Combine(RepoRoot, "scripts/package.py")), StringComparison.Ordinal);
        Assert.Contains($"jellyfin/jellyfin:{version[..version.LastIndexOf('.')]}", File.ReadAllText(Path.Combine(RepoRoot, "deploy/compose.e2e.yml")), StringComparison.Ordinal);
    }
}
