using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "write-trace")
{
    await OpenDALIoBench.WriteTrace.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(OpenDALIoBench.IoBenchmarks).Assembly).Run(args);
