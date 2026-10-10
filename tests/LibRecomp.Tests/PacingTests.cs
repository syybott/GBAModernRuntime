using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace LibRecomp.Tests;

public sealed class PacingTests(ITestOutputHelper output)
{
    [Fact]
    public void PublicPacingModesExecuteIdenticalHardwareStepsAndDefaultRemainsPaced()
    {
        string folder = Directory.CreateTempSubdirectory("pacing-comparison-").FullName;
        try
        {
            var reports = new List<JsonDocument>();
            try
            {
                foreach (string mode in new[] { "default", "paced", "unpaced" })
                {
                    string executable = Path.Combine(AppContext.BaseDirectory, "pacing-probe", "PacingProbe" + (OperatingSystem.IsWindows() ? ".exe" : ""));
                    string data = Path.Combine(folder, mode);
                    var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true };
                    info.ArgumentList.Add(mode); info.ArgumentList.Add(data);
                    using var process = Process.Start(info)!;
                    if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); Assert.Fail("Pacing probe timed out."); }
                    Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
                    reports.Add(JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "report.json"))));
                    var report = reports[^1].RootElement;
                    output.WriteLine($"{mode}: {report.GetProperty("frames_per_second")} simulated FPS, {report.GetProperty("steps")} steps, {report.GetProperty("irqs")} IRQs; {report.GetProperty("state_sha256")}");
                    Assert.Equal(120, report.GetProperty("frames").GetInt32());
                    Assert.True(report.GetProperty("irqs").GetInt32() > 120);
                    var samples = report.GetProperty("samples").EnumerateArray().ToArray();
                    Assert.Equal(120, samples.Length);
                    Assert.All(samples, sample => Assert.Equal(0xABCD1234u, sample.GetProperty("dma").GetUInt32()));
                    for (int i = 1; i < samples.Length; i++)
                        Assert.InRange(samples[i].GetProperty("cycles").GetInt64() - samples[i - 1].GetProperty("cycles").GetInt64(), 280864L, 280928L);
                    string[] events = report.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToArray();
                    Assert.Equal(2, events.Length);
                    Assert.StartsWith("first:", events[0]);
                    Assert.Equal("second:" + events[0].Split(':')[1], events[1]);
                }
                Assert.Equal(reports[0].RootElement.GetProperty("state_sha256").GetString(), reports[1].RootElement.GetProperty("state_sha256").GetString());
                Assert.Equal(reports[0].RootElement.GetProperty("state_sha256").GetString(), reports[2].RootElement.GetProperty("state_sha256").GetString());
                Assert.InRange(reports[0].RootElement.GetProperty("frames_per_second").GetDouble(), 1, 65);
                Assert.InRange(reports[1].RootElement.GetProperty("frames_per_second").GetDouble(), 1, 65);
                // No minimum unpaced speed: slow CI hardware is allowed. Equality proves no work was omitted.
            }
            finally { foreach (var report in reports) report.Dispose(); }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
