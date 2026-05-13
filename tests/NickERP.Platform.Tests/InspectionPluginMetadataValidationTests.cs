using System.Reflection;
using NickERP.Inspection.Authorities.CustomsGh;
using NickERP.Inspection.ExternalSystems.IcumsGh;
using NickERP.Inspection.ExternalSystems.Mock;
using NickERP.Inspection.Inference.Mock;
using NickERP.Inspection.Inference.OCR.ContainerNumber;
using NickERP.Inspection.Inference.OnnxRuntime;
using NickERP.Inspection.Scanners.Ase;
using NickERP.Inspection.Scanners.FS6000;
using NickERP.Inspection.Scanners.Mock;
using NickERP.Platform.Plugins;

namespace NickERP.Platform.Tests;

public class InspectionPluginMetadataValidationTests
{
    private const string ExpectedModule = "inspection";

    private static readonly IReadOnlyDictionary<string, Type> ExpectedPluginTypes =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["NickERP.Inspection.Authorities.CustomsGh"] = typeof(CustomsGhRulesProvider),
            ["NickERP.Inspection.ExternalSystems.IcumsGh"] = typeof(IcumsGhAdapter),
            ["NickERP.Inspection.ExternalSystems.Mock"] = typeof(MockExternalSystemAdapter),
            ["NickERP.Inspection.Inference.Mock"] = typeof(MockInferenceRunner),
            ["NickERP.Inspection.Inference.OCR.ContainerNumber"] = typeof(ContainerNumberRecognizer),
            ["NickERP.Inspection.Inference.OnnxRuntime"] = typeof(OnnxRuntimeRunner),
            ["NickERP.Inspection.Scanners.Ase"] = typeof(AseScannerAdapter),
            ["NickERP.Inspection.Scanners.FS6000"] = typeof(FS6000ScannerAdapter),
            ["NickERP.Inspection.Scanners.Mock"] = typeof(MockScannerAdapter)
        };

    [Fact]
    public void Inspection_plugin_source_manifests_are_loadable_and_declare_required_loader_metadata()
    {
        var manifestPaths = EnumerateSourceManifests();
        manifestPaths.Should().HaveCount(ExpectedPluginTypes.Count);

        foreach (var manifestPath in manifestPaths)
        {
            var manifest = PluginManifest.LoadFrom(manifestPath);

            manifest.Module.Should().Be(ExpectedModule, because: $"{manifestPath} is an inspection plugin");
            manifest.TypeCode.Should().NotBeNullOrWhiteSpace();
            manifest.Contracts.Should().NotBeNullOrEmpty();
            manifest.Contracts.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c));
            manifest.MinHostContractVersion.Should().NotBeNullOrWhiteSpace(
                because: $"{manifestPath} must pin the minimum plugin contract version");
            Version.TryParse(manifest.MinHostContractVersion, out _).Should().BeTrue(
                because: $"{manifestPath} must use a parseable major.minor contract version");
        }
    }

    [Fact]
    public void Inspection_plugin_attributes_match_manifest_module_and_type_code()
    {
        foreach (var manifestPath in EnumerateSourceManifests())
        {
            var pluginDirectory = Path.GetFileName(Path.GetDirectoryName(manifestPath))!;
            ExpectedPluginTypes.Should().ContainKey(pluginDirectory);

            var manifest = PluginManifest.LoadFrom(manifestPath);
            var pluginType = ExpectedPluginTypes[pluginDirectory];
            var attribute = pluginType.GetCustomAttribute<PluginAttribute>(inherit: false);

            attribute.Should().NotBeNull($"{pluginType.FullName} must be discoverable by PluginLoader");
            attribute!.TypeCode.Should().Be(manifest.TypeCode);
            attribute.Module.Should().Be(manifest.Module);
        }
    }

    private static IReadOnlyList<string> EnumerateSourceManifests()
    {
        var pluginsRoot = Path.Combine(FindRepositoryRoot(), "modules", "inspection", "plugins");
        Directory.Exists(pluginsRoot).Should().BeTrue($"plugins root should exist at {pluginsRoot}");

        return Directory.EnumerateFiles(pluginsRoot, PluginManifest.FileName, SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "modules", "inspection", "plugins");
            if (Directory.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find repository root from test base directory {AppContext.BaseDirectory}.");
    }
}
