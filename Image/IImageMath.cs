using System.Collections.Generic;
using System.Drawing;

namespace NINA.Plugin.Livestack.Image {
    public interface IImageMath {
        static abstract ImageMath Instance { get; }

        void ApplyGreenDeNoiseInPlace(Bitmap colorBitmap, double amount);
        (double Median, double MAD) CalculateMedianAndMAD(int[] pixelValueCounts, int originalDataLength);
        ImageMath.BitmapWithMedian CreateGrayBitmap(float[] data, int width, int height);
        Bitmap DownsampleGray16(Bitmap input, int factor);
        List<Accord.Point> Flip(List<Accord.Point> points, int width, int height);
        Bitmap MergeGray16ToRGB48(Bitmap red, Bitmap green, Bitmap blue);
        float[] PercentileClipping(List<CFitsioFITSReader> images, double lowerPercentile, double upperPercentile);
        void RemoveHotPixelOutliers(float[] imageData, int width, int height, int neighborSize = 1, double outlierFactor = 10);
        void SequentialStack(float[] image, float[] stack, int stackImageCount);
    }
}
