using System.Diagnostics;
using OpenDAL;

namespace OpenDALIoBench;

/// <summary>
/// Paired sync/async/Task.Run write trace used to investigate the async
/// 16 MiB outlier seen in full-suite runs. The three variants alternate
/// within every iteration, so OS write-back drift hits them equally.
/// Run with: dotnet run -c Release -- write-trace
/// </summary>
public static class WriteTrace
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"opendal-write-trace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var op = new Operator("fs", new Dictionary<string, string> { ["root"] = root });
        try
        {
            foreach (var mib in new[] { 1, 2, 4, 8, 16, 32, 64, 128 })
            {
                var payload = new byte[mib * 1024 * 1024];
                Random.Shared.NextBytes(payload);
                var iterations = mib <= 32 ? 12 : 6;

                op.Write("wa-sync.bin", payload);
                await op.WriteAsync("wa-async.bin", payload);
                await Task.Run(() => op.Write("wa-taskrun.bin", payload));

                var sync = new List<double>(iterations);
                var asyncTotal = new List<double>(iterations);
                var asyncSubmit = new List<double>(iterations);
                var taskRun = new List<double>(iterations);

                for (var i = 0; i < iterations; i++)
                {
                    var sw = Stopwatch.StartNew();
                    op.Write("wa-sync.bin", payload);
                    sw.Stop();
                    sync.Add(sw.Elapsed.TotalMilliseconds);

                    sw.Restart();
                    var task = op.WriteAsync("wa-async.bin", payload);
                    var submit = sw.Elapsed.TotalMilliseconds;
                    await task;
                    sw.Stop();
                    asyncSubmit.Add(submit);
                    asyncTotal.Add(sw.Elapsed.TotalMilliseconds);

                    sw.Restart();
                    await Task.Run(() => op.Write("wa-taskrun.bin", payload));
                    sw.Stop();
                    taskRun.Add(sw.Elapsed.TotalMilliseconds);
                }

                Console.WriteLine(
                    $"{mib,4} MiB  sync={Median(sync):F2} ms  async={Median(asyncTotal):F2} ms " +
                    $"(submit={Median(asyncSubmit):F2} ms)  taskrun={Median(taskRun):F2} ms");
            }
        }
        finally
        {
            op.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }
}
