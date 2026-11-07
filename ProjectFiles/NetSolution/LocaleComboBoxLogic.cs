#region Using directives
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.UI;
using UAManagedCore;
using FTOptix.HighAvailability;
using FTOptix.Retentivity;
using FTOptix.OPCUAClient;
using OpcUa = UAManagedCore.OpcUa;
#endregion

public class LocaleComboBoxLogic : BaseNetLogic
{
    /// <summary>
    /// This method sets the locale model for the current project based on the project's available locales.
    /// It creates a model containing the locales and adds them to the logic object.
    /// </summary>
    /// <param name="owner">The owner object, which is a ComboBox.</param>
    public override void Start()
    {
        var localeCombo = (ComboBox)Owner;

        string[] projectLocales = Project.Current.Localization.Locales;
        var modelLocales = InformationModel.MakeObject("Locales");
        modelLocales.Children.Clear();

        foreach (var locale in projectLocales)
        {
            var language = InformationModel.MakeVariable(locale, OpcUa.DataTypes.String);
            language.Value = locale;
            modelLocales.Add(language);
        }

        LogicObject.Add(modelLocales);
        localeCombo.Model = modelLocales.NodeId;
    }
}
