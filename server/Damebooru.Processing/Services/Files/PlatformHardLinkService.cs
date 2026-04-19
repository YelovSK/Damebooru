using Damebooru.Core.Interfaces;
using System.Runtime.InteropServices;

namespace Damebooru.Processing.Services.Files;

public sealed partial class PlatformHardLinkService : IHardLinkService
{
    public HardLinkResult ReplaceWithHardLink(string existingFilePath, string canonicalFilePath)
    {
        if (string.Equals(existingFilePath, canonicalFilePath, StringComparison.OrdinalIgnoreCase))
        {
            return HardLinkResult.Ok();
        }

        if (!File.Exists(existingFilePath))
        {
            return HardLinkResult.Fail("Target file is missing.");
        }

        if (!File.Exists(canonicalFilePath))
        {
            return HardLinkResult.Fail("Canonical file is missing.");
        }

        var directoryPath = Path.GetDirectoryName(existingFilePath);
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return HardLinkResult.Fail("Target directory is missing.");
        }

        var tempLinkPath = Path.Combine(directoryPath, $".bakabooru-hardlink-{Guid.NewGuid():N}.tmp");
        var backupPath = Path.Combine(directoryPath, $".bakabooru-hardlink-backup-{Guid.NewGuid():N}.tmp");

        try
        {
            var createResult = CreateHardLinkInternal(tempLinkPath, canonicalFilePath);
            if (!createResult.Success)
            {
                return createResult;
            }

            File.Move(existingFilePath, backupPath, overwrite: false);
            File.Move(tempLinkPath, existingFilePath, overwrite: false);
            File.Delete(backupPath);
            return HardLinkResult.Ok();
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tempLinkPath))
                {
                    File.Delete(tempLinkPath);
                }

                if (File.Exists(backupPath) && !File.Exists(existingFilePath))
                {
                    File.Move(backupPath, existingFilePath, overwrite: false);
                }
            }
            catch
            {
                // Best-effort rollback cleanup only.
            }

            return HardLinkResult.Fail(ex.Message);
        }
    }

    private static HardLinkResult CreateHardLinkInternal(string linkPath, string existingPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return WindowsCreateHardLink(linkPath, existingPath)
                ? HardLinkResult.Ok()
                : HardLinkResult.Fail($"CreateHardLink failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return LinuxCreateHardLink(existingPath, linkPath) == 0
                ? HardLinkResult.Ok()
                : HardLinkResult.Fail($"link() failed with errno {Marshal.GetLastWin32Error()}.");
        }

        return HardLinkResult.Fail("Hardlinks are not supported on this platform.");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WindowsCreateHardLink(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes = default);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxCreateHardLink(string existingPath, string newPath);
}
