if (args.Length != 0)
{
    if (args.Length != 2 || args[0] != "--soak-seconds"
        || !int.TryParse(args[1], out var seconds) || seconds is < 1 or > 3600)
    {
        Console.Error.WriteLine("Usage: [--soak-seconds 1..3600]");
        return 2;
    }
    try
    {
        await AcpTransportChecks.SoakAsync(seconds).WaitAsync(TimeSpan.FromSeconds(seconds + 20));
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL soak\n{error}");
        return 1;
    }
}

var failed = 0;
var total = 0;
foreach (var (name, run) in AcpTransportChecks.Tests().Concat(AcpTransportChecks.LifetimeTests())
    .Concat(AcpTransportChecks.ShutdownTests()).Concat(AcpTransportChecks.StressTests()))
{
    total++;
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(40));
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}\n{error}");
    }
}
Console.WriteLine($"{total - failed}/{total} checks passed.");
return failed == 0 ? 0 : 1;
