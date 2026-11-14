using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameBoost
{
    internal sealed class SystemUsageSnapshot
    {
        internal double? CpuPercent { get; set; }
        internal ulong? MemoryUsedBytes { get; set; }
        internal ulong? MemoryTotalBytes { get; set; }
        internal double? GpuPercent { get; set; }
    }

    internal sealed class SystemUsageMonitor : IDisposable
    {
        private const uint PdhMoreData = 0x800007D2;
        private const uint PdhNewData = 1;
        private const uint FormatDouble = 0x00000200;
        private const uint FormatNoCap = 0x00008000;
        private const string GpuCounterPath = @"\GPU Engine(*)\Utilization Percentage";
        private const string CpuCounterPath = @"\Processor Information(_Total)\% Processor Time";

        private static readonly int MemoryStatusSize = Marshal.SizeOf(typeof(MemoryStatus));
        private static readonly int CounterItemSize = Marshal.SizeOf(typeof(FormattedCounterItem));

        private readonly object sync = new object();
        private readonly Dictionary<string, double> engineTotals =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private PdhQuery query;
        private IntPtr gpuCounter;
        private IntPtr cpuCounter;
        private bool initialized;
        private bool disposed;
        private bool multipleProcessorGroups;
        private bool hasPdhBaseline;
        private bool hasCpuBaseline;
        private ulong lastIdle;
        private ulong lastKernel;
        private ulong lastUser;

        internal SystemUsageSnapshot Read()
        {
            lock (sync)
            {
                if (disposed)
                    throw new ObjectDisposedException("SystemUsageMonitor");

                if (!initialized)
                    Initialize();

                var sample = new SystemUsageSnapshot();
                if (!multipleProcessorGroups)
                    sample.CpuPercent = ReadCpuTimes();

                var memory = new MemoryStatus { Length = (uint)MemoryStatusSize };
                if (GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical != 0 &&
                    memory.AvailablePhysical <= memory.TotalPhysical)
                {
                    sample.MemoryTotalBytes = memory.TotalPhysical;
                    sample.MemoryUsedBytes = memory.TotalPhysical - memory.AvailablePhysical;
                }

                if (query == null)
                    return sample;
                if (PdhCollectQueryData(query) != 0)
                {
                    hasPdhBaseline = false;
                    return sample;
                }
                if (hasPdhBaseline)
                {
                    if (cpuCounter != IntPtr.Zero)
                        sample.CpuPercent = ReadPdhCpu();
                    if (gpuCounter != IntPtr.Zero)
                        sample.GpuPercent = ReadGpu();
                }

                hasPdhBaseline = true;
                return sample;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                if (query != null)
                {
                    query.Dispose();
                    query = null;
                }
            }
        }

        private void Initialize()
        {
            initialized = true;
            multipleProcessorGroups = GetActiveProcessorGroupCount() > 1;
            PdhQuery opened;
            uint result = PdhOpenQuery(null, UIntPtr.Zero, out opened);
            if (result == 0)
            {
                gpuCounter = AddCounter(opened, GpuCounterPath);
                if (multipleProcessorGroups)
                    cpuCounter = AddCounter(opened, CpuCounterPath);
                if (gpuCounter != IntPtr.Zero || cpuCounter != IntPtr.Zero)
                {
                    query = opened;
                    return;
                }
            }
            if (opened != null)
                opened.Dispose();
        }

        private static IntPtr AddCounter(PdhQuery query, string path)
        {
            IntPtr counter;
            return PdhAddEnglishCounter(query, path, UIntPtr.Zero, out counter) == 0 ? counter : IntPtr.Zero;
        }

        private double? ReadCpuTimes()
        {
            ulong idle;
            ulong kernel;
            ulong user;
            if (!GetSystemTimes(out idle, out kernel, out user))
                return null;

            double? result = null;
            if (hasCpuBaseline && idle >= lastIdle && kernel >= lastKernel && user >= lastUser)
            {
                // GetSystemTimes reports idle time as part of kernel time.
                double total = (double)(kernel - lastKernel) + (user - lastUser);
                if (total > 0)
                    result = ClampPercent(100 * (total - (idle - lastIdle)) / total);
            }

            lastIdle = idle;
            lastKernel = kernel;
            lastUser = user;
            hasCpuBaseline = true;
            return result;
        }

        private double? ReadPdhCpu()
        {
            FormattedCounter value;
            uint result = PdhGetFormattedCounterValue(cpuCounter, FormatDouble, IntPtr.Zero, out value);
            if (result != 0 || !IsValid(value))
                return null;
            return ClampPercent(value.Value);
        }

        private double? ReadGpu()
        {
            // Query the required buffer size again if the set of live GPU instances changes.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                uint size = 0;
                uint count;
                if (PdhGetFormattedCounterArray(gpuCounter, FormatDouble | FormatNoCap, ref size, out count, IntPtr.Zero)
                    != PdhMoreData || size == 0 || size > Int32.MaxValue)
                    return null;

                uint capacity = size;
                IntPtr buffer = Marshal.AllocHGlobal((int)capacity);
                try
                {
                    uint result = PdhGetFormattedCounterArray(gpuCounter, FormatDouble | FormatNoCap, ref size, out count, buffer);
                    if (result == 0 && count <= capacity / (uint)CounterItemSize)
                        return ReadBusiestEngine(buffer, count);
                    if (result != PdhMoreData)
                        return null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return null;
        }

        private double? ReadBusiestEngine(IntPtr buffer, uint count)
        {
            engineTotals.Clear();
            double? busiest = null;
            for (int index = 0; index < count; index++)
            {
                IntPtr entry = IntPtr.Add(buffer, index * CounterItemSize);
                var item = (FormattedCounterItem)Marshal.PtrToStructure(entry, typeof(FormattedCounterItem));
                if (!IsValid(item.Counter))
                    continue;

                string instance = Marshal.PtrToStringUni(item.Name);
                string engine = GetEngineKey(instance);
                if (engine == null)
                    continue;

                double total;
                engineTotals.TryGetValue(engine, out total);
                total += item.Counter.Value;
                engineTotals[engine] = total;
                if (!busiest.HasValue || total > busiest.Value)
                    busiest = total;
            }

            return busiest.HasValue ? (double?)ClampPercent(busiest.Value) : null;
        }

        private static string GetEngineKey(string instance)
        {
            if (instance == null || !instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
                return null;
            int adapter = instance.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
            int type = instance.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            if (adapter < 0 || type <= adapter)
                return null;

            // Drop the process ID and display type. The adapter LUID, physical index,
            // and engine index identify the same engine across all of its processes.
            return instance.Substring(adapter, type - adapter);
        }

        private static bool IsValid(FormattedCounter counter)
        {
            return (counter.Status == 0 || counter.Status == PdhNewData) &&
                counter.Value >= 0 && !Double.IsInfinity(counter.Value);
        }

        private static double ClampPercent(double percent)
        {
            return Math.Min(100, Math.Max(0, percent));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length;
            public uint Load;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FormattedCounter
        {
            public uint Status;
            public double Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FormattedCounterItem
        {
            public IntPtr Name;
            public FormattedCounter Counter;
        }

        private sealed class PdhQuery : SafeHandleZeroOrMinusOneIsInvalid
        {
            public PdhQuery() : base(true) { }

            protected override bool ReleaseHandle()
            {
                return PdhCloseQuery(handle) == 0;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

        [DllImport("kernel32.dll")]
        private static extern ushort GetActiveProcessorGroupCount();

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string dataSource, UIntPtr userData, out PdhQuery query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(PdhQuery query, string path, UIntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(PdhQuery query);

        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out FormattedCounter value);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
