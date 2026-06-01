using System.Reflection;
using NetArchTest.Rules;
using TestResult = NetArchTest.Rules.TestResult;

namespace FileIt.Architecture.Test;

/// <summary>
/// Same architecture boundaries as ArchitectureRules, but reported ONE RESULT
/// PER MODULE instead of one combined result per rule. When a new module is
/// added, its own row passes or fails independently, so you can see at a glance
/// which module violated which boundary. The global rules (Domain/Infra
/// direction, interface naming, async-void) stay in ArchitectureRules because
/// they have no per-module axis.
/// </summary>
[TestClass]
public class PerModuleArchitectureRules
{
    private static Assembly Load(string name) => Assembly.Load(name);

    // One record per module: the App assembly, the Host assembly, and the
    // module's namespace stem. Adding a new module = add one line here.
    public sealed record Module(string Name, string AppAsm, string HostAsm, string Stem);

    private static readonly Module[] Modules =
    {
        new("Services",  "FileIt.Module.Services.App",  "FileIt.Module.Services.Host",  "FileIt.Module.Services"),
        new("SimpleFlow","FileIt.Module.SimpleFlow.App","FileIt.Module.SimpleFlow.Host","FileIt.Module.SimpleFlow"),
        new("DataFlow",  "FileIt.Module.DataFlow.App",  "FileIt.Module.DataFlow.Host",  "FileIt.Module.DataFlow"),
        new("Complex",   "FileIt.Module.Complex.App",   "FileIt.Module.Complex.Host",   "FileIt.Module.Complex"),
    };

    // MSTest feeds this to each [DataTestMethod]; each row becomes its own result.
    public static IEnumerable<object[]> ModuleData =>
        Modules.Select(m => new object[] { m });

    // Display name shows the module, so results read "Rule_... (Services)" etc.
    public static string ModuleName(MethodInfo method, object[] data) =>
        $"{method.Name}({((Module)data[0]).Name})";
        
    private static void AssertPass(TestResult result, string ruleDescription)
    {
        if (!result.IsSuccessful)
        {
            var failing = result.FailingTypeNames ?? Array.Empty<string>();
            Assert.Fail($"{ruleDescription}\nOffending types:\n  {string.Join("\n  ", failing)}");
        }
    }

    private static string[] OtherStems(Module m, string suffix) =>
        Modules.Where(x => x.Name != m.Name).Select(x => x.Stem + suffix).ToArray();

    // ---- one result per module ----

    [DataTestMethod]
    [DynamicData(nameof(ModuleData), DynamicDataDisplayName = nameof(ModuleName))]
    public void Module_AppReferencesDomain(Module m)
    {
        var has = Types.InAssembly(Load(m.AppAsm))
            .That().HaveDependencyOn("FileIt.Domain")
            .GetTypes().Any();
        Assert.IsTrue(has, $"{m.AppAsm} must use FileIt.Domain interfaces");
    }

    [DataTestMethod]
    [DynamicData(nameof(ModuleData), DynamicDataDisplayName = nameof(ModuleName))]
    public void Module_AppDoesNotReferenceOwnHost(Module m)
    {
        var result = Types.InAssembly(Load(m.AppAsm))
            .Should().NotHaveDependencyOn(m.HostAsm).GetResult();
        AssertPass(result, $"{m.AppAsm} must not depend on its own Host");
    }

    [DataTestMethod]
    [DynamicData(nameof(ModuleData), DynamicDataDisplayName = nameof(ModuleName))]
    public void Module_AppDoesNotReferenceOtherApps(Module m)
    {
        var result = Types.InAssembly(Load(m.AppAsm))
            .Should().NotHaveDependencyOnAny(OtherStems(m, ".App")).GetResult();
        AssertPass(result, $"{m.AppAsm} must not reference other module App projects");
    }

    [DataTestMethod]
    [DynamicData(nameof(ModuleData), DynamicDataDisplayName = nameof(ModuleName))]
    public void Module_HostDoesNotReferenceOtherHosts(Module m)
    {
        var result = Types.InAssembly(Load(m.HostAsm))
            .Should().NotHaveDependencyOnAny(OtherStems(m, ".Host")).GetResult();
        AssertPass(result, $"{m.HostAsm} must not reference other module Host projects");
    }

    [DataTestMethod]
    [DynamicData(nameof(ModuleData), DynamicDataDisplayName = nameof(ModuleName))]
    public void Module_HostDoesNotReferenceOtherApps(Module m)
    {
        var result = Types.InAssembly(Load(m.HostAsm))
            .Should().NotHaveDependencyOnAny(OtherStems(m, ".App")).GetResult();
        AssertPass(result, $"{m.HostAsm} must not reference other module App projects");
    }

    [DataTestMethod]
    [DynamicData(nameof(ModuleData), DynamicDataDisplayName = nameof(ModuleName))]
    public void Module_AppPublicTypesUnderAppNamespace(Module m)
    {
        var result = Types.InAssembly(Load(m.AppAsm))
            .That().ArePublic()
            .Should().ResideInNamespaceStartingWith(m.Stem + ".App").GetResult();
        AssertPass(result, $"Public types in {m.AppAsm} must live under {m.Stem}.App");
    }

    [DataTestMethod]
    [DynamicData(nameof(ModuleData), DynamicDataDisplayName = nameof(ModuleName))]
    public void Module_HostPublicTypesUnderHostNamespace(Module m)
    {
        var result = Types.InAssembly(Load(m.HostAsm))
            .That().ArePublic()
            .And().DoNotHaveName("Program")
            .And().DoNotHaveName("<Module>")
            .Should().ResideInNamespaceStartingWith(m.Stem + ".Host").GetResult();
        AssertPass(result, $"Public types in {m.HostAsm} must live under {m.Stem}.Host");
    }

    [DataTestMethod]
    [DynamicData(nameof(ModuleData), DynamicDataDisplayName = nameof(ModuleName))]
    public void Module_AppDoesNotUseAzureSdkDirectly(Module m)
    {
        var result = Types.InAssembly(Load(m.AppAsm))
            .Should().NotHaveDependencyOnAny(
                "Azure.Messaging.ServiceBus",
                "Azure.Storage.Blobs",
                "Microsoft.EntityFrameworkCore")
            .GetResult();
        AssertPass(result, $"{m.AppAsm}: App must not depend on Azure SDK or EF directly");
    }
}
