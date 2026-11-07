#region Using directives
using System;
using System.Linq;
using UAManagedCore;
using FTOptix.UI;
using FTOptix.NetLogic;
using FTOptix.Core;
#endregion

/// <summary>
/// Loads the light or dark chart page into the WebBrowser it is attached to, following the session theme.
/// With a "LocalizedPage" string variable on the NetLogic it loads eCharts/{LocalizedPage}-{theme}-{locale}.html, following the user language too.
/// </summary>
public class AnalyticsChartLogic : BaseNetLogic
{
    private const string LogCategory = "AnalyticsChartLogic";
    private const int Dark = 1;
    private static readonly string[] Locales = { "en-US", "es-ES", "it-IT", "fr-FR", "de-DE", "zh-CN" };

    private WebBrowser browser;
    private IUAVariable theme;
    private IUAVariable locale;
    private string localizedPage;
    private string current;

    public override void Start()
    {
        browser = Owner as WebBrowser;
        theme = Session?.GetVariable("Theme");
        if (browser == null || theme == null)
        {
            Log.Warning(LogCategory, $"WebBrowser or session Theme not found for '{Owner.BrowseName}'.");
            return;
        }

        localizedPage = Convert.ToString(LogicObject.GetVariable("LocalizedPage")?.Value.Value);
        if (!string.IsNullOrEmpty(localizedPage))
        {
            locale = Session.User?.GetVariable("LocaleId");
            if (locale != null) locale.VariableChange += OnChanged;
        }

        theme.VariableChange += OnChanged;
        Apply();
    }

    public override void Stop()
    {
        if (theme != null) theme.VariableChange -= OnChanged;
        if (locale != null) locale.VariableChange -= OnChanged;
    }

    private void OnChanged(object sender, VariableChangeEventArgs e) => Apply();

    private void Apply()
    {
        var dark = Convert.ToInt32(theme.Value.Value) == Dark;
        string path;
        if (string.IsNullOrEmpty(localizedPage))
        {
            path = dark ? "eCharts/oven-analytics-dark.html" : "eCharts/oven-analytics.html";
        }
        else
        {
            var id = Convert.ToString(locale?.Value.Value);
            if (!Locales.Contains(id)) id = Locales[0];
            path = $"eCharts/{localizedPage}-{(dark ? "dark" : "light")}-{id}.html";
        }
        if (path == current) return;

        current = path;
        browser.URL = ResourceUri.FromProjectRelativePath(path);
    }
}
