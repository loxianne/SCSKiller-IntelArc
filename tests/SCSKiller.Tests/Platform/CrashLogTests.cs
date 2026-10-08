using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

public class CrashLogTests
{
    /// <summary>A start that fails before the data folder exists still leaves its log. The exception rethrown into the
    /// unhandled handler is logged once, and a full log moves aside, also when several threads write at once.</summary>
    [Fact]
    public void A_crash_is_logged_once_in_a_folder_made_for_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scskiller-crash-" + Guid.NewGuid().ToString("N")[..8], "data");
        try
        {
            var e = new InvalidOperationException("no window");
            CrashLog.Write(dir, e);
            CrashLog.Write(dir, e);
            var log = File.ReadAllText(Path.Combine(dir, CrashLog.FileName));
            Assert.Contains($"on Windows {Environment.OSVersion.Version}: System.InvalidOperationException: no window", log);
            Assert.Equal(2, log.Split("no window").Length);

            File.WriteAllBytes(Path.Combine(dir, CrashLog.FileName), new byte[(1 << 20) + 1]);
            CrashLog.Write(dir, new InvalidOperationException("again"));
            Assert.Equal((1 << 20) + 1, new FileInfo(Path.Combine(dir, "crash.old.log")).Length);
            Assert.Contains("again", File.ReadAllText(Path.Combine(dir, CrashLog.FileName)));

            // handlers on several threads at once: one moves the full log aside, none loses its entry
            File.WriteAllBytes(Path.Combine(dir, CrashLog.FileName), new byte[(1 << 20) + 1]);
            Parallel.For(0, 8, i => CrashLog.Write(dir, new InvalidOperationException($"thread {i}.")));
            log = File.ReadAllText(Path.Combine(dir, CrashLog.FileName));
            Assert.All(Enumerable.Range(0, 8), i => Assert.Contains($"thread {i}.", log));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }
}
