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
    private const int Dark = 1;

    private IUAVariable language;
    private IUAVariable darkTheme;
    private IUAVariable imperial;
    private IUAVariable sessionTheme;

    public override void Start()
    {
        language = Owner.GetVariable("LanguageSection/Language/Control/SelectedValue");
        darkTheme = Owner.GetVariable("AppearanceSection/DarkTheme/Control/Checked");
        imperial = Owner.GetVariable("UnitsSection/Imperial/Control/Checked");
        sessionTheme = Session?.GetVariable("Theme");
        if (language == null || darkTheme == null || imperial == null || sessionTheme == null)
        {
            Log.Warning(LogCategory, $"Missing: language={language != null} dark theme={darkTheme != null} units={imperial != null} session theme={sessionTheme != null}.");
            return;
        }

        language.Value = Session.ActualLocaleId;
        darkTheme.Value = Convert.ToInt32(sessionTheme.Value.Value) == Dark;
        imperial.Value = Session.ActualMeasurementSystem != MeasurementSystem.InternationalSystemOfUnits;

        language.VariableChange += OnLanguageChanged;
        darkTheme.VariableChange += OnThemeChanged;
        imperial.VariableChange += OnUnitsChanged;
    }

    public override void Stop()
    {
        if (language != null) language.VariableChange -= OnLanguageChanged;
        if (darkTheme != null) darkTheme.VariableChange -= OnThemeChanged;
        if (imperial != null) imperial.VariableChange -= OnUnitsChanged;
    }

    private void OnLanguageChanged(object sender, VariableChangeEventArgs e)
    {
        var locale = e.NewValue.Value is LocalizedText text ? text.Text : e.NewValue.Value as string;
        if (!string.IsNullOrEmpty(locale) && locale != Session.User.LocaleId)
            Session.User.LocaleId = locale;
    }

    private void OnThemeChanged(object sender, VariableChangeEventArgs e)
    {
        var theme = (bool)e.NewValue.Value ? Dark : 0;
        if (Convert.ToInt32(sessionTheme.Value.Value) != theme) sessionTheme.Value = theme;
    }

    private void OnUnitsChanged(object sender, VariableChangeEventArgs e)
    {
        Session.User.MeasurementSystem = (bool)e.NewValue.Value ? MeasurementSystem.USCustomaryMeasurementSystem : MeasurementSystem.InternationalSystemOfUnits;
    }
}
