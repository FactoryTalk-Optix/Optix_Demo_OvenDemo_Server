#region Using directives
using System;
using UAManagedCore;
using FTOptix.UI;
using FTOptix.NetLogic;
using FTOptix.Core;
#endregion

/// <summary>Applies the preferences of the Settings screen: language and units go to the session user (it overrides the session), the theme to the session.</summary>
public class SettingsLogic : BaseNetLogic
{
    private const string LogCategory = "SettingsLogic";

    private IUAVariable language;
    private IUAVariable imperial;

    public override void Start()
    {
        language = Owner.GetVariable("LanguageSection/Language/Control/SelectedValue");
        imperial = Owner.GetVariable("UnitsSection/Imperial/Control/Checked");
        if (language == null || imperial == null)
        {
            Log.Warning(LogCategory, $"Missing: language={language != null} units={imperial != null}.");
            return;
        }

        language.Value = Session.ActualLocaleId;
        imperial.Value = Session.ActualMeasurementSystem != MeasurementSystem.InternationalSystemOfUnits;

        language.VariableChange += OnLanguageChanged;
        imperial.VariableChange += OnUnitsChanged;
    }

    public override void Stop()
    {
        if (language != null) language.VariableChange -= OnLanguageChanged;
        if (imperial != null) imperial.VariableChange -= OnUnitsChanged;
    }

    private void OnLanguageChanged(object sender, VariableChangeEventArgs e)
    {
        var locale = e.NewValue.Value is LocalizedText text ? text.Text : e.NewValue.Value as string;
        if (!string.IsNullOrEmpty(locale) && locale != Session.User.LocaleId)
            Session.User.LocaleId = locale;
    }

    private void OnUnitsChanged(object sender, VariableChangeEventArgs e)
    {
        Session.User.MeasurementSystem = (bool)e.NewValue.Value ? MeasurementSystem.USCustomaryMeasurementSystem : MeasurementSystem.InternationalSystemOfUnits;
    }
}
