using NINA.Core.Utility;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.Image {

    public class CalibrationManagerSimd : ICalibrationManager {

        internal sealed class CalibrationMaster : IDisposable {

            public CalibrationMaster(CalibrationFrameMeta meta) {
                Meta = meta ?? throw new ArgumentNullException(nameof(meta));

                _width = meta.Width;
                _height = meta.Height;

                // Ideal layout: one contiguous backing store for the entire master frame.
                // This eliminates thousands of per-row arrays and improves locality for row-by-row access.
                _data = new float[checked(_width * _height)];
                _rowLoaded = new bool[_height];

                _imageReader = new CFitsioFITSReader(meta.Path);
            }

            public CalibrationFrameMeta Meta { get; }

            private readonly CFitsioFITSReader _imageReader;
            private readonly int _width;
            private readonly int _height;

            // Contiguous master data backing store.
            private readonly float[] _data;

            // Tracks which rows have been loaded into _data.
            private readonly bool[] _rowLoaded;

            /// <summary>
            /// Returns a read-only view of the requested row.
            /// Loads the row lazily on first access by reading directly into the backing store.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ReadOnlySpan<float> ReadPixelRow(int row) {
                if ((uint)row >= (uint)_height)
                    throw new ArgumentOutOfRangeException(nameof(row));

                int offset = row * _width;

                if (!_rowLoaded[row]) {
                    Span<float> rowSpan = _data.AsSpan(offset, _width);
                    _imageReader.ReadPixelRowAsFloat(row, rowSpan); // no allocation, fills backing store directly
                    _rowLoaded[row] = true;
                }

                return _data.AsSpan(offset, _width);
            }

            /// <summary>
            /// Clears the loaded-row markers (data remains allocated).
            /// Useful for cold-cache benchmarks without reallocating the backing store.
            /// </summary>
            public void ClearLoadedFlags() => Array.Clear(_rowLoaded, 0, _rowLoaded.Length);

            public void Dispose() {
                try {
                    _imageReader.Dispose();
                } catch {
                }
            }
        }

        public IList<CalibrationFrameMeta> FlatLibrary { get; } = new List<CalibrationFrameMeta>();
        public IList<CalibrationFrameMeta> DarkLibrary { get; } = new List<CalibrationFrameMeta>();
        public IList<CalibrationFrameMeta> BiasLibrary { get; } = new List<CalibrationFrameMeta>();
        private Dictionary<CalibrationFrameMeta, CalibrationMaster> masterCache = new Dictionary<CalibrationFrameMeta, CalibrationMaster>();

        public CalibrationManagerSimd() {
        }

        public void RegisterBiasMaster(CalibrationFrameMeta calibrationFrameMeta) {
            if (!BiasLibrary.Any(x => x.Equals(calibrationFrameMeta))) {
                BiasLibrary.Add(calibrationFrameMeta);
            }
        }

        public void RegisterDarkMaster(CalibrationFrameMeta calibrationFrameMeta) {
            if (!DarkLibrary.Any(x => x.Equals(calibrationFrameMeta))) {
                DarkLibrary.Add(calibrationFrameMeta);
            }
        }

        public void RegisterFlatMaster(CalibrationFrameMeta calibrationFrameMeta) {
            if (!FlatLibrary.Any(x => x.Equals(calibrationFrameMeta))) {
                FlatLibrary.Add(calibrationFrameMeta);
            }
        }

        private CalibrationMaster GetBiasMaster(int width, int height, int gain, int offset, string inFilter, bool isBayered) {
            var filter = string.IsNullOrWhiteSpace(inFilter) ? LiveStackBag.NOFILTER : inFilter;
            CalibrationFrameMeta meta = null;
            if (BiasLibrary?.Count > 0) {
                meta = BiasLibrary.FirstOrDefault(x => x.Gain == gain && x.Offset == offset && x.Width == width && x.Height == height);
                if (meta == null) {
                    meta = BiasLibrary.FirstOrDefault(x => x.Gain == gain && x.Offset == -1 && x.Width == width && x.Height == height);
                }
                if (meta == null) {
                    meta = BiasLibrary.FirstOrDefault(x => x.Gain == -1 && x.Offset == offset && x.Width == width && x.Height == height);
                }
                if (meta == null) {
                    meta = BiasLibrary.FirstOrDefault(x => x.Gain == -1 && x.Offset == -1 && x.Width == width && x.Height == height);
                }
            }
            if (meta == null) {
                return null;
            }
            if (masterCache.ContainsKey(meta)) {
                return masterCache[meta];
            }
            var master = new CalibrationMaster(meta);
            masterCache.Add(meta, master);
            return master;
        }

        private CalibrationMaster GetDarkMaster(int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered) {
            var filter = string.IsNullOrWhiteSpace(inFilter) ? LiveStackBag.NOFILTER : inFilter;
            CalibrationFrameMeta meta = null;
            if (DarkLibrary?.Count > 0) {
                meta = DarkLibrary.FirstOrDefault(x => x.Gain == gain && x.Offset == offset && x.ExposureTime == exposureTime && x.Width == width && x.Height == height);
                if (meta == null) {
                    meta = DarkLibrary.FirstOrDefault(x => x.Gain == gain && x.Offset == -1 && x.Width == width && x.Height == height);
                }
                if (meta == null) {
                    meta = DarkLibrary.FirstOrDefault(x => x.Gain == -1 && x.Offset == offset && x.Width == width && x.Height == height);
                }
                if (meta == null) {
                    meta = DarkLibrary.FirstOrDefault(x => x.Gain == -1 && x.Offset == -1 && x.Width == width && x.Height == height);
                }
            }
            if (meta == null) {
                return null;
            }
            if (masterCache.ContainsKey(meta)) {
                return masterCache[meta];
            }
            if (meta == null) {
                return null;
            }
            var master = new CalibrationMaster(meta);
            masterCache.Add(meta, master);
            return master;
        }

        private CalibrationMaster GetFlatMaster(int width, int height, string inFilter, bool isBayered) {
            var filter = string.IsNullOrWhiteSpace(inFilter) ? LiveStackBag.NOFILTER : inFilter;
            CalibrationFrameMeta meta = null;
            if (FlatLibrary?.Count > 0) {
                meta = FlatLibrary.FirstOrDefault(x => x.Filter == filter && x.Width == width && x.Height == height);
            }
            if (meta == null) {
                return null;
            }
            if (masterCache.ContainsKey(meta)) {
                return masterCache[meta];
            }
            if (meta == null) {
                return null;
            }
            var master = new CalibrationMaster(meta);
            masterCache.Add(meta, master);
            return master;
        }

        public float[] ApplyLightFrameCalibrationInPlace(
            CFitsioFITSReader image,
            int width,
            int height,
            double exposureTime,
            int gain,
            int offset,
            string inFilter,
            bool isBayered) {
            CalibrationMaster bias = null;
            if (LivestackMediator.Plugin.UseBiasForLights) {
                bias = GetBiasMaster(width, height, gain, offset, inFilter, isBayered);
            }

            var dark = GetDarkMaster(width, height, exposureTime, gain, offset, inFilter, isBayered);
            var flat = GetFlatMaster(width, height, inFilter, isBayered);

            var sb = new StringBuilder();
            sb.Append($"Calibrating \"{image.FilePath}\";");
            if (bias != null) sb.Append($" using bias \"{bias.Meta.Path}\";");
            if (dark != null) sb.Append($" using dark \"{dark.Meta.Path}\";");
            if (flat != null) sb.Append($" using flat \"{flat.Meta.Path}\";");
            Logger.Info(sb.ToString());

            float[] imageArray = new float[width * height];

            // Hoist invariants
            bool hasBias = bias != null;
            bool hasDark = dark != null;
            bool hasFlat = flat != null;
            float flatMean = hasFlat ? (float)flat.Meta.Mean : 1f;

            float[] lightRowBuffer = ArrayPool<float>.Shared.Rent(width);
            try {
                for (int row = 0; row < height; row++) {
                    Span<float> lightRow = lightRowBuffer.AsSpan(0, width);
                    image.ReadPixelRowAsFloat(row, lightRow);

                    int rowStart = row * width;

                    CalibrateRow(
                        dst: imageArray.AsSpan(rowStart, width),
                        light: lightRow,
                        bias: hasBias ? bias.ReadPixelRow(row) : default,
                        dark: hasDark ? dark.ReadPixelRow(row) : default,
                        flat: hasFlat ? flat.ReadPixelRow(row) : default,
                        hasBias: hasBias,
                        hasDark: hasDark,
                        hasFlat: hasFlat,
                        flatMean: flatMean);
                }
            } finally {
                ArrayPool<float>.Shared.Return(lightRowBuffer);
            }

            return imageArray;
        }

        public float[] ApplyFlatFrameCalibrationInPlace(
            CFitsioFITSReader image,
            int width,
            int height,
            double exposureTime,
            int gain,
            int offset,
            string inFilter,
            bool isBayered) {
            var bias = GetBiasMaster(width, height, gain, offset, inFilter, isBayered);
            CalibrationMaster dark = null;
            if (bias == null) {
                dark = GetDarkMaster(width, height, exposureTime, gain, offset, inFilter, isBayered);
            }

            var sb = new StringBuilder();
            sb.Append($"Calibrating \"{image.FilePath}\";");
            if (bias != null) sb.Append($" using bias \"{bias.Meta.Path}\";");
            if (dark != null) sb.Append($" using dark \"{dark.Meta.Path}\";");
            Logger.Info(sb.ToString());

            float[] imageArray = new float[width * height];

            // Hoist invariants
            bool hasBias = bias != null;
            bool hasDark = dark != null;
            float[] lightRowBuffer = ArrayPool<float>.Shared.Rent(width);
            try {
                for (int row = 0; row < height; row++) {
                    Span<float> lightRow = lightRowBuffer.AsSpan(0, width);
                    image.ReadPixelRowAsFloat(row, lightRow);

                    int rowStart = row * width;

                    CalibrateRow(
                        dst: imageArray.AsSpan(rowStart, width),
                        light: lightRow,
                        bias: hasBias ? bias.ReadPixelRow(row) : default,
                        dark: hasDark ? dark.ReadPixelRow(row) : default,
                        flat: default,
                        hasBias: hasBias,
                        hasDark: hasDark,
                        hasFlat: false,
                        flatMean: 0);
                }
            } finally {
                ArrayPool<float>.Shared.Return(lightRowBuffer);
            }

            return imageArray;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CalibrateRow(
            Span<float> dst,
            ReadOnlySpan<float> light,
            ReadOnlySpan<float> bias,
            ReadOnlySpan<float> dark,
            ReadOnlySpan<float> flat,
            bool hasBias,
            bool hasDark,
            bool hasFlat,
            float flatMean) {
            int n = dst.Length;

            // Fast scalar fallback if SIMD is unavailable or too short to matter
            if (!Vector.IsHardwareAccelerated || n < Vector<float>.Count) {
                for (int i = 0; i < n; i++) {
                    float v = light[i];
                    if (hasBias) v -= bias[i];
                    if (hasDark) v -= dark[i];

                    if (v < 0f) v = 0f;
                    else if (v > 1f) v = 1f;

                    if (hasFlat) {
                        // v / (flat/mean)  == v * (mean/flat)
                        v *= flatMean / flat[i];
                    }

                    dst[i] = v;
                }
                return;
            }

            int simd = Vector<float>.Count;
            int iVec = 0;
            int last = n - (n % simd);

            var vZero = Vector<float>.Zero;
            var vOne = new Vector<float>(1f);
            var vMean = hasFlat ? new Vector<float>(flatMean) : default;

            for (; iVec < last; iVec += simd) {
                var v = new Vector<float>(light.Slice(iVec, simd));

                if (hasBias) v -= new Vector<float>(bias.Slice(iVec, simd));
                if (hasDark) v -= new Vector<float>(dark.Slice(iVec, simd));

                // clamp [0,1]
                v = Vector.Min(vOne, Vector.Max(vZero, v));

                if (hasFlat) {
                    var f = new Vector<float>(flat.Slice(iVec, simd));
                    v *= (vMean / f);
                }

                v.CopyTo(dst.Slice(iVec, simd));
            }

            // Scalar tail
            for (int i = iVec; i < n; i++) {
                float v = light[i];
                if (hasBias) v -= bias[i];
                if (hasDark) v -= dark[i];

                if (v < 0f) v = 0f;
                else if (v > 1f) v = 1f;

                if (hasFlat) v *= flatMean / flat[i];

                dst[i] = v;
            }
        }

        public void Dispose() {
            foreach (var item in masterCache) {
                try {
                    item.Value.Dispose();
                } catch { }
            }
            masterCache.Clear();
            BiasLibrary.Clear();
            DarkLibrary.Clear();
            FlatLibrary.Clear();
        }
    }
}