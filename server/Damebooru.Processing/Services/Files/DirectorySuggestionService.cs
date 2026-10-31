namespace Damebooru.Processing.Services.Files;

/// <summary>
/// Completes server-side directory paths, so library paths can be picked without knowing the container's volume layout.
/// </summary>
public sealed class DirectorySuggestionService
{
    private const int MaxSuggestions = 50;

    private static readonly HashSet<string> PseudoFileSystems = new(StringComparer.Ordinal)
    {
        "proc", "sysfs", "devpts", "tmpfs", "devtmpfs", "mqueue", "cgroup", "cgroup2", "securityfs",
        "debugfs", "tracefs", "pstore", "bpf", "configfs", "fusectl", "hugetlbfs", "autofs", "binfmt_misc",
        "nsfs", "overlay", "squashfs", "efivarfs", "rpc_pipefs",
    };

    private static readonly string[] SystemMountPrefixes = ["/proc", "/sys", "/dev", "/run", "/boot", "/snap"];

    public IReadOnlyList<string> Suggest(string? input)
    {
        input ??= string.Empty;
        var parent = Path.GetDirectoryName(input);

        // A bare drive ("C:"), a relative name or nothing typed yet: offer starting points instead.
        if (string.IsNullOrEmpty(parent) && !Path.EndsInDirectorySeparator(input))
        {
            return GetStartingPoints()
                .Where(path => path.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var directory = Path.EndsInDirectorySeparator(input) ? input : parent!;
        var prefix = Path.EndsInDirectorySeparator(input) ? string.Empty : Path.GetFileName(input);

        try
        {
            return Directory
                .EnumerateDirectories(directory, "*", new EnumerationOptions { IgnoreInaccessible = true })
                .Select(Path.GetFileName)
                .Where(name => name!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(MaxSuggestions)
                .Select(name => WithTrailingSeparator(Path.Combine(directory, name!)))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static IEnumerable<string> GetStartingPoints()
    {
        if (OperatingSystem.IsWindows())
        {
            return Directory.GetLogicalDrives();
        }

        // In Docker the mounted volumes are what a library can point at; the rest of the container root is noise.
        var mounts = GetMountPoints();
        return mounts.Count > 0 ? mounts.Prepend("/") : ["/"];
    }

    private static List<string> GetMountPoints()
    {
        const string mountsFile = "/proc/mounts";
        if (!File.Exists(mountsFile))
        {
            return [];
        }

        try
        {
            return File.ReadLines(mountsFile)
                .Select(line => line.Split(' '))
                .Where(fields => fields.Length >= 3 && !PseudoFileSystems.Contains(fields[2]))
                .Select(fields => UnescapeMountPath(fields[1]))
                .Where(path => path != "/" && !SystemMountPrefixes.Any(prefix => IsUnder(path, prefix)))
                .Where(Directory.Exists)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(WithTrailingSeparator)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static bool IsUnder(string path, string root)
        => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);

    // /proc/mounts escapes space, tab, newline and backslash as octal (e.g. "\040").
    private static string UnescapeMountPath(string path)
        => path.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

    private static string WithTrailingSeparator(string path)
        => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
