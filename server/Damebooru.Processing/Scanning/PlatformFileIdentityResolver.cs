using Damebooru.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Damebooru.Processing.Scanning;

public class PlatformFileIdentityResolver : IFileIdentityResolver
{
    private readonly ILogger<PlatformFileIdentityResolver> _logger;

    public PlatformFileIdentityResolver(ILogger<PlatformFileIdentityResolver> logger)
    {
        _logger = logger;
    }

    public FileIdentity? TryResolve(string filePath)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return TryGetWindowsIdentity(filePath);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return TryGetLinuxIdentity(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to resolve file identity for {Path}", filePath);
        }

        return null;
    }

    private static FileIdentity? TryGetWindowsIdentity(string filePath)
    {
        using var handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (PlatformNativeMethods.GetFileInformationByHandleEx(
                handle,
                Win32FileInfoByHandleClass.FileIdInfo,
                out var fileIdInfo,
                (uint)Marshal.SizeOf<Win32FileIdInfo>()))
        {
            return new FileIdentity(
                fileIdInfo.VolumeSerialNumber.ToString("X16"),
                $"{fileIdInfo.FileId.Part1:X16}{fileIdInfo.FileId.Part0:X16}");
        }

        if (!PlatformNativeMethods.GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return new FileIdentity(
            info.VolumeSerialNumber.ToString("X8"),
            fileIndex.ToString("X16"));
    }

    private static FileIdentity? TryGetLinuxIdentity(string filePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return null;
        }

        using var handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var fd = handle.DangerousGetHandle().ToInt32();

        if (PlatformNativeMethods.fstat(fd, out var stat) != 0)
        {
            if (PlatformNativeMethods.stat(filePath, out stat) != 0)
            {
                return null;
            }
        }

        if (stat.StDev == 0 || stat.StIno == 0)
        {
            return null;
        }

        return new FileIdentity(
            stat.StDev.ToString(),
            stat.StIno.ToString());
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32FileTime
{
    public uint DwLowDateTime;
    public uint DwHighDateTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32ByHandleFileInformation
{
    public uint FileAttributes;
    public Win32FileTime CreationTime;
    public Win32FileTime LastAccessTime;
    public Win32FileTime LastWriteTime;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}

internal static partial class PlatformNativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        Win32FileInfoByHandleClass fileInformationClass,
        out Win32FileIdInfo lpFileInformation,
        uint dwBufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(SafeFileHandle hFile, out Win32ByHandleFileInformation lpFileInformation);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    internal static partial int fstat(int fd, out LinuxStat statBuffer);

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int stat(string path, out LinuxStat statBuffer);
}

internal enum Win32FileInfoByHandleClass
{
    FileIdInfo = 18,
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32FileId128
{
    public ulong Part0;
    public ulong Part1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32FileIdInfo
{
    public ulong VolumeSerialNumber;
    public Win32FileId128 FileId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LinuxTimespec
{
    public long TvSec;
    public long TvNsec;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LinuxStat
{
    public ulong StDev;
    public ulong StIno;
    public ulong StNlink;
    public uint StMode;
    public uint StUid;
    public uint StGid;
    public uint Padding0;
    public ulong StRdev;
    public long StSize;
    public long StBlksize;
    public long StBlocks;
    public LinuxTimespec StAtim;
    public LinuxTimespec StMtim;
    public LinuxTimespec StCtim;
    public long Reserved0;
    public long Reserved1;
    public long Reserved2;
}
