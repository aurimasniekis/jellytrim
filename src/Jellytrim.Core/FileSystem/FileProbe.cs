using System.Runtime.InteropServices;
using Jellytrim.Core.Domain;
using Microsoft.Win32.SafeHandles;

namespace Jellytrim.Core.FileSystem;

/// <summary>What the filesystem says about one path, without following a final symlink.</summary>
public sealed record FileObservation(
    ProbeStatus Status,
    bool IsRegularFile = false,
    bool IsSymlink = false,
    string? Identity = null,
    string? VolumeId = null,
    long Size = 0,
    long AllocatedBytes = 0,
    int LinkCount = 0,
    long ModifiedUnixNanos = 0)
{
    public static FileObservation NotFound { get; } = new(ProbeStatus.NotFound);
}

public interface IFileProbe
{
    FileObservation Probe(string path);
}

/// <summary>
/// Physical identity via statx (Linux), lstat (macOS) and GetFileInformationByHandle (Windows).
/// .NET exposes neither inode, link count nor allocated blocks, and those are what make hard-link
/// accounting and file-replaced detection possible. Any failure returns a non-Ok status and callers
/// must treat that as unverifiable.
/// </summary>
public sealed unsafe partial class NativeFileProbe : IFileProbe
{
    public FileObservation Probe(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return ProbeLinux(path);
            }

            if (OperatingSystem.IsMacOS())
            {
                return ProbeMac(path);
            }

            if (OperatingSystem.IsWindows())
            {
                return ProbeWindows(path);
            }
        }
        catch (EntryPointNotFoundException)
        {
            return new FileObservation(ProbeStatus.Unsupported);
        }
        catch (DllNotFoundException)
        {
            return new FileObservation(ProbeStatus.Unsupported);
        }

        return new FileObservation(ProbeStatus.Unsupported);
    }

    private static ProbeStatus FromErrno(int errno, bool mac) => errno switch
    {
        2 or 20 => ProbeStatus.NotFound, // ENOENT, ENOTDIR
        13 or 1 => ProbeStatus.AccessDenied, // EACCES, EPERM
        38 when !mac => ProbeStatus.Unsupported, // ENOSYS
        78 when mac => ProbeStatus.Unsupported,
        _ => ProbeStatus.IoError,
    };

    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const int SIfMt = 0xF000;
    private const int SIfReg = 0x8000;
    private const int SIfLnk = 0xA000;

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatxNative(int dirfd, string path, int flags, uint mask, byte* buffer);

    private static FileObservation ProbeLinux(string path)
    {
        // struct statx is architecture-independent, so the offsets below hold on x64 and arm64 alike.
        byte* buffer = stackalloc byte[256];
        if (StatxNative(AtFdCwd, path, AtSymlinkNoFollow, StatxBasicStats, buffer) != 0)
        {
            return new FileObservation(FromErrno(Marshal.GetLastPInvokeError(), false));
        }

        var nlink = *(uint*)(buffer + 16);
        var mode = *(ushort*)(buffer + 28);
        var ino = *(ulong*)(buffer + 32);
        var size = *(ulong*)(buffer + 40);
        var blocks = *(ulong*)(buffer + 48);
        var mtimeSec = *(long*)(buffer + 112);
        var mtimeNsec = *(uint*)(buffer + 120);
        var devMajor = *(uint*)(buffer + 136);
        var devMinor = *(uint*)(buffer + 140);
        var volume = $"{devMajor}:{devMinor}";
        return new FileObservation(
            ProbeStatus.Ok,
            (mode & SIfMt) == SIfReg,
            (mode & SIfMt) == SIfLnk,
            $"{volume}:{ino}",
            volume,
            (long)size,
            (long)blocks * 512,
            (int)nlink,
            (mtimeSec * 1_000_000_000L) + mtimeNsec);
    }

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LstatArm64(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LstatX64(string path, byte* buffer);

    private static FileObservation ProbeMac(string path)
    {
        byte* buffer = stackalloc byte[256];
        var rc = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? LstatArm64(path, buffer) : LstatX64(path, buffer);
        if (rc != 0)
        {
            return new FileObservation(FromErrno(Marshal.GetLastPInvokeError(), true));
        }

        var dev = *(int*)buffer;
        var mode = *(ushort*)(buffer + 4);
        var nlink = *(ushort*)(buffer + 6);
        var ino = *(ulong*)(buffer + 8);
        var mtimeSec = *(long*)(buffer + 48);
        var mtimeNsec = *(long*)(buffer + 56);
        var size = *(long*)(buffer + 96);
        var blocks = *(long*)(buffer + 104);
        var volume = dev.ToString();
        return new FileObservation(
            ProbeStatus.Ok,
            (mode & SIfMt) == SIfReg,
            (mode & SIfMt) == SIfLnk,
            $"{volume}:{ino}",
            volume,
            size,
            blocks * 512,
            nlink,
            (mtimeSec * 1_000_000_000L) + mtimeNsec);
    }

    private static FileObservation ProbeWindows(string path)
    {
        const uint FileReadAttributes = 0x80;
        const uint ShareAll = 7;
        const uint OpenExisting = 3;
        const uint BackupSemantics = 0x02000000;
        const uint OpenReparsePoint = 0x00200000;
        using var handle = CreateFileW(path, FileReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            return new FileObservation(error switch
            {
                2 or 3 => ProbeStatus.NotFound,
                5 => ProbeStatus.AccessDenied,
                _ => ProbeStatus.IoError,
            });
        }

        ByHandleInfo info;
        if (!GetFileInformationByHandle(handle, &info))
        {
            return new FileObservation(ProbeStatus.IoError);
        }

        var isDirectory = (info.FileAttributes & 0x10) != 0;
        var isReparse = (info.FileAttributes & 0x400) != 0;
        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        var size = (long)(((ulong)info.FileSizeHigh << 32) | info.FileSizeLow);
        var modified = (long)(((ulong)info.LastWriteHigh << 32) | info.LastWriteLow);
        var volume = info.VolumeSerialNumber.ToString("x8");
        return new FileObservation(
            ProbeStatus.Ok,
            !isDirectory && !isReparse,
            isReparse,
            $"{volume}:{index}",
            volume,
            size,
            size, // Allocation is not on this structure. Compression and sparse are reported as an estimate on Windows.
            (int)info.NumberOfLinks,
            (modified - 116444736000000000L) * 100);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleInfo
    {
        public uint FileAttributes;
        public uint CreationLow;
        public uint CreationHigh;
        public uint LastAccessLow;
        public uint LastAccessHigh;
        public uint LastWriteLow;
        public uint LastWriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, ByHandleInfo* info);
}
