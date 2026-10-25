using System.Diagnostics;
using ServerComms.Bot;

// ServerComms.Bot: the Discord side of ServerComms. Runs as a console app (double-click, logs to the
// window) or as a Windows service (install / uninstall from an admin prompt). Settings come from
// appsettings.json next to the exe; see appsettings.example.json.

const string ServiceName = "ServerComms.Bot";

if (args.Length > 0 && args[0].ToLowerInvariant() is "install" or "uninstall")
    return ServiceCommand(args[0].ToLowerInvariant());

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,   // a service starts in System32; config and logs live next to the exe
});
builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "logs")));

var options = builder.Configuration.GetSection("ServerComms").Get<BotOptions>() ?? new BotOptions();
var problems = File.Exists(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
    ? options.Validate()
    : new List<string> { "appsettings.json not found next to the exe (copy appsettings.example.json and fill it in)" };
if (problems.Count > 0)
{
    // Refuse to half-run: say exactly what to fix. A service run shows this in the log file and the
    // Windows event log, a console run on screen.
    var message = "ServerComms.Bot can't start, fix appsettings.json:" + Environment.NewLine +
                  string.Join(Environment.NewLine, problems.Select(p => "  - " + p));
    Console.Error.WriteLine(message);
    using var provider = new FileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "logs"));
    provider.CreateLogger("Program").LogCritical("{Message}", message);
    PauseIfConsole();
    return 2;
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<BotState>();
builder.Services.AddSingleton<DatabaseStartup>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseStartup>());
builder.Services.AddSingleton<DiscordConnection>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscordConnection>());
builder.Services.AddHostedService<Crosschat>();
builder.Services.AddHostedService<StatusBoard>();
builder.Services.AddSingleton<Lookups>();
builder.Services.AddSingleton<Linking>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Linking>());
builder.Services.AddSingleton<IInteractionModule>(sp => sp.GetRequiredService<Linking>());
builder.Services.AddSingleton<Rcon>();
builder.Services.AddSingleton<Panels>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Panels>());
builder.Services.AddSingleton<IInteractionModule>(sp => sp.GetRequiredService<Panels>());
builder.Services.AddSingleton<IInteractionModule, Buttons>();
builder.Services.AddSingleton<Commander>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Commander>());
builder.Services.AddSingleton<IInteractionModule>(sp => sp.GetRequiredService<Commander>());
builder.Services.AddHostedService<Interactions>();

var host = builder.Build();
host.Services.GetRequiredService<ILogger<BotOptions>>().LogInformation(
    "ServerComms.Bot {Version} starting for {Game}",
    typeof(BotOptions).Assembly.GetName().Version?.ToString(3), options.Game);
await host.RunAsync();
return 0;

// When started by double-click the window would close before the message can be read.
static void PauseIfConsole()
{
    if (Environment.UserInteractive && !Console.IsInputRedirected)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }
}

// "install": registers the service (delayed auto-start, restarts on failure). "uninstall": removes it.
static int ServiceCommand(string action)
{
    var exe = Environment.ProcessPath!;
    string[][] steps = action == "install"
        ?
        [
            ["create", ServiceName, "binPath=", $"\"{exe}\"", "start=", "delayed-auto", "DisplayName=", "ServerComms Bot"],
            ["description", ServiceName, "ServerComms Discord bot: crosschat, player lists, panels and RCON."],
            ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/60000"],
            ["start", ServiceName],
        ]
        :
        [
            ["stop", ServiceName],
            ["delete", ServiceName],
        ];
    foreach (var step in steps)
    {
        var info = new ProcessStartInfo("sc.exe") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var a in step)
            info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !(action == "uninstall" && step[0] == "stop"))
        {
            Console.Error.WriteLine($"sc {step[0]} failed ({p.ExitCode}). Run this from an administrator prompt.");
            Console.Error.WriteLine(output.Trim());
            return 1;
        }
    }
    Console.WriteLine(action == "install"
        ? $"Installed and started the {ServiceName} service (starts with Windows, restarts on failure)."
        : $"Removed the {ServiceName} service.");
    return 0;
}
