#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.RecipeX;
using FTOptix.Store;
using UAManagedCore;
using static RecipeCommandsLogic;
#endregion

public class RecipesBroadcastLogic : BaseNetLogic
{
    public override void Start()
    {
        // Publish recipes at MQTT broker at regular intervals
        publishTask = new PeriodicTask(PublishRecipesToMqtt, 10000, LogicObject);
        publishTask.Start();
        // Initialize recipes on the database if not already done
        recipesInitializationTask = new LongRunningTask(InitializeRecipesInDatabase, LogicObject);
        recipesInitializationTask.Start();
    }

    private void InitializeRecipesInDatabase()
    {
        // Wait for the database to be available
        var store = Project.Current.Get<Store>("DataStores/EmbeddedDatabase");
        if (store == null)
        {
            Log.Error("InitializeRecipesInDatabase", "EmbeddedDatabase not found");
            return;
        }

        // Loop until the database is available, checking cancellation token
        while (store.Status != StoreStatus.Online)
        {
            if (recipesInitializationTask.IsCancellationRequested)
                return;

            Log.Info("InitializeRecipesInDatabase", "Waiting for database to become available...");
            Thread.Sleep(1000);
        }

        // Check if Margherita and Calzone recipes exist
        bool margheritaExists = RecipeExists("Margherita");
        bool calzoneExists = RecipeExists("Calzone");

        // Create Margherita recipe if it doesn't exist
        if (!margheritaExists)
        {
            Log.Info("InitializeRecipesInDatabase", "Creating Margherita recipe...");
            CreateRecipe("Margherita", new RecipeData
            {
                Phase1Duration = 120,
                Phase1Setpoint = 220,
                Phase1InputFan = 50,
                Phase1OutputFan = 30,
                Phase2Duration = 180,
                Phase2Setpoint = 250,
                Phase2InputFan = 60,
                Phase2OutputFan = 40,
                Phase3Duration = 60,
                Phase3Setpoint = 200,
                Phase3InputFan = 40,
                Phase3OutputFan = 20
            });
        }

        // Create Calzone recipe if it doesn't exist
        if (!calzoneExists)
        {
            Log.Info("InitializeRecipesInDatabase", "Creating Calzone recipe...");
            CreateRecipe("Calzone", new RecipeData
            {
                Phase1Duration = 90,
                Phase1Setpoint = 200,
                Phase1InputFan = 45,
                Phase1OutputFan = 25,
                Phase2Duration = 240,
                Phase2Setpoint = 230,
                Phase2InputFan = 55,
                Phase2OutputFan = 35,
                Phase3Duration = 90,
                Phase3Setpoint = 180,
                Phase3InputFan = 35,
                Phase3OutputFan = 15
            });
        }

        if (!margheritaExists || !calzoneExists)
        {
            Log.Info("InitializeRecipesInDatabase", "Recipe initialization completed");
        }
        else
        {
            Log.Info("InitializeRecipesInDatabase", "All required recipes already exist");
        }
    }

    private bool RecipeExists(string recipeName)
    {
        if (recipeSchema == null || string.IsNullOrEmpty(recipeName))
            return false;

        // Use GetRecipes API to check if recipe exists
        var result = recipeSchema.GetRecipes();
        if (result.ResultCode != GetRecipesResultCode.Success)
            return false;

        // Check if any recipe matches the name
        foreach (var recipe in result.Recipes)
        {
            if (recipe.RecipeId.Name == recipeName)
                return true;
        }

        return false;
    }

    private void CreateRecipe(string recipeName, RecipeData data)
    {
        try
        {
            var recipeId = new RecipeId { Name = recipeName, Version = "1" };

            // Get the target node from the recipe schema
            var targetNodeId = recipeSchema.TargetNode;
            if (targetNodeId == null || targetNodeId == NodeId.Empty)
            {
                Log.Error("CreateRecipe", "RecipeSchema has no target node configured");
                return;
            }

            // Get the target object to temporarily set values
            var targetNode = InformationModel.Get(targetNodeId);
            if (targetNode == null)
            {
                Log.Error("CreateRecipe", "Target node not found");
                return;
            }

            // Store original values so we can restore them
            var originalValues = new Dictionary<string, int>();
            SaveOriginalPhaseValues(targetNode, "OvenPhase1", originalValues);
            SaveOriginalPhaseValues(targetNode, "OvenPhase2", originalValues);
            SaveOriginalPhaseValues(targetNode, "OvenPhase3", originalValues);

            // Set the recipe values on the target
            SetPhaseValuesOnTarget(targetNode, "OvenPhase1", data.Phase1Duration, data.Phase1Setpoint, data.Phase1InputFan, data.Phase1OutputFan);
            SetPhaseValuesOnTarget(targetNode, "OvenPhase2", data.Phase2Duration, data.Phase2Setpoint, data.Phase2InputFan, data.Phase2OutputFan);
            SetPhaseValuesOnTarget(targetNode, "OvenPhase3", data.Phase3Duration, data.Phase3Setpoint, data.Phase3InputFan, data.Phase3OutputFan);

            // Transfer from target to store to create the recipe
            var result = recipeSchema.TransferFromTargetToStore(recipeId, targetNodeId, false, ErrorPolicy.BestEffort);

            if (result == TransferFromTargetToStoreResultCode.SuccessRecipeCreated ||
                result == TransferFromTargetToStoreResultCode.SuccessRecipeUpdated)
            {
                Log.Info("CreateRecipe", $"Recipe '{recipeName}' created successfully");

                // Set the Approved metadata to true by default
                var metadataResult = recipeSchema.SetRecipeMetadataValue(recipeId, "Approved", true);
                if (metadataResult != SetRecipeMetadataValueResultCode.Success)
                {
                    Log.Warning("CreateRecipe", $"Failed to set Approved metadata for '{recipeName}': {metadataResult}");
                }
            }
            else
            {
                Log.Error("CreateRecipe", $"Failed to create recipe '{recipeName}': {result}");
            }

            // Restore original values on the target
            RestoreOriginalPhaseValues(targetNode, "OvenPhase1", originalValues);
            RestoreOriginalPhaseValues(targetNode, "OvenPhase2", originalValues);
            RestoreOriginalPhaseValues(targetNode, "OvenPhase3", originalValues);
        }
        catch (Exception ex)
        {
            Log.Error("CreateRecipe", $"Failed to create recipe '{recipeName}': {ex.Message}");
        }
    }

    private void SaveOriginalPhaseValues(IUANode targetNode, string phaseName, Dictionary<string, int> storage)
    {
        var phase = targetNode.GetObject(phaseName);
        if (phase == null) return;

        storage[$"{phaseName}/Duration"] = (int)(phase.GetVariable("Duration")?.Value ?? 0);
        storage[$"{phaseName}/Setpoint"] = (int)(phase.GetVariable("Setpoint")?.Value ?? 0);
        storage[$"{phaseName}/InputFan"] = (int)(phase.GetVariable("InputFan")?.Value ?? 0);
        storage[$"{phaseName}/OutputFan"] = (int)(phase.GetVariable("OutputFan")?.Value ?? 0);
    }

    private void RestoreOriginalPhaseValues(IUANode targetNode, string phaseName, Dictionary<string, int> storage)
    {
        var phase = targetNode.GetObject(phaseName);
        if (phase == null) return;

        phase.GetVariable("Duration")?.SetValue(storage.GetValueOrDefault($"{phaseName}/Duration", 0));
        phase.GetVariable("Setpoint")?.SetValue(storage.GetValueOrDefault($"{phaseName}/Setpoint", 0));
        phase.GetVariable("InputFan")?.SetValue(storage.GetValueOrDefault($"{phaseName}/InputFan", 0));
        phase.GetVariable("OutputFan")?.SetValue(storage.GetValueOrDefault($"{phaseName}/OutputFan", 0));
    }

    private void SetPhaseValuesOnTarget(IUANode targetNode, string phaseName, int duration, int setpoint, int inputFan, int outputFan)
    {
        var phase = targetNode.GetObject(phaseName);
        if (phase == null)
        {
            Log.Warning("SetPhaseValuesOnTarget", $"Phase '{phaseName}' not found on target");
            return;
        }

        phase.GetVariable("Duration")?.SetValue(duration);
        phase.GetVariable("Setpoint")?.SetValue(setpoint);
        phase.GetVariable("InputFan")?.SetValue(inputFan);
        phase.GetVariable("OutputFan")?.SetValue(outputFan);
    }

    private class RecipeData
    {
        public int Phase1Duration { get; set; }
        public int Phase1Setpoint { get; set; }
        public int Phase1InputFan { get; set; }
        public int Phase1OutputFan { get; set; }
        public int Phase2Duration { get; set; }
        public int Phase2Setpoint { get; set; }
        public int Phase2InputFan { get; set; }
        public int Phase2OutputFan { get; set; }
        public int Phase3Duration { get; set; }
        public int Phase3Setpoint { get; set; }
        public int Phase3InputFan { get; set; }
        public int Phase3OutputFan { get; set; }
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
        publishTask?.Dispose();
        recipesInitializationTask?.Dispose();
    }

    private void PublishRecipesToMqtt()
    {
        try
        {
            // Retrieve list of recipes from the DB
            var listOfRecipes = recipeSchema.GetRecipes();
            if (listOfRecipes.ResultCode != GetRecipesResultCode.Success)
            {
                Log.Warning("PublishRecipesToMqtt", "Cannot load recipes list from database");
            }

            // Prepare the list of recipes for publishing
            var listOfRecipePayloads = new List<ExtendedRecipesPayload>();

            // Build payload
            foreach (var recipeFromStore in listOfRecipes.Recipes)
            {
                // Get metadata for this recipe
                var recipesMetadataList = recipeSchema.GetRecipeMetadataValues(recipeFromStore.RecipeId);
                var recipeData = recipesMetadataList.MetadataValues.ToDictionary(
                    item => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(item.Name), 
                    item => Convert.ToBoolean(item.Value)
                );

                var recipeValuesPayload = RecipeCommandsLogic.RecipeIdToRecipePayload(recipeSchema, recipeFromStore.RecipeId);
                var recipePayload = new ExtendedRecipesPayload
                {
                    RecipeName = recipeValuesPayload.RecipeName,
                    Version = recipeValuesPayload.Version,
                    Approved = recipeData["Approved"],
                    Used = recipeData["Used"],
                    Deprecated = recipeData["Deprecated"],
                    Data = recipeValuesPayload.Data
                };
                
                listOfRecipePayloads.Add(recipePayload);
            }

            // Publish list
            var recipesListAsJson = JsonSerializer.Serialize(listOfRecipePayloads);
            _ = RecipeCommandsLogic.PublishStringToMqtt(recipesListAsJson, "optix/iot/recipeslist");
        }
        catch (Exception ex)
        {
            Log.Warning("PublishRecipesToMqtt", $"Cannot publish recipes: {ex.Message}");
        }
    }

    public class ExtendedRecipesPayload : RecipeCommandsLogic.RecipePayload
    {
        public bool Approved { get; set; }
        public bool Used { get; set; }
        public bool Deprecated { get; set; }
    }

    private RecipeSchema recipeSchema = Project.Current.Get<FTOptix.RecipeX.RecipeSchema>("Recipes/RecipeSchema");
    private PeriodicTask publishTask;
    private LongRunningTask recipesInitializationTask;
}
