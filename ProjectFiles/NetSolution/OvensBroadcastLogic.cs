#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NativeUI;
using FTOptix.NetLogic;
using FTOptix.WebUI;
using FTOptix.Alarm;
using FTOptix.RecipeX;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using FTOptix.CoreBase;
using FTOptix.Core;
using static RecipeCommandsLogic;
#endregion

public class OvensBroadcastLogic : BaseNetLogic
{
    public override void Start()
    {
        // Publish ovens status at MQTT broker at regular intervals (500ms)
        publishTask = new PeriodicTask(PublishOvensStatusToMqtt, 500, LogicObject);
        publishTask.Start();
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
        publishTask?.Dispose();
    }

    private void PublishOvensStatusToMqtt()
    {
        try
        {
            // Get the Ovens container from Model/Ovens
            var ovensContainer = Project.Current.Get<IUANode>("Model/Ovens");
            if (ovensContainer == null)
            {
                Log.Warning("PublishOvensStatusToMqtt", "Ovens container not found at Model/Ovens");
                return;
            }

            // Collect all ovens
            var listOfOvenPayloads = new List<Dictionary<string, object>>();

            // Iterate through all children of the Ovens container
            var ovens = ovensContainer.Children.Where(child => child.NodeClass == NodeClass.Object);
            
            foreach (var oven in ovens)
            {
                try
                {
                    var ovenPayload = ExtractOvenStatus(oven);
                    if (ovenPayload != null)
                    {
                        listOfOvenPayloads.Add(ovenPayload);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("PublishOvensStatusToMqtt", $"Error processing oven {oven.BrowseName}: {ex.Message}");
                }
            }

            // Publish list as JSON with serialization options that ignore null values
            var options = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
            var ovensListAsJson = JsonSerializer.Serialize(listOfOvenPayloads, options);
            _ = RecipeCommandsLogic.PublishStringToMqtt(ovensListAsJson, "optix/iot/ovenslist");
        }
        catch (Exception ex)
        {
            Log.Warning("PublishOvensStatusToMqtt", $"Cannot publish ovens status: {ex.Message}");
        }
    }

    private Dictionary<string, object> ExtractOvenStatus(IUANode ovenNode)
    {
        var payload = new Dictionary<string, object>
        {
            { "OvenName", ovenNode.BrowseName }
        };

        // Extract variable values from the oven
        var ovenVariables = ovenNode.Children.OfType<IUAVariable>();
        foreach (var variable in ovenVariables)
        {
            try
            {
                // Get the actual value from the variable - check if it's a ValueWrapper and extract the Value property
                var varValue = variable.Value;
                if (varValue != null)
                {
                    // Try to get the Value property if it exists (for UAVariable wrappers)
                    var valueProperty = varValue.GetType().GetProperty("Value");
                    if (valueProperty != null)
                    {
                        payload[variable.BrowseName] = valueProperty.GetValue(varValue);
                    }
                    else
                    {
                        payload[variable.BrowseName] = varValue;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("ExtractOvenStatus", $"Could not read variable {variable.BrowseName}: {ex.Message}");
            }
        }

        return payload;
    }

    private PeriodicTask publishTask;
}
