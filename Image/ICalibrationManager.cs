using System;

namespace NINA.Plugin.Livestack.Image {

    public interface ICalibrationManager : IDisposable {

        float[] ApplyFlatFrameCalibrationInPlace(CFitsioFITSReader image, int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered);

        float[] ApplyLightFrameCalibrationInPlace(CFitsioFITSReader image, int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered);

        void Dispose();

        void RegisterBiasMaster(CalibrationFrameMeta calibrationFrameMeta);

        void RegisterDarkMaster(CalibrationFrameMeta calibrationFrameMeta);

        void RegisterFlatMaster(CalibrationFrameMeta calibrationFrameMeta);
    }
}