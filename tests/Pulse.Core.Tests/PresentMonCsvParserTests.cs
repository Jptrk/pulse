using Pulse.Core.Frames;

namespace Pulse.Core.Tests;

public class PresentMonCsvParserTests
{
    private static string[] FixtureLines() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "desktop-apps-v2.csv"));

    [Fact]
    public void Parses_real_v2_capture()
    {
        string[] lines = FixtureLines();
        Assert.True(PresentMonCsvParser.LooksLikeHeader(lines[0]));
        var parser = new PresentMonCsvParser(lines[0]);
        Assert.True(parser.HasGpuBusy);
        Assert.True(parser.HasCpuBusy);
        Assert.True(parser.HasDisplayLatency);

        int parsed = 0;
        var apps = new HashSet<string>();
        foreach (string line in lines.Skip(1))
        {
            if (parser.TryParse(line, out var row))
            {
                parsed++;
                apps.Add(row.Application);
                Assert.True(row.ProcessId > 0);
                Assert.True(row.Frame.FrameTimeMs > 0);
            }
        }

        Assert.Equal(lines.Length - 1, parsed);
        Assert.Contains("dwm.exe", apps);
        Assert.Contains("WindowsTerminal.exe", apps);
    }

    [Fact]
    public void First_row_values_match_columns()
    {
        string[] lines = FixtureLines();
        var parser = new PresentMonCsvParser(lines[0]);
        Assert.True(parser.TryParse(lines[1], out var row));

        Assert.Equal("dwm.exe", row.Application);
        Assert.Equal(2176, row.ProcessId);
        Assert.Equal("Hardware: Legacy Flip", row.PresentMode);
        Assert.Equal(1.0388, row.Frame.CpuStartMs, 4);
        Assert.Equal(1.0396f, row.Frame.FrameTimeMs, 4);
        Assert.Equal(0.1905f, row.Frame.GpuBusyMs, 4);
        Assert.Equal(5.5555f, row.Frame.DisplayedTimeMs, 4);
        Assert.True(float.IsNaN(row.Frame.ClickToPhotonMs)); // "NA"
        Assert.True(float.IsNaN(row.Frame.PcLatencyMs));      // column absent
    }

    [Fact]
    public void Column_order_comes_from_header()
    {
        var parser = new PresentMonCsvParser("ProcessID,FrameTime,Application,GPUBusy");
        Assert.True(parser.TryParse("42,16.6,game.exe,15.0", out var row));
        Assert.Equal(42, row.ProcessId);
        Assert.Equal("game.exe", row.Application);
        Assert.Equal(16.6f, row.Frame.FrameTimeMs, 3);
        Assert.Equal(15f, row.Frame.GpuBusyMs, 3);
        Assert.False(parser.HasCpuBusy);
    }

    [Fact]
    public void Accepts_v1_column_names()
    {
        var parser = new PresentMonCsvParser("Application,ProcessID,MsBetweenPresents,MsGPUBusy");
        Assert.True(parser.TryParse("a.exe,1,8.3,7.9", out var row));
        Assert.Equal(8.3f, row.Frame.FrameTimeMs, 3);
        Assert.Equal(7.9f, row.Frame.GpuBusyMs, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("game.exe,notanumber,16.6")]
    [InlineData("game.exe,1")]
    [InlineData("game.exe,1,NA")]
    [InlineData("game.exe,1,0")]
    public void Rejects_bad_rows(string line)
    {
        var parser = new PresentMonCsvParser("Application,ProcessID,FrameTime");
        Assert.False(parser.TryParse(line, out _));
    }

    [Fact]
    public void Rejects_non_presentmon_header()
    {
        Assert.Throws<FormatException>(() => new PresentMonCsvParser("foo,bar,baz"));
    }

    [Fact]
    public void Pools_repeated_strings()
    {
        var parser = new PresentMonCsvParser("Application,ProcessID,FrameTime");
        parser.TryParse("game.exe,1,16", out var a);
        parser.TryParse("game.exe,1,16", out var b);
        Assert.Same(a.Application, b.Application);
    }
}
