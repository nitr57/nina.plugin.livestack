using Accord;
using NINA.Image.ImageAnalysis;
using System.Collections.Generic;

namespace NINA.Plugin.Livestack.Image {

    public interface IImageTransformer {

        float[] ApplyAffineTransformation(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        void ApplyAffineTransformationInto(float[] sourceImageData, float[] destinationImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        void ApplyAffineTransformationAndStack(float[] sourceImageData, float[] stackImageData, int stackImageCount, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        float[] ApplyAffineTransformation(ushort[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        void ApplyAffineTransformationInto(ushort[] sourceImageData, float[] destinationImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        void ApplyAffineTransformationAndStack(ushort[] sourceImageData, float[] stackImageData, int stackImageCount, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        ushort[] ApplyAffineTransformationAsUshort(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        void ApplyAffineTransformationAsUshortInto(float[] sourceImageData, ushort[] destinationImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        double[,] ComputeAffineTransformation(List<Point> stars, List<Point> referenceStars);

        List<Point> GetStars(List<DetectedStar> starList, int width, int height);

        bool IsFlippedImage(double[,] affineMatrix);
    }
}
