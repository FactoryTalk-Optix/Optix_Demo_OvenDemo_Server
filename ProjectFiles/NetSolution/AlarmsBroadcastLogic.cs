#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UAManagedCore;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NativeUI;
using FTOptix.WebUI;
using FTOptix.RecipeX;
using FTOptix.Alarm;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.Core;
#endregion

public class AlarmsBroadcastLogic : BaseNetLogic
{
    public override void Start()
    {
        incomingAlarmsVariable = Project.Current.GetVariable(IncomingAlarmsPath);
        if (incomingAlarmsVariable == null)
        {
            Log.Warning(LogCategory, $"{IncomingAlarmsPath} variable not found");
            return;
        }

        incomingAlarmsVariable.VariableChange += OnIncomingAlarmsChanged;

        var startupJson = incomingAlarmsVariable.Value?.Value?.ToString();
        if (!string.IsNullOrWhiteSpace(startupJson))
            SynchronizeRemoteAlarms(DeserializeIncomingPayload(startupJson));
    }

    public override void Stop()
    {
        if (incomingAlarmsVariable != null)
            incomingAlarmsVariable.VariableChange -= OnIncomingAlarmsChanged;

        incomingAlarmsVariable = null;
    }

    private void OnIncomingAlarmsChanged(object sender, VariableChangeEventArgs e)
    {
        try
        {
            var json = e.NewValue?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(json))
            {
                RemoveStaleRemoteAlarms(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                return;
            }

            var remoteAlarms = DeserializeIncomingPayload(json);
            SynchronizeRemoteAlarms(remoteAlarms);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"Cannot process incoming alarms: {ex.Message}");
        }
    }

    private List<AlarmPayload> DeserializeIncomingPayload(string json)
    {
        try
        {
            var payloadList = JsonSerializer.Deserialize<List<AlarmPayload>>(json, jsonOptions);
            if (payloadList != null)
                return payloadList;
        }
        catch
        {
        }

        try
        {
            var singlePayload = JsonSerializer.Deserialize<AlarmPayload>(json, jsonOptions);
            if (singlePayload != null)
                return new List<AlarmPayload> { singlePayload };
        }
        catch
        {
        }

        Log.Warning(LogCategory, "Unable to parse incoming alarms payload");
        return new List<AlarmPayload>();
    }

    private void SynchronizeRemoteAlarms(List<AlarmPayload> remoteAlarms)
    {
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var payload in remoteAlarms.Where(a => a != null && a.IsActive))
        {
            var key = BuildRemoteAlarmKey(payload);
            activeKeys.Add(key);
            UpsertRemoteAlarm(payload, key);
        }

        RemoveStaleRemoteAlarms(activeKeys);
    }

    private void UpsertRemoteAlarm(AlarmPayload payload, string key)
    {
        var remoteFolder = EnsureRemoteAlarmsFolder();
        if (remoteFolder == null)
            return;

        var alarmBrowseName = RemoteAlarmPrefix + key;

        var remoteAlarm = remoteFolder.Get<AlarmFromServer>(alarmBrowseName);
        if (remoteAlarm == null)
        {
            remoteAlarm = InformationModel.MakeObject<AlarmFromServer>(alarmBrowseName, Optix_Demo_OvenDemo_Server.ObjectTypes.AlarmFromServer);
            remoteFolder.Add(remoteAlarm);
        }

        remoteAlarm.AlarmName = payload.AlarmName ?? string.Empty;
        remoteAlarm.SourceName = payload.SourceName ?? string.Empty;
        remoteAlarm.Message = payload.Message ?? string.Empty;
        remoteAlarm.Severity = payload.Severity.ToString();
        remoteAlarm.IsActive = payload.IsActive;
        remoteAlarm.IsAcknowledged = payload.IsAcknowledged;
        remoteAlarm.IsConfirmed = payload.IsConfirmed;
        remoteAlarm.Time = payload.Time;
        remoteAlarm.Timestamp = payload.Timestamp;
        remoteAlarm.OvenName = payload.OvenName ?? string.Empty;
        remoteAlarm.OvenId = payload.OvenId ?? string.Empty;
    }

    private void RemoveStaleRemoteAlarms(HashSet<string> activeKeys)
    {
        var remoteFolder = GetRemoteAlarmsFolder();
        if (remoteFolder == null)
            return;

        var staleAlarmNodes = remoteFolder.Children
            .Where(child => child.BrowseName.StartsWith(RemoteAlarmPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(child => !activeKeys.Contains(child.BrowseName.Substring(RemoteAlarmPrefix.Length)))
            .ToList();

        foreach (var staleAlarmNode in staleAlarmNodes)
        {
            staleAlarmNode.Delete();
        }
    }

    private IUAObject EnsureRemoteAlarmsFolder()
    {
        var remoteFolder = Project.Current.Get(RemoteFolderPath) as IUAObject;
        if (remoteFolder != null)
            return remoteFolder;

        var model = Project.Current.Get(ModelPath) as IUAObject;
        if (model == null)
        {
            Log.Warning(LogCategory, "Model node not found");
            return null;
        }

        remoteFolder = InformationModel.MakeObject(RemoteFolderName);
        model.Add(remoteFolder);
        return remoteFolder;
    }

    private IUAObject GetRemoteAlarmsFolder()
    {
        return Project.Current.Get(RemoteFolderPath) as IUAObject;
    }

    private static string BuildRemoteAlarmKey(AlarmPayload payload)
    {
        var source = payload.SourceName ?? string.Empty;
        var name = payload.AlarmName ?? string.Empty;
        return SanitizeBrowseName(source + "__" + name);
    }

    private static string SanitizeBrowseName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown";

        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_')
            .ToArray();

        return new string(chars);
    }

    public class AlarmPayload
    {
        public string AlarmName { get; set; }
        public string SourceName { get; set; }
        public string Message { get; set; }
        public ushort Severity { get; set; }
        public bool IsActive { get; set; }
        public bool IsAcknowledged { get; set; }
        public string OvenName { get; set; }
        public string OvenId { get; set; }
        public bool IsConfirmed { get; set; }
        public DateTime Time { get; set; }
        public DateTime Timestamp { get; set; }
    }

    private const string LogCategory = "AlarmsBroadcastLogic";
    private const string IncomingAlarmsPath = "Model/IncomingAlarms";
    private const string ModelPath = "Model";
    private const string RemoteFolderName = "RemoteAlarms";
    private const string RemoteFolderPath = ModelPath + "/" + RemoteFolderName;
    private const string RemoteAlarmPrefix = "RemoteMqttAlarm__";

    private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private IUAVariable incomingAlarmsVariable;
}
