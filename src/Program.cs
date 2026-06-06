namespace OpenDeck.Loupedeck;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\io.github.brendangrant.opendeck.loupedeck", out var isPrimaryInstance);
        if (!isPrimaryInstance)
        {
            Log.Info("Another OpenDeck Loupedeck plugin instance is already running. Exiting this launch.");
            return 0;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        var launch = PluginLaunchOptions.Parse(args);
        OpenActionConnection? openAction = null;
        if (launch.Port is not null)
        {
            openAction = new OpenActionConnection(
                new Uri($"ws://127.0.0.1:{launch.Port}"),
                launch.PluginUuid,
                launch.RegisterEvent);
        }

        await using var host = new PluginHost(openAction);
        try
        {
            await host.RunAsync(shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex.ToString());
            return 1;
        }
    }
}

internal sealed record PluginLaunchOptions(int? Port, string PluginUuid, string RegisterEvent)
{
    public static PluginLaunchOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith('-') || index + 1 >= args.Length)
                continue;
            values[key.TrimStart('-')] = args[++index];
        }

        var port = values.TryGetValue("port", out var portValue) && int.TryParse(portValue, out var parsedPort)
            ? parsedPort
            : (int?)null;
        var uuid = values.TryGetValue("pluginUUID", out var uuidValue)
            ? uuidValue
            : PluginSettings.PluginUuid;
        var registerEvent = values.TryGetValue("registerEvent", out var registerValue)
            ? registerValue
            : "registerPlugin";

        return new PluginLaunchOptions(port, uuid, registerEvent);
    }
}
