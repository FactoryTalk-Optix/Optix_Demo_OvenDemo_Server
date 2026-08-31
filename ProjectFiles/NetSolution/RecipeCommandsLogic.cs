#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.RecipeX;
using UAManagedCore;
using static RecipeCommandsLogic;
#endregion

public class RecipeCommandsLogic : BaseNetLogic
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    [ExportMethod]
    public void ApplyRecipeToAllOvens(NodeId recipeSchemaNodeId, NodeId recipeIdNodeId, out TransferFromStoreToTargetResultCode resultCode)
    {
        var recipeSchema = InformationModel.Get<FTOptix.RecipeX.RecipeSchema>(recipeSchemaNodeId);
        var recipeId = (FTOptix.RecipeX.RecipeId)InformationModel.GetVariable(recipeIdNodeId).Value;
        var ovensFolder = Project.Current.Get<Folder>("Model/Ovens");

        resultCode = TransferFromStoreToTargetResultCode.Success;

        foreach (var oven in ovensFolder.Children.OfType<FakeOptixOven>())
        {
            var result = recipeSchema.TransferFromStoreToTarget(recipeId, oven.NodeId, ErrorPolicy.Strict);
            if (result != TransferFromStoreToTargetResultCode.Success)
            {
                resultCode = result;
            }
        }

        PublishRecipeToMqtt(recipeSchema, recipeId);
    }

    public static void PublishRecipeToMqtt(FTOptix.RecipeX.RecipeSchema recipeSchema, FTOptix.RecipeX.RecipeId recipeId)
    {
        // Build the recipe object
        var recipePayload = RecipeIdToRecipePayload(recipeSchema, recipeId);

        // Publish the recipe data to MQTT
        PublishStringToMqtt(JsonSerializer.Serialize(recipePayload), "optix/iot/recipes");
    }

    public static RecipePayload RecipeIdToRecipePayload(FTOptix.RecipeX.RecipeSchema recipeSchema, FTOptix.RecipeX.RecipeId recipeId)
    {
        // Get the recipe data as a dictionary
        var recipeDataItems = recipeSchema.GetDataItems(recipeId);
        var recipeData = recipeDataItems.DataItems.ToDictionary(item => $"{item.ItemRelativeBrowsePath[0]}/{item.DataItemRelativeBrowsePath[0]}", item => item.Value);

        // Append info about the recipe
        return new RecipePayload
        {
            RecipeName = recipeId.Name,
            Version = recipeId.Version,
            Data = recipeData
        };
    }

    public static bool PublishStringToMqtt(string payload, string topic)
    {
        try
        {
            var mqttClient = Project.Current.Get<FTOptix.MQTTClient.MQTTClient>("MQTT/MQTTClient");
            var mqttConfig = new OptixMqttConfig(topic);
            var result = mqttClient.Publish(mqttConfig.Serialize(), payload);
            Log.Verbose1($"Published recipe to MQTT with result: {result}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("PublishStringToMqtt", $"Cannot publish recipe: {ex.Message}");
            return false;
        }
    }

    public class RecipePayload
    {
        public string RecipeName { get; set; }
        public string Version { get; set; }
        public Dictionary<string, object> Data { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class OptixMqttConfig
    {
        public string Topic { get; }
        public int QoS { get; }
        public bool Retain { get; }

        public OptixMqttConfig(string topicName, int qos = 2, bool retain = true)
        {
            Topic = topicName;
            QoS = qos;
            Retain = retain;
        }

        public string Serialize()
        {
            return JsonSerializer.Serialize(this);
        }
    }
}
