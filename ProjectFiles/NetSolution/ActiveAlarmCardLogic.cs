#region Using directives
using System;
using System.Collections.Generic;
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

public class ActiveAlarmCardLogic : BaseNetLogic
{
    public override void Start()
    {
        activeAlarmCount = LogicObject.GetVariable("ActiveAlarmCount");
        maxSeverity = LogicObject.GetVariable("MaxSeverity");
        hasActiveAlarms = LogicObject.GetVariable("HasActiveAlarms");

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
            int count = 0;
            int mostSevere = 0;

            // Nel progetto 1 = massima gravita (Err), 3 = Info
            foreach (var alarmNode in GetLocalizedAlarms())
            {
                var condition = GetConditionNode(alarmNode);
                if (condition == null || !IsActive(condition))
                    continue;

                count++;
                var severity = GetSeverity(condition, alarmNode);
                if (severity > 0 && (mostSevere == 0 || severity < mostSevere))
                    mostSevere = severity;
            }

            SetIfChanged(activeAlarmCount, count);
            SetIfChanged(maxSeverity, mostSevere);
            SetIfChanged(hasActiveAlarms, count > 0);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"Unable to refresh active alarms: {ex.Message}");
        }
    }

    private IEnumerable<IUANode> GetLocalizedAlarms()
    {
        var retainedAlarms = LogicObject.Context.GetNode(FTOptix.Alarm.Objects.RetainedAlarms);
        var containerId = retainedAlarms?.GetVariable("LocalizedAlarms")?.Value?.Value as NodeId;
        if (containerId == null || containerId.IsEmpty)
            return Enumerable.Empty<IUANode>();

        var container = LogicObject.Context.GetNode(containerId);
        return container?.Children.ToList() ?? Enumerable.Empty<IUANode>();
    }

    private IUANode GetConditionNode(IUANode localizedAlarm)
    {
        var conditionId = localizedAlarm.GetVariable("ConditionId")?.Value?.Value as NodeId;
        if (conditionId == null || conditionId.IsEmpty)
            return null;

        return LogicObject.Context.GetNode(conditionId);
    }

    private static bool IsActive(IUANode condition)
    {
        var input = condition.GetVariable("InputValue")?.Value?.Value;
        var normal = condition.GetVariable("NormalStateValue")?.Value?.Value;
        return input != null && normal != null && Convert.ToInt32(input) != Convert.ToInt32(normal);
    }

    private static int GetSeverity(IUANode condition, IUANode localizedAlarm)
    {
        var value = condition.GetVariable("Severity")?.Value?.Value ?? localizedAlarm.GetVariable("Severity")?.Value?.Value;
        return value == null ? 0 : Convert.ToInt32(value);
    }

    private static void SetIfChanged(IUAVariable variable, object value)
    {
        if (variable == null)
            return;

        if (!Equals(variable.Value?.Value, value))
            variable.Value = new UAValue(value);
    }

    private const string LogCategory = "ActiveAlarmCardLogic";
    private const int RefreshPeriodMs = 1000;

    private IUAVariable activeAlarmCount;
    private IUAVariable maxSeverity;
    private IUAVariable hasActiveAlarms;
    private PeriodicTask refreshTask;
}
