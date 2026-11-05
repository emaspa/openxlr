using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>Numerical memory evidence from this process, without copying its pages or forcing a GC.</summary>
internal sealed record RuntimeSnapshot(DateTimeOffset CollectedAt, string Runtime, long WorkingSetBytes,
    Dictionary<string, long> MemoryKiB, ManagedMemorySnapshot Managed, int ThreadPoolThreads, long PendingWorkItems)
{
    internal static readonly string[] StatusMemoryFields =
        ["VmRSS", "VmHWM", "VmSize", "VmSwap", "VmLck", "RssAnon", "RssFile", "RssShmem"];
    private static readonly string[] RollupMemoryFields =
        ["Pss", "Pss_Anon", "Pss_File", "Pss_Shmem", "Private_Clean", "Private_Dirty", "SwapPss"];

    internal static RuntimeSnapshot Capture()
    {
        DateTimeOffset collectedAt = DateTimeOffset.UtcNow;
        Dictionary<string, long> memory = [];
        if (OperatingSystem.IsLinux())
        {
            memory = ReadKiB(ReadProcFields("/proc/self/status", StatusMemoryFields), StatusMemoryFields);
            foreach (var field in ReadKiB(ReadProcFields("/proc/self/smaps_rollup", RollupMemoryFields), RollupMemoryFields))
                memory.Add(field.Key, field.Value);
        }

        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        var managed = new ManagedMemorySnapshot(GC.GetTotalMemory(forceFullCollection: false),
            GC.GetTotalAllocatedBytes(precise: false), gc.Index, gc.HeapSizeBytes, gc.FragmentedBytes,
            gc.TotalCommittedBytes, gc.TotalAvailableMemoryBytes, GC.CollectionCount(0), GC.CollectionCount(1),
            GC.CollectionCount(2));
        return new(collectedAt, RuntimeInformation.FrameworkDescription, Environment.WorkingSet, memory,
            managed, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount);
    }

    internal static Dictionary<string, long> ReadKiB(Dictionary<string, string> fields, string[] keys)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string key in keys)
            if (fields.TryGetValue(key, out string? value) && value.EndsWith(" kB", StringComparison.Ordinal)
                && long.TryParse(value.AsSpan(0, value.Length - 3).Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out long size))
                values.Add(key, size);
        return values;
    }

    internal static Dictionary<string, string> ReadProcFields(string path, ReadOnlySpan<string> keys)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        Span<char> text = stackalloc char[16 * 1024];
        int length;
        try
        {
            RegularFile.Require(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize: 1);
            using var reader = new StreamReader(stream);
            length = reader.ReadBlock(text);
            if (length == text.Length && reader.Read() != -1) return fields;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return fields; }
        foreach (ReadOnlySpan<char> line in text[..length].EnumerateLines())
        {
            int separator = line.IndexOf(':');
            if (separator <= 0) continue;
            foreach (string key in keys)
                if (line[..separator].SequenceEqual(key))
                {
                    fields.TryAdd(key, line[(separator + 1)..].Trim().ToString());
                    break;
                }
        }
        return fields;
    }
}

internal sealed record ManagedMemorySnapshot(long LiveBytes, long TotalAllocatedBytes, long CollectionIndex,
    long LastHeapSizeBytes, long LastFragmentedBytes, long LastCommittedBytes, long LastAvailableMemoryBytes,
    int Gen0Collections, int Gen1Collections, int Gen2Collections);
