#region Using directives
using System;
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
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using FTOptix.Retentivity;
using FTOptix.OPCUAClient;
using FTOptix.Alarm;
using FTOptix.Core;
#endregion

/// <summary>Keeps the approved-recipe ComboBox it is attached to in sync with Model/LocalRecipeChoices (ComboBox does not follow model changes by itself).</summary>
public class LocalRecipeComboLogic : BaseNetLogic
{
    public override void Start()
    {
        combo = Owner as ComboBox;
        choices = Project.Current.Get("Model/LocalRecipeChoices");
        if (combo == null || choices == null)
        {
            Log.Warning(LogCategory, $"ComboBox or Model/LocalRecipeChoices not found for '{Owner.BrowseName}'.");
            return;
        }

        Synchronize();
        task = new PeriodicTask(Synchronize, 1000, LogicObject);
        task.Start();

        oven = Owner.GetVariable("Oven");
        selected = combo.SelectedItemVariable;
        selected.VariableChange += OnSelectionChanged;
    }

    public override void Stop()
    {
        if (selected != null) selected.VariableChange -= OnSelectionChanged;
        task?.Dispose();
        task = null;
    }

    private void OnSelectionChanged(object sender, VariableChangeEventArgs e)
    {
        Log.Info(LogCategory, "Selection changed: " + e.NewValue.Value);
        var manager = oven?.Value.Value is NodeId id && !id.IsEmpty ? InformationModel.Get(id) : null;
        var target = manager?.GetVariable("LocalSimulation/SelectedRecipe");
        if (target != null && e.NewValue.Value is NodeId choice && !choice.IsEmpty) target.Value = choice;
    }

    private void Synchronize()
    {
        var signature = string.Join("|", choices.Children.Select(choice => choice.BrowseName).OrderBy(name => name, StringComparer.Ordinal));
        if (signature == current) return;

        current = signature;
        combo.Refresh();
    }

    private const string LogCategory = "LocalRecipeComboLogic";
    private ComboBox combo;
    private IUANode choices;
    private IUAVariable oven;
    private IUAVariable selected;
    private PeriodicTask task;
    private string current;
}
