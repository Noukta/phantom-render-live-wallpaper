using System.Runtime.InteropServices;

namespace Wpe.Core;

/// <summary>
/// Total 3D GPU utilization of all processes except some (our own mpv), from the "GPU Engine" performance
/// counters. Only created when the GPU rule is enabled; one sample costs well under a millisecond.
/// </summary>
public sealed unsafe partial class GpuLoad : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Item { public char* Name; public uint Status; public double Value; }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW")] private static partial uint PdhOpenQuery(nint source, nint user, out nint query);
    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)] private static partial uint PdhAddEnglishCounter(nint query, string path, nint user, out nint counter);
    [LibraryImport("pdh.dll")] private static partial uint PdhCollectQueryData(nint query);
    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")] private static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint count, void* buffer);
    [LibraryImport("pdh.dll")] private static partial uint PdhCloseQuery(nint query);

    const uint PDH_FMT_DOUBLE = 0x200, PDH_MORE_DATA = 0x800007D2;

    nint _query, _counter;
    bool _primed;

    public GpuLoad()
    {
        if (PdhOpenQuery(0, 0, out _query) != 0) { _query = 0; return; }
        if (PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", 0, out _counter) != 0)
        {
            PdhCloseQuery(_query);
            _query = 0;
        }
    }

    public bool Available => _query != 0;

    /// <summary>Percent of 3D engine time used by processes not in <paramref name="excludePids"/>; null until two samples exist.</summary>
    public double? Sample(IReadOnlySet<int> excludePids)
    {
        if (_query == 0 || PdhCollectQueryData(_query) != 0) return null;
        if (!_primed) { _primed = true; return null; }
        uint size = 0;
        uint st = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out uint count, null);
        if (st != PDH_MORE_DATA || size == 0) return 0;
        byte* buf = (byte*)NativeMemory.Alloc(size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out count, buf) != 0) return null;
            var items = (Item*)buf;
            double total = 0;
            for (uint i = 0; i < count; i++)
            {
                if (items[i].Status != 0) continue;
                // Instance names look like "pid_1234_luid_0x..._phys_0_eng_0_engtype_3D".
                if (excludePids.Count > 0 && excludePids.Contains(ParsePid(items[i].Name))) continue;
                total += items[i].Value;
            }
            return Math.Min(100, total);
        }
        finally { NativeMemory.Free(buf); }
    }

    static int ParsePid(char* name)
    {
        if (name == null || name[0] != 'p' || name[1] != 'i' || name[2] != 'd' || name[3] != '_') return -1;
        int pid = 0;
        for (char* c = name + 4; *c >= '0' && *c <= '9'; c++) pid = pid * 10 + (*c - '0');
        return pid;
    }

    public void Dispose()
    {
        if (_query != 0) { PdhCloseQuery(_query); _query = 0; }
    }
}
