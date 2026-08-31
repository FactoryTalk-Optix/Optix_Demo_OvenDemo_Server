#region Using directives
using System;
using System.Linq;
using System.Text.Json;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using UAManagedCore;
#endregion

public class OvensBehaviorLogic : BaseNetLogic
{
    public override void Start()
    {
        // Subscribe to MQTT topic for oven status updates
        statusVariable = Project.Current.GetVariable("Model/IncomingStatuses");
        statusVariable.VariableChange += StatusVariable_VariableChanged;

        // Task to delete ovens that haven't been updated for a while
        deleteOldOvensTask = new PeriodicTask(DeleteOldOvens, 1000, LogicObject);
        deleteOldOvensTask.Start();
    }

    public override void Stop()
    {
    }

    private void DeleteOldOvens()
    {
        foreach (var item in ovensFolder.Children.OfType<OptixOven>())
        {
            if (DateTime.UtcNow - item.LastUpdate > TimeSpan.FromSeconds(15))
            {
                ovensFolder.Remove(item);
            }
        }
    }

    private void StatusVariable_VariableChanged(object sender, VariableChangeEventArgs e)
    {
        var json = e.NewValue.Value?.ToString();
        if (string.IsNullOrEmpty(json))
            return;

        var payload = JsonSerializer.Deserialize<OvenStatusPayload>(json, jsonOptions);
        AddOrUpdateOvenStatus(payload);
    }

    private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private void AddOrUpdateOvenStatus(OvenStatusPayload payload)
    {
        var ovenNode = ovensFolder.Get<OptixOven>($"{payload.OvenId}");

        if (ovenNode == null)
        {
            // Create a new node for the oven
            ovenNode = InformationModel.MakeObject<OptixOven>($"{payload.OvenId}", ovenType.NodeId);
            ovenNode.OvenId = payload.OvenId;
            ovensFolder.Add(ovenNode);
        }

        // Update or add variables for the oven status
        ovenNode.LastUpdate = payload.Timestamp;
        ovenNode.OvenName = payload.OvenName;
        ovenNode.PowerConsumption = ParseInt(payload.PowerConsumption);
        ovenNode.GasConsumption = ParseInt(payload.GasConsumption);
        ovenNode.CurrentPhase = ParseInt(payload.CurrentPhase);
        ovenNode.CurrentTemperature = ParseInt(payload.CurrentTemperature);
        ovenNode.CycleTime = ParseInt(payload.CycleTime);
        ovenNode.CurrentStatus.InputFan = ParseInt(payload.InputFan);
        ovenNode.CurrentStatus.OutputFan = ParseInt(payload.OutputFan);
        ovenNode.CurrentRecipe = payload.CurrentRecipe;
        ovenNode.CurrentStatus.Heating = payload.Heating == "1";
        ovenNode.Latitude = ParseDouble(payload.Latitude);
        ovenNode.Longitude = ParseDouble(payload.Longitude);
    }

    private static double ParseDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        value = value.Replace(",", ".");
        if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedValue))
            return parsedValue;

        return 0;
    }

    private static int ParseInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        value = value.Replace(",", ".");
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedValue))
            return Convert.ToInt32(parsedValue);

        if (int.TryParse(value, out var intValue))
            return intValue;

        return 0;
    }

    private IUAVariable statusVariable;
    private IUANode ovenType = Project.Current.Get("Model/Types/OptixOven");
    private IUANode ovensFolder = Project.Current.Get("Model/Ovens");
    private PeriodicTask deleteOldOvensTask;
}

public class OvenStatusPayload
{
    public DateTime Timestamp { get; set; }
    public string OvenId { get; set; }
    public string OvenName { get; set; }
    public string PowerConsumption { get; set; }
    public string GasConsumption { get; set; }
    public string CurrentPhase { get; set; }
    public string CurrentTemperature { get; set; }
    public string CurrentRecipe { get; set; }
    public string InputFan { get; set; }
    public string OutputFan { get; set; }
    public string CycleTime { get; set; }
    public string Heating { get; set; }
    public string Latitude { get; set; }
    public string Longitude { get; set; }
}

