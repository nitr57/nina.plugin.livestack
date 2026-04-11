using MathNet.Numerics.Statistics;
using Moq;
using NINA.Core.Utility.WindowService;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Image;
using NINA.Profile.Interfaces;

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
            reader.Dispose();
            manager.Dispose();
            manager2.Dispose();
        }

        [Test]
        public void Test1() {
            var baseline = manager.ApplyLightFrameCalibrationInPlace(reader, frameWidth, frameHeight, frameExposureTime, frameGain, frameOffset, frameFilter, frameIsBayered);

            var simd = manager2.ApplyLightFrameCalibrationInPlace(reader, frameWidth, frameHeight, frameExposureTime, frameGain, frameOffset, frameFilter, frameIsBayered);

            FloatAssert.AreEqual(baseline, simd);
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