using NINA.Core.Utility;
using System;
using System.Diagnostics;
using System.Runtime;

namespace NINA.Plugin.Livestack.Image {

    internal static class LiveStackMemoryPressure {
        private const long Megabyte = 1024L * 1024L;
        private const long ManagedCollectionThresholdBytes = 768L * Megabyte;
        private const long PrivateCollectionThresholdBytes = 1536L * Megabyte;
        private const long PrivateCompactionThresholdBytes = 3072L * Megabyte;
        private static readonly TimeSpan MinimumCollectionInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan MinimumCompactionInterval = TimeSpan.FromMinutes(2);
        private static readonly object syncRoot = new object();
        private static DateTimeOffset lastCollection = DateTimeOffset.MinValue;
        private static DateTimeOffset lastCompaction = DateTimeOffset.MinValue;

        public static void CollectIfNeeded(string reason) {
            MemorySnapshot snapshot = GetSnapshot();
            if (!HasCollectionPressure(snapshot)) {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (syncRoot) {
                if (now - lastCollection < MinimumCollectionInterval) {
                    return;
                }

                lastCollection = now;
            }

            bool compact = HasCompactionPressure(snapshot, now);
            Logger.Info($"Live Stack memory pressure trim ({reason}): managed={FormatBytes(snapshot.ManagedHeapBytes)}, private={FormatBytes(snapshot.PrivateBytes)}, memoryLoad={FormatBytes(snapshot.MemoryLoadBytes)}, highMemoryThreshold={FormatBytes(snapshot.HighMemoryLoadThresholdBytes)}, compact={compact}");

            if (compact) {
                lock (syncRoot) {
                    lastCompaction = now;
                }

                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                return;
            }

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        }

        public static void CompactAfterReleasingLargeBuffers(string reason) {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (syncRoot) {
                if (now - lastCompaction < MinimumCollectionInterval) {
                    return;
                }

                lastCollection = now;
                lastCompaction = now;
            }

            MemorySnapshot snapshot = GetSnapshot();
            Logger.Info($"Live Stack compacting released buffers ({reason}): managed={FormatBytes(snapshot.ManagedHeapBytes)}, private={FormatBytes(snapshot.PrivateBytes)}");
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        private static bool HasCollectionPressure(MemorySnapshot snapshot) {
            if (snapshot.ManagedHeapBytes >= ManagedCollectionThresholdBytes || snapshot.PrivateBytes >= PrivateCollectionThresholdBytes) {
                return true;
            }

            return snapshot.HighMemoryLoadThresholdBytes > 0
                && snapshot.MemoryLoadBytes >= snapshot.HighMemoryLoadThresholdBytes * 85 / 100;
        }

        private static bool HasCompactionPressure(MemorySnapshot snapshot, DateTimeOffset now) {
            if (now - lastCompaction < MinimumCompactionInterval) {
                return false;
            }

            if (snapshot.PrivateBytes >= PrivateCompactionThresholdBytes) {
                return true;
            }

            return snapshot.HighMemoryLoadThresholdBytes > 0
                && snapshot.MemoryLoadBytes >= snapshot.HighMemoryLoadThresholdBytes * 95 / 100;
        }

        private static MemorySnapshot GetSnapshot() {
            GCMemoryInfo gcMemoryInfo = GC.GetGCMemoryInfo();
            using Process process = Process.GetCurrentProcess();
            return new MemorySnapshot(
                GC.GetTotalMemory(false),
                process.PrivateMemorySize64,
                gcMemoryInfo.MemoryLoadBytes,
                gcMemoryInfo.HighMemoryLoadThresholdBytes);
        }

        private static string FormatBytes(long bytes) {
            if (bytes <= 0) {
                return "n/a";
            }

            return $"{bytes / (double)Megabyte:N0} MB";
        }

        private readonly record struct MemorySnapshot(long ManagedHeapBytes, long PrivateBytes, long MemoryLoadBytes, long HighMemoryLoadThresholdBytes);
    }
}
