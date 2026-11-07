#region Using directives
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.CoreBase;
using FTOptix.NativeUI;
using FTOptix.NetLogic;
using FTOptix.WebUI;
using FTOptix.RecipeX;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using FTOptix.Alarm;
using FTOptix.Core;
using FTOptix.Retentivity;
using FTOptix.OPCUAClient;
#endregion

public class FleetGridLogic : BaseNetLogic
{
    public override void Start()
    {
        cardsContainer = LogicObject.Owner.Get(CardsContainerPath);
        if (cardsContainer == null)
        {
            Log.Warning(LogCategory, $"{CardsContainerPath} not found under {LogicObject.Owner.BrowseName}");
            return;
        }

        refreshTask = new PeriodicTask(Refresh, RefreshPeriodMs, LogicObject);
        refreshTask.Start();
    }

    public override void Stop()
    {
        refreshTask?.Dispose();
        refreshTask = null;
    }

    private void Refresh()
    {
        try
        {
            var managersFolder = GetManagersFolder();
            if (managersFolder == null)
                return;

            var onlyMonitored = LogicObject.GetVariable("MonitoredFilter")?.Value?.Value is bool filter && filter;
            var managers = managersFolder.Children.OfType<OvenEdgeManager>()
                .Where(m => !onlyMonitored || m.IsMonitored)
                .ToList();
            var expectedNames = new HashSet<string>(managers.Select(m => CardPrefix + OvenEdgeManagerBehavior.GetDeviceId(m.BrowseName)));

            foreach (var manager in managers)
            {
                var cardName = CardPrefix + OvenEdgeManagerBehavior.GetDeviceId(manager.BrowseName);
                if (cardsContainer.Get(cardName) != null)
                    continue;

                var card = InformationModel.MakeObject<OvenCard>(cardName, Optix_Demo_OvenDemo_Server.ObjectTypes.OvenCard);
                // The card's bindings go through DataPointer/TelemetryDataPointer/..., so it must point at the manager.
                card.GetVariable("DataPointer").Value = new UAValue(manager.NodeId);
                cardsContainer.Add(card);
            }

            var staleCards = cardsContainer.Children
                .Where(c => c.BrowseName.StartsWith(CardPrefix, StringComparison.Ordinal) && !expectedNames.Contains(c.BrowseName))
                .ToList();

            foreach (var staleCard in staleCards)
                staleCard.Delete();
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"Unable to refresh fleet cards: {ex.Message}");
        }
    }

    // The "FleetFolder" variable name is kept so existing UI instances don't break; it must point to the managers folder.
    private IUANode GetManagersFolder()
    {
        var folderId = LogicObject.GetVariable("FleetFolder")?.Value?.Value as NodeId;
        if (folderId != null && !folderId.IsEmpty)
            return LogicObject.Context.GetNode(folderId);

        return Project.Current.Get(DefaultManagersFolderPath);
    }

    private const string LogCategory = "FleetGridLogic";
    private const string DefaultManagersFolderPath = "Model/" + OvenEdgeManagerBehavior.ManagersFolderName;
    private const string CardsContainerPath = "div/Content";
    private const string CardPrefix = "Card_";
    private const int RefreshPeriodMs = 1000;

    private IUANode cardsContainer;
    private PeriodicTask refreshTask;
}

/// <summary>
/// Publishes, for every oven (local or remote), the operator-facing texts under manager/Details and the chart data files
/// served to the Oven details dialog. Runs once per second.
/// </summary>
internal sealed class OvenDetailsRuntime
{
    private const string Category = "OvenDetails";
    private static readonly string[] PhaseNames = { "PreHeat", "Bake", "Cooldown" };
    private readonly OvenCycleTraceStore traces = new OvenCycleTraceStore();
    private readonly Dictionary<string, string> published = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, long> cycleStarts = new Dictionary<string, long>(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Checked, OvenCycleSnapshotPhase[] Phases)> profiles = new Dictionary<string, (long, OvenCycleSnapshotPhase[])>(StringComparer.Ordinal);
    private readonly HashSet<string> pages = new HashSet<string>(StringComparer.Ordinal);
    private string directory;
    private long nextError;

    public void Tick()
    {
        var managers = Project.Current.Get("Model/" + OvenEdgeManagerBehavior.ManagersFolderName)?.Children.ToList();
        var fleet = Project.Current.Get(OvenEdgeManagerBehavior.TelemetryFolderPath);
        if (managers == null || fleet == null) return;

        foreach (var manager in managers)
        {
            if (!manager.BrowseName.StartsWith(OvenEdgeManagerBehavior.NamePrefix, StringComparison.Ordinal)) continue;
            var id = OvenEdgeManagerBehavior.GetDeviceId(manager.BrowseName);
            var telemetry = fleet.Get(id);
            if (telemetry == null || !OvenMqttClientHelper.IsValidDeviceId(id)) continue;
            try { Update(id, manager, telemetry); }
            catch (Exception error) { Warn(id + ": " + error.Message); }
        }
    }

    private void Update(string id, IUANode manager, IUANode telemetry)
    {
        var online = manager.GetVariable("Connection")?.Value.Value is bool connected && connected;
        var local = LocalFleetRuntime.IsLocalManager(manager);
        var phase = (int)ReadDouble(telemetry, "CurrentPhase");
        var temperature = ReadDouble(telemetry, "Temperature");
        var setpoint = ReadDouble(telemetry, "TemperatureSetpoint");
        var phaseElapsed = ReadDouble(telemetry, "PhaseElapsedSeconds");
        var recipe = ReadApplied(telemetry);
        var recipeText = recipe == null || string.IsNullOrEmpty(recipe.Name) ? string.Empty : $"{recipe.Name} v{recipe.Version}";
        var profile = GetProfile(recipe);
        var state = (int)ReadDouble(telemetry, "ExecutionState");
        var running = online && phase >= 1 && state != 0 && state != 5;

        if (running && !cycleStarts.ContainsKey(id)) cycleStarts[id] = Environment.TickCount64;
        if (!running) cycleStarts.Remove(id);
        var fallback = cycleStarts.TryGetValue(id, out var started) ? (Environment.TickCount64 - started) / 1000.0 : 0.0;
        var elapsed = OvenCycleSnapshot.Elapsed(profile, phase, phaseElapsed, fallback);
        traces.Record(id, running, elapsed, temperature);
        WriteChart(id, OvenCycleSnapshot.Build(id, online, running, phase, temperature, setpoint, elapsed, recipeText, profile, traces.Get(id)));

        var details = FleetAnalyticsCollector.EnsureFolder(manager, "Details");
        var phaseName = profile != null && phase >= 1 && phase <= profile.Length ? profile[phase - 1].Name : $"Phase {phase}";
        var phaseLength = profile != null && phase >= 1 && phase <= profile.Length ? profile[phase - 1].End - profile[phase - 1].Start : 0.0;
        var faulted = manager.GetVariable("ProcessFault")?.Value.Value is bool fault && fault;
        var disconnected = manager.GetVariable("ConnectionAlarmActive")?.Value.Value is bool alarm && alarm;
        var reported = (int)ReadDouble(telemetry, "ActiveAlarmCount");
        var alarms = new List<string>();
        if (disconnected) alarms.Add("Disconnected");
        if (faulted) alarms.Add("Process fault");
        if (!local && reported > 0) alarms.Add($"{reported} reported by the oven");

        var cycles = ReadDouble(telemetry, "CyclesCompletedTotal");
        Flag(details, "IsLocal", local);
        Text(details, "ModeText", local ? "Local simulation" : "Remote edge");
        Text(details, "ConnectionText", online ? "Connected" : "Disconnected");
        Text(details, "StateText", !online ? "Offline" : running ? $"Running - {phaseName}" : "Idle");
        Text(details, "TemperatureText", online ? $"{temperature:0.0} °C / {setpoint:0.#} °C" : "--");
        Text(details, "PhaseText", !running ? "--" : phaseLength > 0 ? $"{phase} / {profile.Length} - {phaseName} ({Clock(phaseElapsed)} / {Clock(phaseLength)})" : $"{phaseName} ({Clock(phaseElapsed)})");
        Text(details, "BatchText", Convert.ToString(telemetry.GetVariable("CurrentBatchId")?.Value.Value) is string batch && batch.Length > 0 ? batch : "--");
        Text(details, "RecipeText", recipeText.Length > 0 ? recipeText : "--");
        Text(details, "PowerText", $"{ReadDouble(telemetry, "PowerDraw"):0.00} kW");
        Text(details, "EnergyTodayText", $"{ReadDouble(telemetry, "EnergyConsumedToday"):0.000} kWh");
        Text(details, "CyclesTodayText", $"{ReadDouble(telemetry, "CyclesCompletedToday"):0}");
        Text(details, "TotalsText", $"{cycles:0} cycles - {ReadDouble(telemetry, "GoodCyclesTotal"):0} good - {ReadDouble(telemetry, "RejectedCyclesTotal"):0} rejected");
        Text(details, "RunTimeText", TimeSpan.FromSeconds(ReadDouble(telemetry, "RunningSecondsTotal")).ToString(@"hh\:mm\:ss"));
        Text(details, "UptimeText", TimeSpan.FromSeconds(ReadDouble(telemetry, "UptimeSeconds")).ToString(@"d\.hh\:mm\:ss"));
        Text(details, "AlarmText", alarms.Count == 0 ? "None" : string.Join(", ", alarms));
        Text(details, "LastUpdateText", telemetry.GetVariable("Timestamp")?.Value.Value is DateTime stamp && stamp.Year >= 2000 ? stamp.ToLocalTime().ToString("HH:mm:ss") : "--");
    }

    private static RecipeId ReadApplied(IUANode telemetry) => telemetry.GetVariable("AppliedRecipe")?.Value.Value as RecipeId;

    // Recipes are immutable once released, so a profile is read once and only re-checked while it is still unknown.
    private OvenCycleSnapshotPhase[] GetProfile(RecipeId recipe)
    {
        if (recipe == null || string.IsNullOrEmpty(recipe.Name) || string.IsNullOrEmpty(recipe.Version)) return null;
        var key = recipe.Name + "\0" + recipe.Version;
        if (profiles.TryGetValue(key, out var entry) && (entry.Phases != null || Environment.TickCount64 - entry.Checked < 30000)) return entry.Phases;

        OvenCycleSnapshotPhase[] phases = null;
        try
        {
            var schema = Project.Current.Get<RecipeSchema>("Recipes/RecipeSchema");
            if (schema != null)
            {
                var list = new List<OvenCycleSnapshotPhase>();
                double start = 0;
                foreach (var name in PhaseNames)
                {
                    var duration = Convert.ToDouble(LocalOvenController.ReadValue(schema, recipe, name, "Duration"));
                    var phaseSetpoint = Convert.ToDouble(LocalOvenController.ReadValue(schema, recipe, name, "TemperatureSetpoint"));
                    list.Add(new OvenCycleSnapshotPhase(name, start, start + duration, phaseSetpoint));
                    start += duration;
                }
                phases = list.ToArray();
            }
        }
        catch (Exception) { phases = null; }
        profiles[key] = (Environment.TickCount64, phases);
        return phases;
    }

    private void WriteChart(string id, string json)
    {
        if (directory == null)
        {
            directory = ResourceUri.FromProjectRelativePath("eCharts/cycle").Uri;
            Directory.CreateDirectory(directory);
        }
        if (pages.Add(id))
        {
            foreach (var theme in new[] { "light", "dark" }) WriteIfDifferent(Path.Combine(directory, $"{id}-{theme}.html"), Page(id, theme));
        }
        if (published.TryGetValue(id, out var previous) && previous == json) return;

        var path = Path.Combine(directory, id + "-data.js");
        var temp = path + ".tmp";
        File.WriteAllText(temp, "window.OVEN_CYCLE = " + json + ";");
        File.Move(temp, path, true);
        published[id] = json;
    }

    private static string Page(string id, string theme) =>
        "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<link rel=\"stylesheet\" href=\"../oven-cycle.css\">\n</head>\n" +
        $"<body data-theme=\"{theme}\" data-oven=\"{id}\">\n<div id=\"status\"></div>\n<div id=\"chart\"></div>\n" +
        "<script src=\"../echarts.min.js\"></script>\n<script src=\"../oven-cycle.js\"></script>\n</body>\n</html>\n";

    private static void WriteIfDifferent(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        File.WriteAllText(path, content);
    }

    private static string Clock(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    private static double ReadDouble(IUANode node, string name)
    {
        var value = node.GetVariable(name)?.Value.Value;
        if (value == null) return 0;
        var number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        return double.IsFinite(number) ? number : 0;
    }

    private static void Text(IUANode folder, string name, string value)
    {
        var variable = FleetAnalyticsCollector.EnsureVariable(folder, name, OpcUa.DataTypes.String);
        if (Convert.ToString(variable.Value.Value) != value) variable.Value = new UAValue(value);
    }

    private static void Flag(IUANode folder, string name, bool value)
    {
        var variable = FleetAnalyticsCollector.EnsureVariable(folder, name, OpcUa.DataTypes.Boolean);
        if (!(variable.Value.Value is bool current) || current != value) variable.Value = new UAValue(value);
    }

    private void Warn(string message)
    {
        if (Environment.TickCount64 < nextError) return;
        nextError = Environment.TickCount64 + 15000;
        Log.Warning(Category, message);
    }
}
