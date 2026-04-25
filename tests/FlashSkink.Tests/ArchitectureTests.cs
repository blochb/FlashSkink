using System.Reflection;
using Xunit;

namespace FlashSkink.Tests;

public class ArchitectureTests
{
    private static Assembly GetAssembly(string name)
    {
        // Force load by touching the referenced project's assembly
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == name);
        return loaded ?? Assembly.Load(name);
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
    public void Presentation_DoesNotReference_Avalonia()
    {
        var refs = GetAssembly("FlashSkink.Presentation").GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);
        Assert.DoesNotContain(refs, r => r.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UI_DoesNotReference_Core_Directly()
    {
        var refs = GetAssembly("FlashSkink.UI.Avalonia").GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);
        Assert.DoesNotContain(refs, r => r.Equals("FlashSkink.Core", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CLI_DoesNotReference_Presentation()
    {
        var refs = GetAssembly("FlashSkink.CLI").GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);
        Assert.DoesNotContain(refs, r => r.Equals("FlashSkink.Presentation", StringComparison.OrdinalIgnoreCase));
    }
}
