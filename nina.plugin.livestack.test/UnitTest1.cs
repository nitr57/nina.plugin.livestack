using MathNet.Numerics.Statistics;
using Moq;
using NINA.Core.Utility.WindowService;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using NINA.Profile.Interfaces;
using Accord;
using System.Reflection;

namespace nina.plugin.livestack.test {

    public class Tests {
        public string ImagePath { get; set; } = "..\\..\\..\\..\\nina.plugin.livestack.benchmark\\BenchmarkData\\light_b_1.fits";

        public string FlatPath { get; set; } = "..\\..\\..\\..\\nina.plugin.livestack.benchmark\\BenchmarkData\\flat_b.fits";

        public string BiasPath { get; set; } = "..\\..\\..\\..\\nina.plugin.livestack.benchmark\\BenchmarkData\\bias.fits";

        private int frameWidth;
        private int frameHeight;
        private double frameExposureTime;
        private int frameGain;
        private int frameOffset;
        private string frameFilter;
        private bool frameIsBayered;

        private CFitsioFITSReader? reader;
        private CalibrationManager? manager;
        private CalibrationManagerSimd? manager2;

        [SetUp]
        public void Setup() {
            var path = Path.GetFullPath(ImagePath);
            var profileMock = new Mock<IProfileService>();
            string outValue = "";
            profileMock.Setup(m => m.ActiveProfile.PluginSettings.TryGetValue(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    out outValue))
                .Returns(true);
            var dataFactoryMock = new Mock<IImageDataFactory>();
            var windowMock = new Mock<IWindowServiceFactory>();
            var plugin = new Livestack(profileMock.Object, dataFactoryMock.Object, windowMock.Object);
            LivestackMediator.RegisterPlugin(plugin);

            reader = new CFitsioFITSReader(ImagePath);

            // Readout metadata
            using (var fits = new CFitsioFITSReader(ImagePath)) {
                var metaData = fits.ReadHeader().ExtractMetaData();

                frameGain = metaData.Camera.Gain;
                frameOffset = metaData.Camera.Offset;
                frameFilter = metaData.FilterWheel.Filter;
                frameExposureTime = double.IsNaN(metaData.Image.ExposureTime) ? 0 : metaData.Image.ExposureTime;
                frameWidth = fits.Width;
                frameHeight = fits.Height;
            }

            manager = new CalibrationManager();
            manager2 = new CalibrationManagerSimd();

            // Register FLAT Master
            using (var fits = new CFitsioFITSReader(FlatPath)) {
                var metaData = fits.ReadHeader().ExtractMetaData();

                var imageType = metaData.Image.ImageType;
                var gain = metaData.Camera.Gain;
                var offset = metaData.Camera.Offset;
                var filter = metaData.FilterWheel.Filter;
                var exposureTime = double.IsNaN(metaData.Image.ExposureTime) ? 0 : metaData.Image.ExposureTime;
                var width = fits.Width;
                var height = fits.Height;

                var mean = (float)fits.ReadAllPixelsAsFloat().Mean();

                manager.RegisterFlatMaster(new CalibrationFrameMeta(CalibrationFrameType.FLAT, FlatPath, gain, offset, exposureTime, filter, width, height, mean));
                manager2.RegisterFlatMaster(new CalibrationFrameMeta(CalibrationFrameType.FLAT, FlatPath, gain, offset, exposureTime, filter, width, height, mean));
            }

            // Register BIAS Master
            if (!string.IsNullOrEmpty(BiasPath)) {
                using (var fits = new CFitsioFITSReader(BiasPath)) {
                    var metaData = fits.ReadHeader().ExtractMetaData();

                    var imageType = metaData.Image.ImageType;
                    var gain = metaData.Camera.Gain;
                    var offset = metaData.Camera.Offset;
                    var filter = metaData.FilterWheel.Filter;
                    var exposureTime = double.IsNaN(metaData.Image.ExposureTime) ? 0 : metaData.Image.ExposureTime;
                    var width = fits.Width;
                    var height = fits.Height;

                    var mean = (float)fits.ReadAllPixelsAsFloat().Mean();

                    manager.RegisterBiasMaster(new CalibrationFrameMeta(CalibrationFrameType.BIAS, BiasPath, gain, offset, exposureTime, filter, width, height, mean));
                    manager2.RegisterBiasMaster(new CalibrationFrameMeta(CalibrationFrameType.BIAS, BiasPath, gain, offset, exposureTime, filter, width, height, mean));
                }
                plugin.UseBiasForLights = true;
            }
        }

        [TearDown]
        public void Teardown() {
            reader?.Dispose();
            manager?.Dispose();
            manager2?.Dispose();
        }

        [Test]
        public void Test1() {
            var baseline = manager.ApplyLightFrameCalibrationInPlace(reader, frameWidth, frameHeight, frameExposureTime, frameGain, frameOffset, frameFilter, frameIsBayered);

            var simd = manager2.ApplyLightFrameCalibrationInPlace(reader, frameWidth, frameHeight, frameExposureTime, frameGain, frameOffset, frameFilter, frameIsBayered);

            FloatAssert.AreEqual(baseline, simd);
        }

        [Test]
        public void CanRoundTripFitsPathWithBracketsAndCurlyBraces() {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"Livestack[Test]{{{Guid.NewGuid():N}}}");
            Directory.CreateDirectory(tempRoot);

            var filePath = Path.Combine(tempRoot, "frame[1]{test}.fits");
            ushort[] source = [0, 16384, 32768, ushort.MaxValue];

            try {
                var writer = new CFitsioFITSExtendedWriter(filePath, source, 2, 2, NINA.Image.FileFormat.FITS.CfitsioNative.COMPRESSION.NOCOMPRESS);
                writer.Close();

                using var fits = new CFitsioFITSReader(filePath);
                var actual = fits.ReadAllPixelsAsFloat();

                Assert.That(fits.Width, Is.EqualTo(2));
                Assert.That(fits.Height, Is.EqualTo(2));
                FloatAssert.AreEqual([0f, 16384f / ushort.MaxValue, 32768f / ushort.MaxValue, 1f], actual, absTol: 1e-6f, relTol: 1e-6f);
            } finally {
                if (File.Exists(filePath)) {
                    File.Delete(filePath);
                }

                if (Directory.Exists(tempRoot)) {
                    Directory.Delete(tempRoot);
                }
            }
        }

        [Test]
        public void ComputeAffineTransformation_SucceedsWithSparseCorrespondences() {
            var transformer = ImageTransformer2.Instance;
            var estimateMethod = typeof(ImageTransformer2).GetMethod("EstimateAffineTransformation", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(estimateMethod, Is.Not.Null);

            var sparsePairs = new List<(Point Ref, Point Src, int Votes)> {
                (new Point(0, 0), new Point(12, -7), 10),
                (new Point(100, 0), new Point(112, -7), 10),
                (new Point(0, 100), new Point(12, 93), 10),
                (new Point(100, 100), new Point(112, 93), 10),
                (new Point(50, 30), new Point(62, 23), 10)
            };

            var matrix = (double[,])estimateMethod!.Invoke(transformer, new object[] { sparsePairs })!;

            Assert.That(matrix[0, 0], Is.EqualTo(1d).Within(1e-6));
            Assert.That(matrix[1, 1], Is.EqualTo(1d).Within(1e-6));
            Assert.That(matrix[0, 1], Is.EqualTo(0d).Within(1e-6));
            Assert.That(matrix[1, 0], Is.EqualTo(0d).Within(1e-6));
            Assert.That(matrix[0, 2], Is.EqualTo(12d).Within(1e-6));
            Assert.That(matrix[1, 2], Is.EqualTo(-7d).Within(1e-6));
        }

        [Test]
        public void NeedsStarDetection_RedetectsWhenDetectedStarsIsZero() {
            var needsStarDetectionMethod = typeof(LivestackDockable).GetMethod("NeedsStarDetection", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(needsStarDetectionMethod, Is.Not.Null);

            Assert.That((bool)needsStarDetectionMethod!.Invoke(null, new object?[] { null })!, Is.True);
            Assert.That((bool)needsStarDetectionMethod.Invoke(null, new object[] { new StarDetectionAnalysis { DetectedStars = -1 } })!, Is.True);
            Assert.That((bool)needsStarDetectionMethod.Invoke(null, new object[] { new StarDetectionAnalysis { DetectedStars = 0 } })!, Is.True);
            Assert.That((bool)needsStarDetectionMethod.Invoke(null, new object[] { new StarDetectionAnalysis { DetectedStars = 1 } })!, Is.False);
        }

        [Test]
        public void SequentialStack_SimdMatchesScalarReference() {
            float[] originalStack = Enumerable.Range(0, 37).Select(i => ((i * 17) % 101) / 100f).ToArray();
            float[] image = Enumerable.Range(0, 37).Select(i => ((i * 29) % 97) / 96f).ToArray();
            float[] stack = (float[])originalStack.Clone();

            ImageMath.Instance.SequentialStack(image, stack, stackImageCount: 7);

            float[] expected = SequentialStackReference(image, originalStack, stackImageCount: 7);
            FloatAssert.AreEqual(expected, stack);
        }

        [Test]
        public void ApplyAffineTransformation_FloatMatchesReferenceImplementation() {
            var transformer = ImageTransformer2.Instance;
            const int width = 320;
            const int height = 240;

            float[] source = Enumerable.Range(0, width * height)
                .Select(i => ((i * 73) % 1021) / 1020f)
                .ToArray();

            foreach (double[,] matrix in GetAffineTestMatrices()) {
                foreach (bool flipped in new[] { false, true }) {
                    float[] expected = ApplyAffineTransformationReference(source, width, height, matrix, flipped);
                    float[] actual = transformer.ApplyAffineTransformation(source, width, height, matrix, flipped);
                    FloatAssert.AreEqual(expected, actual, absTol: 0f, relTol: 0f);
                }
            }
        }

        [Test]
        public void ApplyAffineTransformation_UShortVariantsMatchReferenceImplementation() {
            var transformer = ImageTransformer2.Instance;
            const int width = 320;
            const int height = 240;

            ushort[] ushortSource = Enumerable.Range(0, width * height)
                .Select(i => (ushort)((i * 131) % ushort.MaxValue))
                .ToArray();
            float[] floatSource = ushortSource.Select(x => x / (float)ushort.MaxValue).ToArray();

            foreach (double[,] matrix in GetAffineTestMatrices()) {
                foreach (bool flipped in new[] { false, true }) {
                    float[] expectedFloat = ApplyAffineTransformationReference(ushortSource, width, height, matrix, flipped);
                    float[] actualFloat = transformer.ApplyAffineTransformation(ushortSource, width, height, matrix, flipped);
                    FloatAssert.AreEqual(expectedFloat, actualFloat, absTol: 0f, relTol: 0f);

                    ushort[] expectedUshort = ApplyAffineTransformationAsUShortReference(floatSource, width, height, matrix, flipped);
                    ushort[] actualUshort = transformer.ApplyAffineTransformationAsUshort(floatSource, width, height, matrix, flipped);
                    Assert.That(actualUshort, Is.EqualTo(expectedUshort));
                }
            }
        }

        private static float[] SequentialStackReference(float[] image, float[] stack, int stackImageCount) {
            float[] result = (float[])stack.Clone();
            float nextCount = stackImageCount + 1f;
            for (int i = 0; i < result.Length; i++) {
                result[i] = (stackImageCount * result[i] + image[i]) / nextCount;
            }
            return result;
        }

        private static IEnumerable<double[,]> GetAffineTestMatrices() {
            yield return new double[3, 3] {
                { 1.0, 0.0, 2.75 },
                { 0.0, 1.0, -3.5 },
                { 0.0, 0.0, 1.0 }
            };
            yield return new double[3, 3] {
                { 0.9875, 0.04375, -1.8 },
                { -0.03125, 1.0125, 4.2 },
                { 0.0, 0.0, 1.0 }
            };
            yield return new double[3, 3] {
                { 1.0009765625, -0.0625, 6.125 },
                { 0.0546875, 0.998046875, -5.875 },
                { 0.0, 0.0, 1.0 }
            };
        }

        private static float[] ApplyAffineTransformationReference(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage) {
            float[] transformedImageData = new float[width * height];

            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    float transformedX = (float)(affineMatrix[0, 0] * x + affineMatrix[0, 1] * y + affineMatrix[0, 2]);
                    float transformedY = (float)(affineMatrix[1, 0] * x + affineMatrix[1, 1] * y + affineMatrix[1, 2]);

                    int newX = (int)transformedX;
                    int newY = (int)transformedY;
                    if (flippedImage) {
                        newX = width - 1 - newX;
                        newY = height - 1 - newY;
                    }

                    if ((uint)newX < (uint)width && (uint)newY < (uint)height) {
                        transformedImageData[y * width + x] = sourceImageData[newY * width + newX];
                    }
                }
            }

            return transformedImageData;
        }

        private static float[] ApplyAffineTransformationReference(ushort[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage) {
            float[] transformedImageData = new float[width * height];

            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    float transformedX = (float)(affineMatrix[0, 0] * x + affineMatrix[0, 1] * y + affineMatrix[0, 2]);
                    float transformedY = (float)(affineMatrix[1, 0] * x + affineMatrix[1, 1] * y + affineMatrix[1, 2]);

                    int newX = (int)transformedX;
                    int newY = (int)transformedY;
                    if (flippedImage) {
                        newX = width - 1 - newX;
                        newY = height - 1 - newY;
                    }

                    if ((uint)newX < (uint)width && (uint)newY < (uint)height) {
                        transformedImageData[y * width + x] = sourceImageData[newY * width + newX] / (float)ushort.MaxValue;
                    }
                }
            }

            return transformedImageData;
        }

        private static ushort[] ApplyAffineTransformationAsUShortReference(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage) {
            ushort[] transformedImageData = new ushort[width * height];

            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    float transformedX = (float)(affineMatrix[0, 0] * x + affineMatrix[0, 1] * y + affineMatrix[0, 2]);
                    float transformedY = (float)(affineMatrix[1, 0] * x + affineMatrix[1, 1] * y + affineMatrix[1, 2]);

                    int newX = (int)transformedX;
                    int newY = (int)transformedY;
                    if (flippedImage) {
                        newX = width - 1 - newX;
                        newY = height - 1 - newY;
                    }

                    if ((uint)newX < (uint)width && (uint)newY < (uint)height) {
                        transformedImageData[y * width + x] = (ushort)Math.Clamp(sourceImageData[newY * width + newX] * ushort.MaxValue, 0, ushort.MaxValue);
                    }
                }
            }

            return transformedImageData;
        }
    }

    public static class FloatAssert {

        public static void AreEqual(
            ReadOnlySpan<float> expected,
            ReadOnlySpan<float> actual,
            float absTol = 1e-6f,
            float relTol = 1e-6f) {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));

            for (int i = 0; i < expected.Length; i++) {
                float e = expected[i];
                float a = actual[i];

                float abs = MathF.Abs(a - e);
                float rel = abs / MathF.Max(MathF.Abs(e), 1e-20f);

                if (abs > absTol && rel > relTol) {
                    Assert.Fail(
                        $"Mismatch at index {i}: expected={e}, actual={a}, abs={abs}, rel={rel}");
                }
            }
        }
    }
}
