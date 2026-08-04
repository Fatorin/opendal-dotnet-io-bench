using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(OpenDALIoBench.IoBenchmarks).Assembly).Run(args);
