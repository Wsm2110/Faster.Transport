using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using System;

namespace BenchmarkSuite1
{
    internal class Program
    {
        public static void Main(string[] args)
        {
           BenchmarkRunner.Run<TcpSocketSendAsyncBenchmark>(new DebugInProcessConfig());
            Console.ReadLine();
        }
    }
}