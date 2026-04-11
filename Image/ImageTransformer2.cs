using Accord;
using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.Statistics;
using NINA.Image.ImageAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.Image {

    public class ImageTransformer2 : IImageTransformer {
        private static readonly Lazy<ImageTransformer2> lazy = new Lazy<ImageTransformer2>(() => new ImageTransformer2());

        public static ImageTransformer2 Instance => lazy.Value;

        private ImageTransformer2() {
        }

        public List<Accord.Point> GetStars(List<DetectedStar> starList, int width, int height) {
            const int maxStars = 100;

            // Filter stars by brightness
            var filteredStars = starList
                .Where(x => x.MaxBrightness < 65000) // Exclude saturated stars
                .OrderByDescending(x => x.MaxBrightness) // Prioritize brighter stars
                .ToList();

            // Divide the image into a grid and select the brightest stars in each grid cell
            int gridSize = 5;
            double cellWidth = width / (double)gridSize;
            double cellHeight = height / (double)gridSize;
            int maxStarsPerCell = Math.Max(1, maxStars / (gridSize * gridSize));

            var selectedStars = new List<Accord.Point>();

            for (int i = 0; i < gridSize; i++) {
                for (int j = 0; j < gridSize; j++) {
                    // Get the stars in the current grid cell
                    var starsInCell = filteredStars
                        .Where(s => s.Position.X >= i * cellWidth && s.Position.X < (i + 1) * cellWidth
                                 && s.Position.Y >= j * cellHeight && s.Position.Y < (j + 1) * cellHeight)
                        .OrderByDescending(s => s.MaxBrightness) // Pick the brightest stars in the cell
                        .Take(maxStarsPerCell)
                        .ToList();

                    selectedStars.AddRange(starsInCell.Select(s => s.Position));
                }
            }

            // If we still need more stars, fall back to the closest ones to the center
            if (selectedStars.Count < maxStars) {
                var centerX = width / 2.0;
                var centerY = height / 2.0;

                selectedStars.AddRange(
                    filteredStars
                        .Where(s => !selectedStars.Contains(s.Position))
                        .OrderBy(s => GetDistanceToCenter(s.Position, centerX, centerY))
                        .Take(maxStars - selectedStars.Count)
                        .Select(s => s.Position)
                );
            }

            return selectedStars.Take(maxStars).ToList();
        }

        private bool IsWellInsideFrame(System.Drawing.Rectangle bb, int width, int height) {
            // Require the bounding box to be fully inside with a small margin.
            const int margin = 2;
            return bb.Left >= margin && bb.Top >= margin && bb.Right <= (width - margin) && bb.Bottom <= (height - margin);
        }

        private double GetDistanceToCenter(Accord.Point position, double centerX, double centerY) {
            return Math.Sqrt(Math.Pow(position.X - centerX, 2) + Math.Pow(position.Y - centerY, 2));
        }

        public double[,] ComputeAffineTransformation(
                List<Point> stars,
                List<Point> referenceStars) {
            var referenceTriangles = ComputeTriangleList(referenceStars);
            var targetTriangles = ComputeTriangleList(stars);

            var votingMatrix = ComputeVotingMatrix(
                referenceStars.Count,
                stars.Count,
                referenceTriangles,
                targetTriangles,
                maxTriangleDistance: 0.02);

            var matches = ComputeMatchList(ref votingMatrix, voteThreshold: 150);

            var pairs = matches
                .Select(m => (Ref: referenceStars[m.Index1], Src: stars[m.Index2], Votes: m.Votes))
                .ToList();

            if (pairs.Count < 3)
                throw new InvalidOperationException("Not enough matches for affine estimation.");

            // PASS 1: generous threshold to bootstrap
            var (model1, inliers1) = FitAffineRansac(
                pairs,
                iterations: 500,
                inlierThresholdPx: 5.0,
                minInliers: 8);

            // Robustly estimate residual scale from inliers
            var residuals = ComputeResiduals(model1, pairs, inliers1);
            double sigma = RobustSigmaFromResiduals(residuals);

            // PASS 2: tighter, data-driven threshold
            // Clamp keeps it sane across extreme cases.
            double thr = Math.Clamp(3.0 * sigma, 1.0, 6.0);

            var (model2, inliers2) = FitAffineRansac(
                pairs,
                iterations: 500,
                inlierThresholdPx: thr,
                minInliers: 8);

            // Final refinement on inliers
            return RefineAffineLeastSquares(pairs, inliers2);
        }

        private (double[,] Model, List<int> Inliers) FitAffineRansac(
                List<(Point Ref, Point Src, int Votes)> pairs,
                int iterations,
                double inlierThresholdPx,
                int minInliers) {
            if (pairs.Count < 3)
                throw new ArgumentException("Need at least 3 correspondences.");

            var rng = Random.Shared;
            double bestScore = double.NegativeInfinity;
            double[,] bestModel = null;
            List<int> bestInliers = new();

            double thr2 = inlierThresholdPx * inlierThresholdPx;

            for (int it = 0; it < iterations; it++) {
                // sample 3 unique indices
                int i0 = rng.Next(pairs.Count);
                int i1 = rng.Next(pairs.Count);
                int i2 = rng.Next(pairs.Count);
                if (i1 == i0 || i2 == i0 || i2 == i1) { it--; continue; }

                // Optional: reject degenerate samples (nearly collinear in either set)
                if (IsDegenerateTriplet(pairs[i0].Ref, pairs[i1].Ref, pairs[i2].Ref)) { it--; continue; }
                if (IsDegenerateTriplet(pairs[i0].Src, pairs[i1].Src, pairs[i2].Src)) { it--; continue; }

                // Fit affine from exactly 3 pairs
                var model = FitAffineFrom3(pairs[i0], pairs[i1], pairs[i2]);
                if (model == null) continue;

                // Score: count inliers + optionally sum of vote-weights for inliers
                var inliers = new List<int>(capacity: pairs.Count);
                double score = 0;

                for (int i = 0; i < pairs.Count; i++) {
                    var p = pairs[i];
                    var proj = ApplyAffine(p.Ref, model);

                    double dx = proj.X - p.Src.X;
                    double dy = proj.Y - p.Src.Y;
                    double e2 = dx * dx + dy * dy;

                    if (e2 <= thr2) {
                        inliers.Add(i);

                        // simple score: inlier count
                        score += 1.0;

                        // weight by triangle votes (often improves stability)
                        score += 0.1 * p.Votes;
                    }
                }

                if (inliers.Count >= minInliers && score > bestScore) {
                    bestScore = score;
                    bestModel = model;
                    bestInliers = inliers;
                }
            }

            if (bestModel == null)
                throw new InvalidOperationException("RANSAC failed to find a valid affine model.");

            return (bestModel, bestInliers);
        }

        private bool IsDegenerateTriplet(Point a, Point b, Point c) {
            // area ~ 0 => collinear; threshold is in pixel^2 units
            double area2 = Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
            return area2 < 1e-3;
        }

        private double[,] FitAffineFrom3(
                (Point Ref, Point Src, int Votes) p0,
                (Point Ref, Point Src, int Votes) p1,
                (Point Ref, Point Src, int Votes) p2) {
            double[,] source = new double[3, 2];
            double[,] target = new double[3, 2];

            // Here: map Ref -> Src (keep consistent with ApplyAffine usage)
            source[0, 0] = p0.Ref.X; source[0, 1] = p0.Ref.Y;
            source[1, 0] = p1.Ref.X; source[1, 1] = p1.Ref.Y;
            source[2, 0] = p2.Ref.X; source[2, 1] = p2.Ref.Y;

            target[0, 0] = p0.Src.X; target[0, 1] = p0.Src.Y;
            target[1, 0] = p1.Src.X; target[1, 1] = p1.Src.Y;
            target[2, 0] = p2.Src.X; target[2, 1] = p2.Src.Y;

            try {
                return ComputeAffineTransformationMatrix(source, target);
            } catch {
                return null; // singular / unstable sample
            }
        }

        private Point ApplyAffine(Point p, double[,] m) {
            double x = m[0, 0] * p.X + m[0, 1] * p.Y + m[0, 2];
            double y = m[1, 0] * p.X + m[1, 1] * p.Y + m[1, 2];
            return new Point((float)x, (float)y);
        }

        private double[,] RefineAffineLeastSquares(
                List<(Point Ref, Point Src, int Votes)> pairs,
                List<int> inliers) {
            int n = inliers.Count;
            if (n < 3)
                throw new InvalidOperationException("Need at least 3 inliers to refine.");

            double[,] source = new double[n, 2];
            double[,] target = new double[n, 2];

            for (int i = 0; i < n; i++) {
                var p = pairs[inliers[i]];
                source[i, 0] = p.Ref.X;
                source[i, 1] = p.Ref.Y;
                target[i, 0] = p.Src.X;
                target[i, 1] = p.Src.Y;
            }

            return ComputeAffineTransformationMatrix(source, target);
        }

        public bool IsFlippedImage(double[,] affineMatrix) {
            // Extract the top-left 2x2 part of the affine transformation matrix
            double a = affineMatrix[0, 0];
            double b = affineMatrix[0, 1];
            double c = affineMatrix[1, 0];
            double d = affineMatrix[1, 1];

            // Calculate the angle of rotation (in radians)
            double angleRad = Math.Atan2(b, a);  // atan2 handles both sin and cos components
            double angleDeg = angleRad * (180.0 / Math.PI);  // Convert radians to degrees

            // Normalize the angle to be within 0 to 360 degrees
            if (angleDeg < 0)
                angleDeg += 360;

            // Check if the angle is between 160° and 200°
            return angleDeg >= 160 && angleDeg <= 200;
        }

        public float[] ApplyAffineTransformation(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            // Create a new output array to hold the transformed image
            float[] transformedImageData = new float[width * height];

            // Loop through all pixels in the source image
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    // Apply the affine transformation to each pixel's coordinates
                    var transformedCoords = ApplyAffineMatrix(x, y, affineMatrix);

                    // Map transformed coordinates back to the new image
                    int newX = (int)transformedCoords.X;
                    int newY = (int)transformedCoords.Y;
                    if (flippedImage) {
                        newX = width - 1 - newX;
                        newY = height - 1 - newY;
                    }

                    // Ensure we stay within the image bounds
                    if (newX >= 0 && newX < width && newY >= 0 && newY < height) {
                        // Get the interpolated pixel value from the source image data at the transformed coordinates
                        float newPixelValue = GetInterpolatedPixelValue(newX, newY, sourceImageData, width, height);

                        // Set the pixel in the transformed image data
                        transformedImageData[y * width + x] = newPixelValue;
                    }
                }
            }

            return transformedImageData;
        }

        public float[] ApplyAffineTransformation(ushort[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            // Create a new output array to hold the transformed image
            float[] transformedImageData = new float[width * height];

            // Loop through all pixels in the source image
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    // Apply the affine transformation to each pixel's coordinates
                    var transformedCoords = ApplyAffineMatrix(x, y, affineMatrix);

                    // Map transformed coordinates back to the new image
                    int newX = (int)transformedCoords.X;
                    int newY = (int)transformedCoords.Y;
                    if (flippedImage) {
                        newX = width - 1 - newX;
                        newY = height - 1 - newY;
                    }

                    // Ensure we stay within the image bounds
                    if (newX >= 0 && newX < width && newY >= 0 && newY < height) {
                        // Get the interpolated pixel value from the source image data at the transformed coordinates
                        float newPixelValue = GetInterpolatedPixelValue(newX, newY, sourceImageData, width, height);

                        // Set the pixel in the transformed image data
                        transformedImageData[y * width + x] = newPixelValue;
                    }
                }
            }

            return transformedImageData;
        }

        public ushort[] ApplyAffineTransformationAsUshort(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            // Create a new output array to hold the transformed image
            ushort[] transformedImageData = new ushort[width * height];

            // Loop through all pixels in the source image
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    // Apply the affine transformation to each pixel's coordinates
                    var transformedCoords = ApplyAffineMatrix(x, y, affineMatrix);

                    // Map transformed coordinates back to the new image
                    int newX = (int)transformedCoords.X;
                    int newY = (int)transformedCoords.Y;
                    if (flippedImage) {
                        newX = width - 1 - newX;
                        newY = height - 1 - newY;
                    }

                    // Ensure we stay within the image bounds
                    if (newX >= 0 && newX < width && newY >= 0 && newY < height) {
                        // Get the interpolated pixel value from the source image data at the transformed coordinates
                        float newPixelValue = GetInterpolatedPixelValue(newX, newY, sourceImageData, width, height);

                        // Set the pixel in the transformed image data
                        transformedImageData[y * width + x] = (ushort)Math.Clamp(newPixelValue * ushort.MaxValue, 0, ushort.MaxValue);
                    }
                }
            }

            return transformedImageData;
        }

        private double[,] ComputeAffineTransformationMatrix(double[,] sourcePoints, double[,] targetPoints) {
            int numPoints = sourcePoints.GetLength(0);
            if (numPoints < 3) {
                throw new ArgumentException("At least 3 points are required for affine transformation.");
            }

            var A = DenseMatrix.OfArray(new double[numPoints * 2, 6]);
            var B = DenseVector.OfArray(new double[numPoints * 2]);

            for (int i = 0; i < numPoints; i++) {
                A[i * 2, 0] = sourcePoints[i, 0];
                A[i * 2, 1] = sourcePoints[i, 1];
                A[i * 2, 2] = 1;
                A[i * 2, 3] = 0;
                A[i * 2, 4] = 0;
                A[i * 2, 5] = 0;

                A[i * 2 + 1, 0] = 0;
                A[i * 2 + 1, 1] = 0;
                A[i * 2 + 1, 2] = 0;
                A[i * 2 + 1, 3] = sourcePoints[i, 0];
                A[i * 2 + 1, 4] = sourcePoints[i, 1];
                A[i * 2 + 1, 5] = 1;

                B[i * 2] = targetPoints[i, 0];
                B[i * 2 + 1] = targetPoints[i, 1];
            }

            // Solve for X using least squares
            var AtA = A.TransposeThisAndMultiply(A); // Equivalent to At * A
            var AtB = A.TransposeThisAndMultiply(B); // Equivalent to At * B
            var X = AtA.Solve(AtB); // Solve the system AtA * X = AtB

            return new double[3, 3] {
                { X[0], X[1], X[2] },
                { X[3], X[4], X[5] },
                { 0, 0, 1 }
            };
        }

        private Point ApplyAffineMatrix(int x, int y, double[,] matrix) {
            double newX = matrix[0, 0] * x + matrix[0, 1] * y + matrix[0, 2];
            double newY = matrix[1, 0] * x + matrix[1, 1] * y + matrix[1, 2];

            return new Point((float)newX, (float)newY);
        }

        private float GetInterpolatedPixelValue(int x, int y, float[] imageData, int width, int height) {
            if (x < 0 || y < 0 || x >= width || y >= height) {
                return 0; // Out-of-bounds pixels return 0 (or any appropriate default value)
            }

            // Bilinear interpolation
            int x0 = x;
            int y0 = y;
            int x1 = Math.Min(x0 + 1, width - 1);
            int y1 = Math.Min(y0 + 1, height - 1);

            // Get pixel values for interpolation
            float c00 = imageData[y0 * width + x0];
            float c01 = imageData[y0 * width + x1];
            float c10 = imageData[y1 * width + x0];
            float c11 = imageData[y1 * width + x1];

            float xFraction = x - x0;
            float yFraction = y - y0;

            // Calculate the interpolated pixel value
            float interpolatedValue = (float)(
                c00 * (1 - xFraction) * (1 - yFraction) +
                c01 * xFraction * (1 - yFraction) +
                c10 * (1 - xFraction) * yFraction +
                c11 * xFraction * yFraction
            );

            return interpolatedValue;
        }

        private float GetInterpolatedPixelValue(int x, int y, ushort[] imageData, int width, int height) {
            if (x < 0 || y < 0 || x >= width || y >= height) {
                return 0; // Out-of-bounds pixels return 0 (or any appropriate default value)
            }

            // Bilinear interpolation
            int x0 = x;
            int y0 = y;
            int x1 = Math.Min(x0 + 1, width - 1);
            int y1 = Math.Min(y0 + 1, height - 1);

            // Get pixel values for interpolation
            float c00 = imageData[y0 * width + x0] / (float)ushort.MaxValue;
            float c01 = imageData[y0 * width + x1] / (float)ushort.MaxValue;
            float c10 = imageData[y1 * width + x0] / (float)ushort.MaxValue;
            float c11 = imageData[y1 * width + x1] / (float)ushort.MaxValue;

            float xFraction = x - x0;
            float yFraction = y - y0;

            // Calculate the interpolated pixel value
            float interpolatedValue = (float)(
                c00 * (1 - xFraction) * (1 - yFraction) +
                c01 * xFraction * (1 - yFraction) +
                c10 * (1 - xFraction) * yFraction +
                c11 * xFraction * yFraction
            );

            return interpolatedValue;
        }

        private List<Triangle> ComputeTriangleList(List<Point> starList) {
            double[,] distanceMatrix = new double[starList.Count + 1, starList.Count + 1];
            List<Triangle> triangles = new List<Triangle>();

            for (int i = 0; i < starList.Count; i++) {
                for (int j = 0; j < starList.Count; j++) {
                    var star1 = starList[i];
                    var star2 = starList[j];
                    distanceMatrix[i, j] = Math.Sqrt(Math.Pow(star1.X - star2.X, 2.0) + Math.Pow(star1.Y - star2.Y, 2.0));
                }
            }

            const double maxRelativeSideLength = 0.95;
            const double minSumOfSides = 1.3;
            const double minSideDifference = 0.05;
            for (int i = 0; i < starList.Count; i++) {
                for (int j = i + 1; j < starList.Count; j++) {
                    for (int k = j + 1; k < starList.Count; k++) {
                        double side1 = distanceMatrix[i, j];
                        double side2 = distanceMatrix[i, k];
                        double side3 = distanceMatrix[j, k];
                        int index1 = i;
                        int index2 = j;
                        int index3 = k;

                        if (side1 < side2) {
                            (side1, side2) = (side2, side1);
                            (index2, index3) = (index3, index2);
                        }
                        if (side2 < side3) {
                            (side2, side3) = (side3, side2);
                            (index1, index2) = (index2, index1);
                        }
                        if (side1 < side2) {
                            (side1, side2) = (side2, side1);
                            (index2, index3) = (index3, index2);
                        }
                        if (side1 > 0.0) {
                            side2 /= side1;
                            side3 /= side1;
                            if (side2 < maxRelativeSideLength && side3 < maxRelativeSideLength && side2 + side3 > minSumOfSides && Math.Abs(side2 - side3) > minSideDifference) {
                                Triangle triangle = new Triangle(side1, side2, side3, index1, index2, index3);
                                triangles.Add(triangle);
                            }
                        }
                    }
                }
            }
            SortTriangleArray(ref triangles);
            return triangles;
        }

        private int[,] ComputeVotingMatrix(int starCount1, int starCount2, List<Triangle> triangles1, List<Triangle> triangles2, double maxTriangleDistance) {
            double distanceSquared = maxTriangleDistance * maxTriangleDistance;
            int[,] votingMatrix = new int[starCount1, starCount2];

            bool isTriangleSet1Smaller = triangles1.Count <= triangles2.Count;
            var smallerTriangleSet = isTriangleSet1Smaller ? triangles1 : triangles2;
            var largerTriangleSet = isTriangleSet1Smaller ? triangles2 : triangles1;

            Parallel.For(0, smallerTriangleSet.Count, smallerIndex => {
                double smallerSide3 = smallerTriangleSet[smallerIndex].Side3;
                double smallerSide2 = smallerTriangleSet[smallerIndex].Side2;

                // Binary search for range in `larger`
                int rangeStart = largerTriangleSet.BinarySearch(new Triangle { Side3 = smallerSide3 - maxTriangleDistance }, new TriangleComparer());
                int rangeEnd = largerTriangleSet.BinarySearch(new Triangle { Side3 = smallerSide3 + maxTriangleDistance }, new TriangleComparer());
                rangeStart = rangeStart < 0 ? ~rangeStart : rangeStart;
                rangeEnd = rangeEnd < 0 ? ~rangeEnd : rangeEnd;

                double minimumDistanceSquared = double.MaxValue;
                int closestTriangleDistance = -1;

                for (int largerIndex = rangeStart; largerIndex < rangeEnd; largerIndex++) {
                    double distanceSquared = (smallerSide3 - largerTriangleSet[largerIndex].Side3) * (smallerSide3 - largerTriangleSet[largerIndex].Side3) + (smallerSide2 - largerTriangleSet[largerIndex].Side2) * (smallerSide2 - largerTriangleSet[largerIndex].Side2);
                    if (distanceSquared < minimumDistanceSquared) {
                        minimumDistanceSquared = distanceSquared;
                        closestTriangleDistance = largerIndex;
                    }
                }

                if (minimumDistanceSquared < distanceSquared && closestTriangleDistance != -1) {
                    var smallerTriangle = smallerTriangleSet[smallerIndex];
                    var largerTriangle = largerTriangleSet[closestTriangleDistance];

                    lock (votingMatrix) {
                        if (isTriangleSet1Smaller) {
                            votingMatrix[smallerTriangle.Index1, largerTriangle.Index1]++;
                            votingMatrix[smallerTriangle.Index2, largerTriangle.Index2]++;
                            votingMatrix[smallerTriangle.Index3, largerTriangle.Index3]++;
                        } else {
                            votingMatrix[largerTriangle.Index1, smallerTriangle.Index1]++;
                            votingMatrix[largerTriangle.Index2, smallerTriangle.Index2]++;
                            votingMatrix[largerTriangle.Index3, smallerTriangle.Index3]++;
                        }
                    }
                }
            });

            return votingMatrix;
        }

        private List<Match> ComputeMatchList(ref int[,] votingMatrix, double voteThreshold) {
            List<Match> matchList = new List<Match>();

            int matrixSize = Math.Min(votingMatrix.GetLength(0), votingMatrix.GetLength(1));

            double threshold = (matrixSize - 1) * (matrixSize - 2) / voteThreshold;
            threshold = Math.Round(Math.Max(4.0, threshold));

            int bestMatchIndex = 0;
            for (int rowIndex = 0; rowIndex < votingMatrix.GetLength(0); rowIndex++) {
                int maxVotes = 0;
                for (int columnIndex = 0; columnIndex < votingMatrix.GetLength(1); columnIndex++) {
                    if (votingMatrix[rowIndex, columnIndex] > maxVotes) {
                        maxVotes = votingMatrix[rowIndex, columnIndex];
                        bestMatchIndex = columnIndex;
                    }
                }

                if (maxVotes >= threshold) {
                    matchList.Add(new Match { Index1 = rowIndex, Index2 = bestMatchIndex, Votes = maxVotes });
                    for (int columnIndex1 = 0; columnIndex1 < votingMatrix.GetLength(1); columnIndex1++) {
                        votingMatrix[rowIndex, columnIndex1] = 0;
                    }
                    for (int rowIndex1 = 0; rowIndex1 < votingMatrix.GetLength(0); rowIndex1++) {
                        votingMatrix[rowIndex1, bestMatchIndex] = 0;
                    }
                }
            }

            SortMatchArray(ref matchList);

            return matchList;
        }

        private void SortMatchArray(ref List<Match> m) {
            m = m.AsParallel().OrderByDescending(a => a.Votes).ToList();
        }

        private void SortTriangleArray(ref List<Triangle> t) {
            t = t.AsParallel().OrderBy(a => a.Side3).ToList();
        }

        private static double RobustSigmaFromResiduals(double[] residuals) {
            // sigma ≈ 1.4826 * MAD, MAD = median(|r - median(r)|)
            if (residuals == null || residuals.Length == 0) return 0.0;
            double med = residuals.Median();

            var absDev = new double[residuals.Length];
            for (int i = 0; i < residuals.Length; i++)
                absDev[i] = Math.Abs(residuals[i] - med);

            double mad = absDev.Median();
            return 1.4826 * mad;
        }

        private double[] ComputeResiduals(
            double[,] model,
            List<(Point Ref, Point Src, int Votes)> pairs,
            List<int> inlierIdx) {
            var r = new double[inlierIdx.Count];

            for (int k = 0; k < inlierIdx.Count; k++) {
                var p = pairs[inlierIdx[k]];
                var proj = ApplyAffine(p.Ref, model);

                double dx = proj.X - p.Src.X;
                double dy = proj.Y - p.Src.Y;

                r[k] = Math.Sqrt(dx * dx + dy * dy);
            }

            return r;
        }

        private struct Match {
            public int Index1 { get; set; }
            public int Index2 { get; set; }
            public int Votes { get; set; }
        };

        private struct Triangle {

            public Triangle(double side1, double side2, double side3, int index1, int index2, int index3) {
                Side1 = side1;
                Side2 = side2;
                Side3 = side3;
                Index1 = index1;
                Index2 = index2;
                Index3 = index3;
            }

            public double Side1 { get; set; }
            public double Side2 { get; set; }
            public double Side3 { get; set; }
            public int Index1 { get; set; }
            public int Index2 { get; set; }
            public int Index3 { get; set; }
        };

        private class TriangleComparer : IComparer<Triangle> {

            public int Compare(Triangle x, Triangle y) {
                return x.Side3.CompareTo(y.Side3);
            }
        }
    }
}