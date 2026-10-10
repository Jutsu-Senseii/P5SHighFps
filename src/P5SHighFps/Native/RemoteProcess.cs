using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace P5SHighFps.Native;

/// <summary>Reads and writes the memory of another process (the game) through the Windows API.</summary>
sealed class RemoteProcess : IDisposable
{
    readonly nint h;

    public RemoteProcess(Process p)
    {
        // VM_OPERATION | VM_READ | VM_WRITE | QUERY_INFORMATION | SUSPEND_RESUME
        h = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x0400 | 0x0800, false, p.Id);
        if (h == 0) throw new Win32Exception();
    }

    public byte[] Read(long addr, int n)
    {
        var b = new byte[n];
        if (!ReadProcessMemory(h, (nint)addr, b, n, out _)) throw new Win32Exception();
        return b;
    }

    public int ReadInt(long addr) => BitConverter.ToInt32(Read(addr, 4));

    public void Write(long addr, byte[] b)
    {
        if (!WriteProcessMemory(h, (nint)addr, b, b.Length, out _)) throw new Win32Exception();
    }

    /// <summary>Writes to code: temporarily makes the page writable, then flushes the instruction cache.</summary>
    public void WriteCode(long addr, byte[] b)
    {
        if (!VirtualProtectEx(h, (nint)addr, b.Length, 0x40, out uint old)) throw new Win32Exception();
        Write(addr, b);
        VirtualProtectEx(h, (nint)addr, b.Length, old, out _);
        FlushInstructionCache(h, (nint)addr, b.Length);
    }

    public void Protect(long addr, int size, uint prot)
    {
        if (!VirtualProtectEx(h, (nint)addr, size, prot, out _)) throw new Win32Exception();
    }

    public void Suspend() => NtSuspendProcess(h);
    public void Resume() => NtResumeProcess(h);
    public void Dispose() => CloseHandle(h);

    [DllImport("kernel32", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] static extern bool ReadProcessMemory(nint h, nint a, byte[] b, int n, out nint r);
    [DllImport("kernel32", SetLastError = true)] static extern bool WriteProcessMemory(nint h, nint a, byte[] b, int n, out nint w);
    [DllImport("kernel32", SetLastError = true)] static extern bool VirtualProtectEx(nint h, nint a, nint n, uint prot, out uint old);
    [DllImport("kernel32", SetLastError = true)] static extern bool FlushInstructionCache(nint h, nint a, nint n);
    [DllImport("kernel32", SetLastError = true)] static extern bool CloseHandle(nint h);
    [DllImport("ntdll")] static extern int NtSuspendProcess(nint h);
    [DllImport("ntdll")] static extern int NtResumeProcess(nint h);
}
