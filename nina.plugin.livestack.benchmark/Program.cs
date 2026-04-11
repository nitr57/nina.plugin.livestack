using Accord.Imaging.Filters;
using BenchmarkDotNet.Running;
using nina.plugin.livestack.benchmark;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using static NINA.Image.FileFormat.XISF.XISFImageProperty.Instrument;

public static class Program {

    public static void Main(string[] args) {
        Console.WriteLine($"Vector.IsHardwareAccelerated = {Vector.IsHardwareAccelerated}");
        Console.WriteLine($"Vector<float>.Count = {Vector<float>.Count}");
        Console.WriteLine();

        BenchmarkRunner.Run<AlignmentBench>();
    }
}