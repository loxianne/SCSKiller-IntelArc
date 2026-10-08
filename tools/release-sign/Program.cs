using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;

// The offline half of the signed update feed. Same code as the client's check (FeedTrust).
//   keygen <key-file>                                                         new key; prints the public key to pin
//   sign <releases.<c>.json> --key <key-file> --kid rel-a [--channel <c>]     writes <feed>.sig
//   verify <releases.<c>.json> [--pub <base64>] [--kid rel-a] [--channel <c>] checks <feed>.sig (no --pub: the pinned keys)
// The key file must be outside every git work tree.

try
{
    return args switch
    {
        ["keygen", var file] => Keygen(file),
        ["sign", var feed, .. var o] when Known(o, "--key", "--kid", "--channel") =>
            Sign(feed, Opt(o, "--key") ?? throw Usage(), Opt(o, "--kid") ?? throw Usage(), Opt(o, "--channel")),
        ["verify", var feed, .. var o] when Known(o, "--pub", "--kid", "--channel") =>
            Verify(feed, Opt(o, "--pub"), Opt(o, "--kid") ?? "rel-a", Opt(o, "--channel")),
        _ => throw Usage(),
    };
}
catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or FormatException or FeedRejectedException or Win32Exception)
{
    Console.Error.WriteLine($"release-sign: {e.Message}");
    return 1;
}

static ArgumentException Usage() => new("usage: keygen <key-file> | sign <releases.<channel>.json> --key <key-file> --kid <kid> [--channel <c>]"
    + " | verify <releases.<channel>.json> [--pub <base64>] [--kid <kid>] [--channel <c>]");

static bool Known(string[] o, params string[] names) => o.Length % 2 == 0 && o.Where((_, i) => i % 2 == 0).All(names.Contains);

static string? Opt(string[] o, string name) => Array.IndexOf(o, name) is var i and >= 0 ? o[i + 1] : null;

static int Keygen(string file)
{
    var (seed, pub) = FeedTrust.NewKey();
    using (var f = new FileStream(OutsideGit(file), FileMode.CreateNew, FileAccess.Write)) f.Write(Encoding.ASCII.GetBytes(seed + "\n"));   // never overwrites
    Console.WriteLine($"wrote {Path.GetFullPath(file)}: the private key, keep it offline");
    Console.WriteLine($"public key: {pub}");
    Console.WriteLine("pin it in FeedTrust.ReleaseKeys (src/SCSKiller.Core/App/Updates.cs) as rel-a (primary) or rel-b (backup)");
    return 0;
}

static int Sign(string feed, string keyFile, string kid, string? channel)
{
    channel = ChannelOf(feed, channel);
    var seed = Convert.FromBase64String(File.ReadAllText(OutsideGit(keyFile)).Trim());
    try
    {
        if (seed.Length != 32) throw new FormatException($"{keyFile}: not a 32-byte Ed25519 seed");
        File.WriteAllText(feed + ".sig", FeedTrust.Sign(seed, kid, channel, File.ReadAllBytes(feed), DateTimeOffset.UtcNow));
    }
    finally { CryptographicOperations.ZeroMemory(seed); }
    Console.WriteLine($"wrote {feed}.sig ({channel}, {kid})");
    return 0;
}

static int Verify(string feed, string? pub, string kid, string? channel)
{
    channel = ChannelOf(feed, channel);
    var keys = pub != null ? new Dictionary<string, string> { [kid] = pub } : FeedTrust.ReleaseKeys;
    var tmp = Directory.CreateTempSubdirectory("release-sign-");   // a fresh replay store: nothing seen yet
    try { new FeedTrust(new AppStore(tmp.FullName), keys).Accept(channel, File.ReadAllBytes(feed), File.ReadAllBytes(feed + ".sig")); }
    finally { tmp.Delete(true); }
    Console.WriteLine($"{feed}.sig verifies ({channel}, {(pub != null ? kid : "pinned keys")})");
    return 0;
}

static string ChannelOf(string feed, string? channel)
{
    channel ??= Regex.Match(Path.GetFileName(feed), @"^releases\.([a-z]+)\.json$").Groups[1].Value;
    return UpdateChannels.All.Contains(channel) ? channel
        : throw new ArgumentException($"no channel: name the feed releases.<channel>.json or pass --channel {string.Join('|', UpdateChannels.All)}");
}

// Fails closed: only git's own "not a git repository" counts as outside.
static string OutsideGit(string path)
{
    var full = Path.GetFullPath(path);
    var dir = Path.GetDirectoryName(full)!;
    if (!Directory.Exists(dir)) throw new ArgumentException($"no directory {dir}");
    using var git = Process.Start(new ProcessStartInfo("git", ["-C", dir, "rev-parse", "--show-toplevel"])
        { RedirectStandardOutput = true, RedirectStandardError = true })!;
    var err = git.StandardError.ReadToEndAsync();
    var top = git.StandardOutput.ReadToEnd().Trim();
    git.WaitForExit();
    if (git.ExitCode == 0) throw new ArgumentException($"{full} is inside the git work tree {top}: keep the release key outside any repo");
    if (!err.Result.Contains("not a git repository")) throw new ArgumentException($"can't tell whether {dir} is in a git work tree: {err.Result.Trim()}");
    return full;
}
