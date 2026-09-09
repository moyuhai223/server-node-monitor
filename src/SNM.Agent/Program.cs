using System.Reflection;
using System.Runtime.InteropServices;
using SNM.Agent.Cli;
using SNM.Agent.Collectors;
using SNM.Agent.Logging;
using SNM.Agent.Net;
using SNM.Agent.Sampling;
using SNM.Contracts;

namespace SNM.Agent;

internal static class Program
{
    public static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

    public static string UserAgent => $"snm-agent/{Version.Split('+')[0]} ({RuntimeInformation.OSDescription.Split(' ')[0]}; {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (Exception ex) when (ex is IOException or System.Security.SecurityException) { }
        if (!CliOptions.TryParse(args, Environment.GetEnvironmentVariable, out var opts, out var error))
        {
            Console.Error.WriteLine($"snm-agent: {error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage.Text);
            return 2;
        }

        switch (opts.Command)
        {
            case AgentCommand.Version:
                Console.WriteLine($"snm-agent {Version} (protocol {ProtocolConstants.ProtocolVersion})");
                return 0;
            case AgentCommand.Help:
                Console.WriteLine(Usage.Text);
                return 0;
        }

        AgentLog.MinimumLevel = opts.LogLevel;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            AgentLog.Error($"unsupported platform: {RuntimeInformation.OSDescription} (Linux and Windows only)");
            return 3;
        }

        ICollectorSet collectors = OperatingSystem.IsWindows() ? CollectorFactory.CreateWindows(opts) : CollectorFactory.CreateLinux(opts);
        var sampler = new SampleBuilder(collectors);

        if (opts.Command == AgentCommand.Test)
        {
            await TestCommand.RunAsync(opts, sampler);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; cts.Cancel(); });
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        AgentLog.Info($"snm-agent {Version} starting: server={opts.Server} key={MaskKey(opts.Key)} proxy={opts.Proxy ?? "(system)"} interval={opts.IntervalMs}ms transport={opts.Transport} netIf={(opts.NetIf is null ? "auto" : string.Join(',', opts.NetIf))} disks={(opts.DiskInclude is null ? "auto" : string.Join(',', opts.DiskInclude))}");
        var session = new AgentSession(opts, sampler);
        await session.RunForeverAsync(cts.Token);
        AgentLog.Info("snm-agent stopped");
        return 0;
    }

    public static string MaskKey(string key) => key.Length >= 9 ? $"{ProtocolConstants.AgentKeyPrefix}****{key[^4..]}" : "****";
}
