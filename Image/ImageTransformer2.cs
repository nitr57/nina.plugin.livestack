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
        private const int MaxStars = 72;
        private const int GridSize = 6;
        private const int MaxTriangleCandidates = 3;
        private const int MinimumTriangleSetForParallelVoting = 1024;
        private const double SaturationThreshold = 65000d;
        private const double MaxAllowedEccentricity = 0.85d;
        private const double MaxTriangleScaleDelta = 0.12d;
        private const double MinTriangleLongestSide = 12d;

        private static readonly Lazy<ImageTransformer2> lazy = new Lazy<ImageTransformer2>(() => new ImageTransformer2());

        public static ImageTransformer2 Instance => lazy.Value;

        private ImageTransformer2() {
        }

        public List<Accord.Point> GetStars(List<DetectedStar> starList, int width, int height) {
            if (starList == null || starList.Count == 0) {
                return new List<Point>();
            }

            var candidateStars = BuildScoredStars(starList, width, height, strictFiltering: true);
            if (candidateStars.Count < 8) {
                candidateStars = BuildScoredStars(starList, width, height, strictFiltering: false);
            }

            if (candidateStars.Count == 0) {
                return new List<Point>();
            }

            int targetStarCount = Math.Min(MaxStars, candidateStars.Count);
            int maxStarsPerCell = Math.Max(1, (int)Math.Ceiling(targetStarCount / (double)(GridSize * GridSize)));
            double cellWidth = Math.Max(1d, width / (double)GridSize);
            double cellHeight = Math.Max(1d, height / (double)GridSize);

            var gridBuckets = new List<ScoredStar>[GridSize * GridSize];
            for (int i = 0; i < gridBuckets.Length; i++) {
                gridBuckets[i] = new List<ScoredStar>();
            }

            foreach (var scoredStar in candidateStars) {
                int cellX = Math.Clamp((int)(scoredStar.Position.X / cellWidth), 0, GridSize - 1);
                int cellY = Math.Clamp((int)(scoredStar.Position.Y / cellHeight), 0, GridSize - 1);
                gridBuckets[(cellY * GridSize) + cellX].Add(scoredStar);
            }

            var selectedStars = new List<Point>(targetStarCount);
            var usedIndices = new HashSet<int>();

            foreach (var bucket in gridBuckets) {
                bucket.Sort(static (left, right) => right.Score.CompareTo(left.Score));
                foreach (var scoredStar in bucket.Take(maxStarsPerCell)) {
                    if (selectedStars.Count >= targetStarCount) {
                        break;
                    }

                    if (usedIndices.Add(scoredStar.SourceIndex)) {
                        selectedStars.Add(scoredStar.Position);
                    }
                }
            }

            if (selectedStars.Count < targetStarCount) {
                foreach (var scoredStar in candidateStars) {
                    if (selectedStars.Count >= targetStarCount) {
                        break;
                    }

                    if (usedIndices.Add(scoredStar.SourceIndex)) {
                        selectedStars.Add(scoredStar.Position);
                    }
                }
            }

            return selectedStars;
        }

        private bool IsWellInsideFrame(System.Drawing.Rectangle bb, int width, int height) {
            const int margin = 4;
            if (bb.Width <= 0 || bb.Height <= 0) {
                return false;
            }
            return bb.Left >= margin && bb.Top >= margin && bb.Right <= (width - margin) && bb.Bottom <= (height - margin);
        }

        private double GetDistanceToCenter(Accord.Point position, double centerX, double centerY) {
            return Math.Sqrt(Math.Pow(position.X - centerX, 2) + Math.Pow(position.Y - centerY, 2));
        }

        private List<ScoredStar> BuildScoredStars(List<DetectedStar> starList, int width, int height, bool strictFiltering) {
            double centerX = width / 2d;
            double centerY = height / 2d;

            var validHfrValues = starList
                .Select(x => x.HFR)
                .Where(IsPositiveFinite)
                .ToArray();

            double medianHfr = validHfrValues.Length > 0 ? validHfrValues.Median() : double.NaN;
            double hfrMad = validHfrValues.Length > 0 ? ComputeMedianAbsoluteDeviation(validHfrValues, medianHfr) : 0d;
            double hfrTolerance = double.IsNaN(medianHfr)
                ? double.PositiveInfinity
                : Math.Max(0.75d, hfrMad > 0 ? 3d * hfrMad : medianHfr * 0.6d);

            var scoredStars = new List<ScoredStar>(starList.Count);
            for (int i = 0; i < starList.Count; i++) {
                var star = starList[i];
                if (!IsFinite(star.Position.X) || !IsFinite(star.Position.Y)) {
                    continue;
                }

                bool insideFrame = IsWellInsideFrame(star.BoundingBox, width, height)
                    || (!strictFiltering && IsPointWellInsideFrame(star.Position, width, height));
                if (!insideFrame) {
                    continue;
                }

                if (!IsFinite(star.MaxBrightness) || star.MaxBrightness <= 0 || star.MaxBrightness >= SaturationThreshold) {
                    continue;
                }

                if (strictFiltering && IsElongatedBoundingBox(star.BoundingBox)) {
                    continue;
                }

                if (strictFiltering && IsPositiveFinite(star.HFR) && !double.IsNaN(medianHfr) && Math.Abs(star.HFR - medianHfr) > hfrTolerance) {
                    continue;
                }

                double score = ComputeStarScore(star, medianHfr);
                score *= 1d / (1d + (GetDistanceToCenter(star.Position, centerX, centerY) / Math.Max(width, height)) * 0.1d);
                scoredStars.Add(new ScoredStar(star.Position, score, i));
            }

            scoredStars.Sort(static (left, right) => right.Score.CompareTo(left.Score));
            return scoredStars;
        }

        private bool IsPointWellInsideFrame(Point point, int width, int height) {
            const int margin = 4;
            return point.X >= margin && point.Y >= margin && point.X < (width - margin) && point.Y < (height - margin);
        }

        private static bool IsElongatedBoundingBox(System.Drawing.Rectangle bb) {
            if (bb.Width <= 0 || bb.Height <= 0) {
                return false;
            }

            double aspectRatio = Math.Max(bb.Width, bb.Height) / (double)Math.Max(1, Math.Min(bb.Width, bb.Height));
            return aspectRatio > (1d + MaxAllowedEccentricity);
        }

        private double ComputeStarScore(DetectedStar star, double medianHfr) {
            double contrast = IsFinite(star.Background)
                ? Math.Max(1d, star.MaxBrightness - star.Background)
                : Math.Max(1d, star.MaxBrightness);

            double brightnessScore = Math.Log10(contrast + 10d);
            double shapePenalty = 1d;
            if (star.BoundingBox.Width > 0 && star.BoundingBox.Height > 0) {
                double aspectRatio = Math.Max(star.BoundingBox.Width, star.BoundingBox.Height) / (double)Math.Max(1, Math.Min(star.BoundingBox.Width, star.BoundingBox.Height));
                shapePenalty += Math.Max(0d, aspectRatio - 1d) * 0.75d;
            }

            double hfrPenalty = 1d;
            if (IsPositiveFinite(star.HFR) && !double.IsNaN(medianHfr) && medianHfr > 0d) {
                double ratio = star.HFR / medianHfr;
                if (ratio < 1d) {
                    ratio = 1d / Math.Max(ratio, 1e-3d);
                }

                hfrPenalty += Math.Max(0d, ratio - 1d) * 0.75d;
            }

            return brightnessScore / (shapePenalty * hfrPenalty);
        }

        private static bool IsFinite(double value) {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsPositiveFinite(double value) {
            return IsFinite(value) && value > 0d;
        }

        private static double ComputeMedianAbsoluteDeviation(double[] values, double median) {
            if (values.Length == 0) {
                return 0d;
            }

            var absoluteDeviation = new double[values.Length];
            for (int i = 0; i < values.Length; i++) {
                absoluteDeviation[i] = Math.Abs(values[i] - median);
            }

            return absoluteDeviation.Median();
        }

        public double[,] ComputeAffineTransformation(
                List<Point> stars,
                List<Point> referenceStars) {
            if (stars == null || referenceStars == null || stars.Count < 3 || referenceStars.Count < 3) {
                throw new InvalidOperationException("Not enough stars for affine transformation.");
            }

            var referenceTriangles = ComputeTriangleList(referenceStars);
            var targetTriangles = ComputeTriangleList(stars);

            var votingMatrix = ComputeVotingMatrix(
                referenceStars.Count,
                stars.Count,
                referenceTriangles,
                targetTriangles,
                maxTriangleDistance: 0.02);

            var matches = ComputeMatchList(votingMatrix, referenceStars.Count, stars.Count, voteThreshold: 150);

            var pairs = matches
                .Select(m => (Ref: referenceStars[m.Index1], Src: stars[m.Index2], Votes: m.Votes))
                .ToList();

            return EstimateAffineTransformation(pairs);
        }

        private double[,] EstimateAffineTransformation(List<(Point Ref, Point Src, int Votes)> pairs) {
            if (pairs.Count < 3)
                throw new InvalidOperationException("Not enough matches for affine estimation.");

            if (pairs.Count < 8) {
                // Sparse frames can still produce a valid affine solution even when there
                // are not enough correspondences to satisfy the RANSAC inlier threshold.
                return RefineAffineLeastSquares(pairs, Enumerable.Range(0, pairs.Count).ToList());
            }

            int minInliers = Math.Clamp((int)Math.Ceiling(pairs.Count * 0.4d), 4, 10);

            // PASS 1: generous threshold to bootstrap
            var (model1, inliers1) = FitAffineRansac(
                pairs,
                iterations: 500,
                inlierThresholdPx: 5.0,
                minInliers: minInliers);

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
                minInliers: minInliers);

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

            int targetIterations = iterations;
            const double confidence = 0.995d;

            for (int it = 0; it < targetIterations; it++) {
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
                if (model == null || !IsPlausibleAffineModel(model)) continue;

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

                    double inlierRatio = inliers.Count / (double)pairs.Count;
                    int requiredIterations = EstimateRequiredRansacIterations(inlierRatio, confidence, sampleSize: 3, maxIterations: iterations);
                    if (requiredIterations < targetIterations) {
                        targetIterations = requiredIterations;
                    }
                }
            }

            if (bestModel == null)
                throw new InvalidOperationException("RANSAC failed to find a valid affine model.");

            return (bestModel, bestInliers);
        }

        private bool IsDegenerateTriplet(Point a, Point b, Point c) {
            double abX = b.X - a.X;
            double abY = b.Y - a.Y;
            double acX = c.X - a.X;
            double acY = c.Y - a.Y;
            double bcX = c.X - b.X;
            double bcY = c.Y - b.Y;

            double area2 = Math.Abs((abX * acY) - (abY * acX));
            double maxSideSquared = Math.Max(
                (abX * abX) + (abY * abY),
                Math.Max((acX * acX) + (acY * acY), (bcX * bcX) + (bcY * bcY)));

            if (maxSideSquared <= 1e-6) {
                return true;
            }

            return (area2 / maxSideSquared) < 0.01d;
        }

        private double[,] FitAffineFrom3(
                (Point Ref, Point Src, int Votes) p0,
                (Point Ref, Point Src, int Votes) p1,
                (Point Ref, Point Src, int Votes) p2) {
            double x0 = p0.Ref.X;
            double y0 = p0.Ref.Y;
            double x1 = p1.Ref.X;
            double y1 = p1.Ref.Y;
            double x2 = p2.Ref.X;
            double y2 = p2.Ref.Y;

            double det = (x0 * (y1 - y2)) + (x1 * (y2 - y0)) + (x2 * (y0 - y1));
            if (Math.Abs(det) < 1e-6d) {
                return null;
            }

            double invDet = 1d / det;

            double m00 = (y1 - y2) * invDet;
            double m01 = (y2 - y0) * invDet;
            double m02 = (y0 - y1) * invDet;

            double m10 = (x2 - x1) * invDet;
            double m11 = (x0 - x2) * invDet;
            double m12 = (x1 - x0) * invDet;

            double m20 = ((x1 * y2) - (x2 * y1)) * invDet;
            double m21 = ((x2 * y0) - (x0 * y2)) * invDet;
            double m22 = ((x0 * y1) - (x1 * y0)) * invDet;

            double u0 = p0.Src.X;
            double v0 = p0.Src.Y;
            double u1 = p1.Src.X;
            double v1 = p1.Src.Y;
            double u2 = p2.Src.X;
            double v2 = p2.Src.Y;

            return new double[3, 3] {
                {
                    (m00 * u0) + (m01 * u1) + (m02 * u2),
                    (m10 * u0) + (m11 * u1) + (m12 * u2),
                    (m20 * u0) + (m21 * u1) + (m22 * u2)
                },
                {
                    (m00 * v0) + (m01 * v1) + (m02 * v2),
                    (m10 * v0) + (m11 * v1) + (m12 * v2),
                    (m20 * v0) + (m21 * v1) + (m22 * v2)
                },
                { 0d, 0d, 1d }
            };
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
            float[] transformedImageData = new float[width * height];
            var (a, b, tx, c, d, ty) = GetAffineCoefficients(affineMatrix);

            ProcessAffineRows(width, height, y => {
                int rowOffset = y * width;
                double srcX = (b * y) + tx;
                double srcY = (d * y) + ty;

                for (int x = 0; x < width; x++) {
                    int newX = (int)(float)srcX;
                    int newY = (int)(float)srcY;
                    if (flippedImage) {
                        newX = width - 1 - newX;
                        newY = height - 1 - newY;
                    }

                    if ((uint)newX < (uint)width && (uint)newY < (uint)height) {
                        transformedImageData[rowOffset + x] = sourceImageData[newY * width + newX];
                    }

                    srcX += a;
                    srcY += c;
                }
            });
            return transformedImageData;
        }

        public float[] ApplyAffineTransformation(ushort[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            float[] transformedImageData = new float[width * height];
            var (a, b, tx, c, d, ty) = GetAffineCoefficients(affineMatrix);

            ProcessAffineRows(width, height, y => {
                int rowOffset = y * width;
                double srcX = (b * y) + tx;
                double srcY = (d * y) + ty;

                for (int x = 0; x < width; x++) {
                    int newX = (int)(float)srcX;
                    int newY = (int)(float)srcY;
                    if (flippedImage) {
                        newX = width - 1 - newX;
                        newY = height - 1 - newY;
                    }

                    if ((uint)newX < (uint)width && (uint)newY < (uint)height) {
                        transformedImageData[rowOffset + x] = sourceImageData[newY * width + newX] / (float)ushort.MaxValue;
                    }

                    srcX += a;
                    srcY += c;
                }
            });
            return transformedImageData;
        }

        public ushort[] ApplyAffineTransformationAsUshort(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            ushort[] transformedImageData = new ushort[width * height];
            var (a, b, tx, c, d, ty) = GetAffineCoefficients(affineMatrix);

            ProcessAffineRows(width, height, y => {
                int rowOffset = y * width;
                double srcX = (b * y) + tx;
                double srcY = (d * y) + ty;

                for (int x = 0; x < width; x++) {
                    int newX = (int)(float)srcX;
                    int newY = (int)(float)srcY;
                    if (flippedImage) {
                        newX = width - 1 - newX;
                        newY = height - 1 - newY;
                    }

                    if ((uint)newX < (uint)width && (uint)newY < (uint)height) {
                        float newPixelValue = sourceImageData[newY * width + newX];
                        transformedImageData[rowOffset + x] = (ushort)Math.Clamp(newPixelValue * ushort.MaxValue, 0, ushort.MaxValue);
                    }

                    srcX += a;
                    srcY += c;
                }
            });
            return transformedImageData;
        }

        private static (double A, double B, double Tx, double C, double D, double Ty) GetAffineCoefficients(double[,] affineMatrix) {
            return (
                affineMatrix[0, 0],
                affineMatrix[0, 1],
                affineMatrix[0, 2],
                affineMatrix[1, 0],
                affineMatrix[1, 1],
                affineMatrix[1, 2]);
        }

        private static void ProcessAffineRows(int width, int height, Action<int> processRow) {
            const int minimumPixelsForParallel = 256 * 256;
            if (Environment.ProcessorCount > 1 && height > 1 && (long)width * height >= minimumPixelsForParallel) {
                Parallel.For(0, height, processRow);
            } else {
                for (int y = 0; y < height; y++) {
                    processRow(y);
                }
            }
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
                        if (side1 >= MinTriangleLongestSide) {
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

        private int[] ComputeVotingMatrix(int starCount1, int starCount2, List<Triangle> triangles1, List<Triangle> triangles2, double maxTriangleDistance) {
            double maxTriangleDistanceSquared = maxTriangleDistance * maxTriangleDistance;
            int[] votingMatrix = new int[starCount1 * starCount2];

            bool isTriangleSet1Smaller = triangles1.Count <= triangles2.Count;
            var smallerTriangleSet = isTriangleSet1Smaller ? triangles1 : triangles2;
            var largerTriangleSet = isTriangleSet1Smaller ? triangles2 : triangles1;
            var triangleComparer = new TriangleComparer();

            if (smallerTriangleSet.Count < MinimumTriangleSetForParallelVoting || Environment.ProcessorCount == 1) {
                for (int smallerIndex = 0; smallerIndex < smallerTriangleSet.Count; smallerIndex++) {
                    AccumulateTriangleVotes(smallerTriangleSet[smallerIndex], largerTriangleSet, isTriangleSet1Smaller, starCount2, maxTriangleDistance, maxTriangleDistanceSquared, triangleComparer, votingMatrix);
                }
                return votingMatrix;
            }

            object mergeLock = new object();
            Parallel.For(0, smallerTriangleSet.Count,
                () => new int[votingMatrix.Length],
                (smallerIndex, _, localVotes) => {
                    AccumulateTriangleVotes(smallerTriangleSet[smallerIndex], largerTriangleSet, isTriangleSet1Smaller, starCount2, maxTriangleDistance, maxTriangleDistanceSquared, triangleComparer, localVotes);
                    return localVotes;
                },
                localVotes => {
                    lock (mergeLock) {
                        for (int i = 0; i < votingMatrix.Length; i++) {
                            votingMatrix[i] += localVotes[i];
                        }
                    }
                });

            return votingMatrix;
        }

        private void AccumulateTriangleVotes(Triangle smallerTriangle, List<Triangle> largerTriangleSet, bool isTriangleSet1Smaller, int starCount2, double maxTriangleDistance, double maxTriangleDistanceSquared, TriangleComparer triangleComparer, int[] voteBuffer) {
            double smallerSide3 = smallerTriangle.Side3;
            double smallerSide2 = smallerTriangle.Side2;

            int rangeStart = largerTriangleSet.BinarySearch(new Triangle { Side3 = smallerSide3 - maxTriangleDistance }, triangleComparer);
            int rangeEnd = largerTriangleSet.BinarySearch(new Triangle { Side3 = smallerSide3 + maxTriangleDistance }, triangleComparer);
            rangeStart = rangeStart < 0 ? ~rangeStart : rangeStart;
            rangeEnd = rangeEnd < 0 ? ~rangeEnd : rangeEnd;

            Span<int> bestTriangleIndices = stackalloc int[MaxTriangleCandidates];
            Span<double> bestTriangleDistances = stackalloc double[MaxTriangleCandidates];
            for (int i = 0; i < MaxTriangleCandidates; i++) {
                bestTriangleIndices[i] = -1;
                bestTriangleDistances[i] = double.MaxValue;
            }

            int candidateCount = 0;
            for (int largerIndex = rangeStart; largerIndex < rangeEnd; largerIndex++) {
                var largerTriangle = largerTriangleSet[largerIndex];
                double scaleDelta = Math.Abs(Math.Log((smallerTriangle.Side1 + 1e-6d) / (largerTriangle.Side1 + 1e-6d)));
                if (scaleDelta > MaxTriangleScaleDelta) {
                    continue;
                }

                double distanceSquared =
                    ((smallerSide3 - largerTriangle.Side3) * (smallerSide3 - largerTriangle.Side3)) +
                    ((smallerSide2 - largerTriangle.Side2) * (smallerSide2 - largerTriangle.Side2));

                if (distanceSquared >= maxTriangleDistanceSquared) {
                    continue;
                }

                InsertTriangleCandidate(largerIndex, distanceSquared, bestTriangleIndices, bestTriangleDistances, ref candidateCount);
            }

            for (int candidateRank = 0; candidateRank < candidateCount; candidateRank++) {
                int largerIndex = bestTriangleIndices[candidateRank];
                if (largerIndex < 0) {
                    continue;
                }

                int voteWeight = MaxTriangleCandidates - candidateRank;
                AddTriangleVotes(voteBuffer, starCount2, smallerTriangle, largerTriangleSet[largerIndex], isTriangleSet1Smaller, voteWeight);
            }
        }

        private static void InsertTriangleCandidate(int largerIndex, double distanceSquared, Span<int> bestTriangleIndices, Span<double> bestTriangleDistances, ref int candidateCount) {
            if (candidateCount == MaxTriangleCandidates && distanceSquared >= bestTriangleDistances[MaxTriangleCandidates - 1]) {
                return;
            }

            int insertAt = Math.Min(candidateCount, MaxTriangleCandidates - 1);
            if (candidateCount < MaxTriangleCandidates) {
                candidateCount++;
            }

            while (insertAt > 0 && distanceSquared < bestTriangleDistances[insertAt - 1]) {
                bestTriangleDistances[insertAt] = bestTriangleDistances[insertAt - 1];
                bestTriangleIndices[insertAt] = bestTriangleIndices[insertAt - 1];
                insertAt--;
            }

            bestTriangleDistances[insertAt] = distanceSquared;
            bestTriangleIndices[insertAt] = largerIndex;
        }

        private static void AddTriangleVotes(int[] voteBuffer, int starCount2, Triangle smallerTriangle, Triangle largerTriangle, bool isTriangleSet1Smaller, int voteWeight) {
            if (isTriangleSet1Smaller) {
                voteBuffer[(smallerTriangle.Index1 * starCount2) + largerTriangle.Index1] += voteWeight;
                voteBuffer[(smallerTriangle.Index2 * starCount2) + largerTriangle.Index2] += voteWeight;
                voteBuffer[(smallerTriangle.Index3 * starCount2) + largerTriangle.Index3] += voteWeight;
            } else {
                voteBuffer[(largerTriangle.Index1 * starCount2) + smallerTriangle.Index1] += voteWeight;
                voteBuffer[(largerTriangle.Index2 * starCount2) + smallerTriangle.Index2] += voteWeight;
                voteBuffer[(largerTriangle.Index3 * starCount2) + smallerTriangle.Index3] += voteWeight;
            }
        }

        private List<Match> ComputeMatchList(int[] votingMatrix, int rowCount, int columnCount, double voteThreshold) {
            List<Match> matchList = new List<Match>();

            int matrixSize = Math.Min(rowCount, columnCount);

            double threshold = (matrixSize - 1) * (matrixSize - 2) / voteThreshold;
            int minimumVotes = (int)Math.Round(Math.Max(4.0, threshold));

            int[] rowBestScores = new int[rowCount];
            int[] rowSecondBestScores = new int[rowCount];
            int[] rowBestIndices = Enumerable.Repeat(-1, rowCount).ToArray();

            int[] columnBestScores = new int[columnCount];
            int[] columnSecondBestScores = new int[columnCount];
            int[] columnBestIndices = Enumerable.Repeat(-1, columnCount).ToArray();

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++) {
                int rowOffset = rowIndex * columnCount;
                for (int columnIndex = 0; columnIndex < columnCount; columnIndex++) {
                    int score = votingMatrix[rowOffset + columnIndex];

                    if (score > rowBestScores[rowIndex]) {
                        rowSecondBestScores[rowIndex] = rowBestScores[rowIndex];
                        rowBestScores[rowIndex] = score;
                        rowBestIndices[rowIndex] = columnIndex;
                    } else if (score > rowSecondBestScores[rowIndex]) {
                        rowSecondBestScores[rowIndex] = score;
                    }

                    if (score > columnBestScores[columnIndex]) {
                        columnSecondBestScores[columnIndex] = columnBestScores[columnIndex];
                        columnBestScores[columnIndex] = score;
                        columnBestIndices[columnIndex] = rowIndex;
                    } else if (score > columnSecondBestScores[columnIndex]) {
                        columnSecondBestScores[columnIndex] = score;
                    }
                }
            }

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++) {
                int columnIndex = rowBestIndices[rowIndex];
                if (columnIndex < 0) {
                    continue;
                }

                int bestScore = rowBestScores[rowIndex];
                if (bestScore < minimumVotes) {
                    continue;
                }

                if (columnBestIndices[columnIndex] != rowIndex) {
                    continue;
                }

                if (!IsDistinctiveMatch(bestScore, rowSecondBestScores[rowIndex])) {
                    continue;
                }

                if (!IsDistinctiveMatch(bestScore, columnSecondBestScores[columnIndex])) {
                    continue;
                }

                matchList.Add(new Match { Index1 = rowIndex, Index2 = columnIndex, Votes = bestScore });
            }

            if (matchList.Count < 3) {
                AppendGreedyMatches(matchList, votingMatrix, rowCount, columnCount, Math.Max(4, (int)Math.Floor(minimumVotes * 0.75)));
            }

            SortMatchArray(ref matchList);

            return matchList;
        }

        private void SortMatchArray(ref List<Match> m) {
            m.Sort(static (left, right) => right.Votes.CompareTo(left.Votes));
        }

        private void SortTriangleArray(ref List<Triangle> t) {
            t.Sort(new TriangleComparer());
        }

        private static bool IsDistinctiveMatch(int bestScore, int secondBestScore) {
            if (bestScore <= 0) {
                return false;
            }

            if (secondBestScore <= 0) {
                return true;
            }

            return bestScore >= secondBestScore + 2 || (bestScore * 10) >= (secondBestScore * 13);
        }

        private void AppendGreedyMatches(List<Match> matchList, int[] votingMatrix, int rowCount, int columnCount, int minimumVotes) {
            var usedRows = new bool[rowCount];
            var usedColumns = new bool[columnCount];

            foreach (var match in matchList) {
                usedRows[match.Index1] = true;
                usedColumns[match.Index2] = true;
            }

            var candidates = new List<Match>();
            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++) {
                int rowOffset = rowIndex * columnCount;
                for (int columnIndex = 0; columnIndex < columnCount; columnIndex++) {
                    int score = votingMatrix[rowOffset + columnIndex];
                    if (score >= minimumVotes) {
                        candidates.Add(new Match { Index1 = rowIndex, Index2 = columnIndex, Votes = score });
                    }
                }
            }

            SortMatchArray(ref candidates);

            foreach (var candidate in candidates) {
                if (!usedRows[candidate.Index1] && !usedColumns[candidate.Index2]) {
                    matchList.Add(candidate);
                    usedRows[candidate.Index1] = true;
                    usedColumns[candidate.Index2] = true;
                }
            }
        }

        private bool IsPlausibleAffineModel(double[,] model) {
            double a = model[0, 0];
            double b = model[0, 1];
            double c = model[1, 0];
            double d = model[1, 1];

            double firstColumnScale = Math.Sqrt((a * a) + (c * c));
            double secondColumnScale = Math.Sqrt((b * b) + (d * d));
            double determinant = (a * d) - (b * c);

            return firstColumnScale > 0.75d
                && firstColumnScale < 1.25d
                && secondColumnScale > 0.75d
                && secondColumnScale < 1.25d
                && Math.Abs(determinant) > 0.6d
                && Math.Abs(determinant) < 1.4d;
        }

        private static int EstimateRequiredRansacIterations(double inlierRatio, double confidence, int sampleSize, int maxIterations) {
            double clampedInlierRatio = Math.Clamp(inlierRatio, 1e-3d, 0.999d);
            double successProbability = Math.Pow(clampedInlierRatio, sampleSize);
            double denominator = Math.Log(1d - successProbability);
            if (double.IsNaN(denominator) || double.IsInfinity(denominator) || denominator >= 0d) {
                return maxIterations;
            }

            int requiredIterations = (int)Math.Ceiling(Math.Log(1d - confidence) / denominator);
            return Math.Clamp(requiredIterations, sampleSize, maxIterations);
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

        private readonly struct ScoredStar {
            public ScoredStar(Point position, double score, int sourceIndex) {
                Position = position;
                Score = score;
                SourceIndex = sourceIndex;
            }

            public Point Position { get; }
            public double Score { get; }
            public int SourceIndex { get; }
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
