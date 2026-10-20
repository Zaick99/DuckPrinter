using System.Text.Json;
using DuckPrinterManager.Core.Models;
using DuckPrinterManager.Core.Native;
using DuckPrinterManager.Core.Services;
using Xunit;

namespace DuckPrinterManager.Tests;

public class NativeCoreTests
{
    [Fact]
    public void HostsAndMacGoThroughRust()
    {
        var h = DuckCore.ParseHosts("PC01\npc01; PC02, 10.0.0.1  bad!host");
        Assert.Equal(["PC01", "PC02", "10.0.0.1"], h.Valid);
        Assert.Equal(1, h.Duplicates);
        Assert.Single(h.Invalid);
        Assert.Equal("001122aabbcc", DuckCore.NormalizeMac("00:11:22:AA:BB:CC"));
        Assert.Null(DuckCore.NormalizeMac("nope"));
        Assert.Equal("001122", DuckCore.SanitizeMacTyping("00-11-22"));
    }

    [Fact]
    public void QueueRoundTripThroughFfi()
    {
        using var core = new DuckCore();
        var p = new TaskPayload { DriverName = "HP", PrinterName = "HP (PR1)", PrinterAddress = "10.0.0.9" };
        var r1 = core.AddTasks(DuckTasks.Remove(["PC1"], new TaskPayload { RemoveTargets = ["10.0.0.9"] }));
        var r2 = core.AddTasks(DuckTasks.Deploy(["PC1", "PC2"], p));
        var dup = core.AddTasks(DuckTasks.Deploy(["pc2"], p));
        Assert.Single(r1.Added);
        Assert.Equal(2, r2.Added.Count);
        Assert.Single(dup.Duplicates);

        var (due, _) = core.Due(10);
        Assert.Equal(["remove", "deploy"], due.Select(t => t.Action));
        Assert.Equal("10.0.0.9", due[0].GetPayload().RemoveTargets[0]);
        Assert.Equal("HP (PR1)", due[1].GetPayload().PrinterName);

        core.Finish(due[0].Id, "ok", "rimossa");
        core.Finish(due[1].Id, "offline", "spento");
        var next = core.Due(10).Tasks;
        Assert.Single(next);
        Assert.Equal("PC1", next[0].Host);
        Assert.Equal("deploy", next[0].Action);

        Assert.Equal(1, core.Revision().Stats.Done);
        Assert.Contains(core.GetLog(due[1].Id), e => e.Msg.Contains("spento"));

        core.PutSecret("ad.password", "s3gr3t0");
        Assert.Equal("s3gr3t0", core.GetSecret("ad.password"));

        var path = Path.Combine(Path.GetTempPath(), $"duck-{Guid.NewGuid():N}.json");
        core.Save(path);
        using var core2 = new DuckCore();
        Assert.Equal(3, core2.Load(path));
        Assert.Null(core2.GetSecret("ad.password")); // i segreti non finiscono mai su disco
        File.Delete(path);
    }

    [Fact]
    public void CoreErrorsBecomeExceptions()
    {
        using var core = new DuckCore();
        Assert.Throws<DuckCoreException>(() => core.ClearTasks("boh"));
    }
}

public class NamingTests
{
    [Theory]
    [InlineData("{DRIVERNAME}", "HP Universal PCL 6", "HP Universal PCL 6")]
    [InlineData("{DRIVERNAME} (PR0042)", "HP Universal PCL 6", "HP Universal PCL 6 (PR0042)")]
    [InlineData("{drivername} - {IP}", "Kyocera", "Kyocera - 10.1.1.5")]
    [InlineData("Ufficio, piano 2", "X", "Ufficio piano 2")]
    [InlineData("", "Canon", "Canon")]
    public void PrinterName(string tpl, string drv, string expected) =>
        Assert.Equal(expected, Naming.RenderPrinterName(tpl, drv, "10.1.1.5"));

    [Fact]
    public void IncrementalNames()
    {
        Assert.Equal("PR0043", Naming.NextName("PR{INCREMENTAL}", ["PR0001", "PR0042", "PRX", "pr0010"]));
        Assert.Equal("PR0001", Naming.NextName("PR{INCREMENTAL}", []));
        Assert.Equal("PR000008", Naming.NextName("PR{INCREMENTAL:6}", ["PR7"]));
        Assert.Equal("ST-12-TS", Naming.NextName("ST-{INCREMENTAL}-TS", ["ST-11-TS", "ST-3-TS", "ST-09-TS"]));
        Assert.True(Naming.IsValidPattern("PR{INCREMENTAL:4}"));
        Assert.False(Naming.IsValidPattern("PR"));
        Assert.Equal("PR", Naming.PatternPrefix("PR{INCREMENTAL}"));
    }
}

public class InfTests
{
    private static string Sample(string n) => Path.Combine(AppContext.BaseDirectory, "Samples", n);

    [Fact]
    public void Utf16LocalizedContinuedLines()
    {
        var r = InfParser.Read(Sample("utf16.inf"));
        var names = r.Drivers.Select(d => d.DriverName).ToList();
        Assert.Contains("HP Universal Printing PCL 6", names);
        Assert.Contains("HP LaserJet Pro M404 PCL 6 (V3)", names);
        Assert.Contains("HP Vecchio x86", names);
        Assert.Equal("x64", r.Drivers.First(d => d.DriverName.StartsWith("HP Universal")).Architecture);
        Assert.Equal("x86", r.Drivers.Last().Architecture); // gli x64 vengono prima
        Assert.Equal("HP", r.Provider);
        Assert.Equal("Printer", r.Class);
        Assert.Equal("HP", r.Drivers[0].Manufacturer);
    }

    [Fact]
    public void HeuristicWhenNoManufacturer()
    {
        var r = InfParser.Read(Sample("nomfg.inf"));
        Assert.Contains(r.Drivers, d => d.DriverName == "Canon Generic Plus UFR II");
        Assert.NotEmpty(r.Warnings);
    }

    [Fact]
    public void FolderScanSurvivesGarbage()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.Copy(Sample("utf16.inf"), Path.Combine(dir, "a.inf"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllBytes(Path.Combine(dir, "sub", "rotto.INF"), [0, 1, 2, 3, 255]);
        var all = InfParser.ReadFolder(dir);
        Assert.Equal(2, all.Count);
        Assert.Equal(3, all.Single(x => x.InfPath.EndsWith("a.inf")).Drivers.Count);
        Directory.Delete(dir, true);
    }
}

public class MiscTests
{
    [Fact]
    public void RepartiParsingAndSuggest()
    {
        var r = Reparti.Parse(["\"Cardiologia\"", " pediatria ", "", "# commento", "Cardiologia", "Radiologia"]);
        Assert.Equal(["Cardiologia", "pediatria", "Radiologia"], r);
    }

    [Fact]
    public void LegacyConfigIsMigrated()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var legacy = Path.Combine(dir, "duck_config.json");
        File.WriteAllText(legacy, """{ "OUPath":"OU=X,DC=a,DC=b", "UserPrefix":"ST", "DefaultPassword":"Pwd!1", "ADGroups":["G1","g1",""], "TaskMaxParallel": 5000 }""");
        var cfg = AppConfig.Load(Path.Combine(dir, "nuovo.json"), legacy);
        Assert.Equal("ST{INCREMENTAL}", cfg.NamePattern);
        Assert.Equal("Pwd!1", cfg.DefaultPassword);
        Assert.Single(cfg.ADGroups);
        Assert.Equal(1000, cfg.TaskMaxParallel);
        cfg.Save(Path.Combine(dir, "nuovo.json"));
        Assert.DoesNotContain("UserPrefix", File.ReadAllText(Path.Combine(dir, "nuovo.json")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void DriverCacheIsContentAddressed()
    {
        var src = Directory.CreateTempSubdirectory().FullName;
        var cache = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(src, "x.inf"), "a");
        var a = DriverCache.Ensure(src, cache);
        var b = DriverCache.Ensure(src, cache);
        Assert.Equal(a, b);
        Assert.True(File.Exists(Path.Combine(a, "x.inf")));
        Assert.Equal(1, DriverCache.Prune(cache, []));
    }
}

public class PowerShellProtocolTests
{
    [Fact]
    public async Task RunnerRoundTripsInputLogAndResult()
    {
        var exe = PowerShellRunner.ResolveExecutable(null);
        if (!File.Exists(exe) && !exe.Contains(Path.DirectorySeparatorChar)) exe = FindOnPath("pwsh");
        if (exe is null) return; // pwsh non installato: test non applicabile
        var dir = Directory.CreateTempSubdirectory().FullName;
        var runner = new PowerShellRunner(dir, () => exe);
        var logs = new List<string>();
        // Ad.ps1 senza modulo ActiveDirectory deve rispondere con un errore strutturato, non bloccarsi
        var r = await runner.RunAsync("Ad.ps1", new { Action = "login", Cred = new { User = "EXAMPLE\\test", Password = "p'à\"x" }, Server = "", Domain = "x" },
            (l, m) => logs.Add(m), TimeSpan.FromSeconds(60));
        Assert.True(r.HasResult, r.ErrorText);
        Assert.Contains("ActiveDirectory", r.Result.GetProperty("error").GetString());
        Directory.Delete(dir, true);
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
}
