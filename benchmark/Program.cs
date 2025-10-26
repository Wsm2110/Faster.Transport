using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using System;

namespace BenchmarkSuite1
{
    internal class Program
    {
        public static void Main(string[] args)
        {
           BenchmarkRunner.Run<FasterConcurrentBenchmark>(new DebugInProcessConfig());
            Console.ReadLine();
        }
    }
}