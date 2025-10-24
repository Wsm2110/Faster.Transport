using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using System;

namespace BenchmarkSuite1
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            BenchmarkRunner.Run<FasterInprocBenchmark>(new DebugInProcessConfig());
            Console.ReadLine();
        }
    }
}