using Damebooru.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
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
        Win32ByHandleFileInformation info;
        if (!PlatformNativeMethods.GetFileInformationByHandle(handle.DangerousGetHandle(), out info))
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

        Span<byte> statBuffer = stackalloc byte[LinuxStatBufferSizeBytes];
        var result = PlatformNativeMethods.stat(filePath, ref MemoryMarshal.GetReference(statBuffer));
        if (result != 0)
        {
            return null;
        }

        var device = BinaryPrimitives.ReadUInt64LittleEndian(statBuffer[..8]);
        var inode = BinaryPrimitives.ReadUInt64LittleEndian(statBuffer.Slice(8, 8));
        return new FileIdentity(device.ToString(), inode.ToString());
    }

    private const int LinuxStatBufferSizeBytes = 256;
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
    internal static partial bool GetFileInformationByHandle(nint hFile, out Win32ByHandleFileInformation lpFileInformation);

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int stat(string path, ref byte statBuffer);
}
