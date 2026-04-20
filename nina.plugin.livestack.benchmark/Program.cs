using BenchmarkDotNet.Running;
using System;
using System.Numerics;

public static class Program {

    public static void Main(string[] args) {
        Console.WriteLine($"Vector.IsHardwareAccelerated = {Vector.IsHardwareAccelerated}");
        Console.WriteLine($"Vector<float>.Count = {Vector<float>.Count}");
        Console.WriteLine();

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
