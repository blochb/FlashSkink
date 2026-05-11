using System.Reflection;
using Xunit;

namespace FlashSkink.Tests;

public class ArchitectureTests
{
    private static readonly string[] ForbiddenUiAssemblyPrefixes =
    [
        "Avalonia",
        "System.Windows",
        "PresentationFramework",
        "WindowsBase",
        "Microsoft.Maui",
        "Microsoft.UI",
        "CommunityToolkit.Mvvm",
        "FlashSkink.Presentation",
        "FlashSkink.UI.",
    ];

    private static Assembly GetAssembly(string name)
    {
        // Force load by touching the referenced project's assembly
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == name);
        return loaded ?? Assembly.Load(name);
    }

    private static void AssertNoForbiddenUiReference(string assemblyName)
    {
        var refs = GetAssembly(assemblyName).GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        foreach (var forbidden in ForbiddenUiAssemblyPrefixes)
        {
            Assert.DoesNotContain(refs, r => r.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Core_DoesNotReference_Avalonia()
    {
        var refs = GetAssembly("FlashSkink.Core").GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);
        Assert.DoesNotContain(refs, r => r.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CoreAbstractions_DoesNotReference_Avalonia()
    {
        var refs = GetAssembly("FlashSkink.Core.Abstractions").GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);
        Assert.DoesNotContain(refs, r => r.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CLI_DoesNotReference_Avalonia()
    {
        var refs = GetAssembly("FlashSkink.CLI").GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);
        Assert.DoesNotContain(refs, r => r.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Core_DoesNotReference_AnyUiOrPresentationAssembly()
    {
        AssertNoForbiddenUiReference("FlashSkink.Core");
    }

    [Fact]
    public void CoreAbstractions_DoesNotReference_AnyUiOrPresentationAssembly()
    {
        AssertNoForbiddenUiReference("FlashSkink.Core.Abstractions");
    }

    [Fact]
    public void CLI_DoesNotReference_AnyUiOrPresentationAssembly()
    {
        AssertNoForbiddenUiReference("FlashSkink.CLI");
    }
}
