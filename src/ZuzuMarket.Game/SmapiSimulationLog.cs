using StardewModdingAPI;
using ZuzuMarket.Core.Simulation;

namespace ZuzuMarket.Game;

/// <summary>Conecta el log de la simulación con la consola de SMAPI.</summary>
internal sealed class SmapiSimulationLog : ISimulationLog
{
    private readonly IMonitor _monitor;

    public SmapiSimulationLog(IMonitor monitor)
    {
        _monitor = monitor;
    }

    public void Info(string message) => _monitor.Log(message, LogLevel.Debug);

    public void Warn(string message) => _monitor.Log(message, LogLevel.Warn);
}
