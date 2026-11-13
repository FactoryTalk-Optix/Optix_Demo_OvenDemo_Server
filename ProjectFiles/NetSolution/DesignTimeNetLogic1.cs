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

public class DesignTimeNetLogic1 : BaseNetLogic
{
    [ExportMethod]
    public void BuildAnalyticsPage()
    {
        var model = FleetAnalyticsCollector.EnsureModel(out var rows, out _, out _);
        var screens = Project.Current.Get("UI/Screens/MainWindowAccessible");

        var cards = screens?.Get("OvensOverview/div/TopCardsInfo");
        if (cards != null)
        {
            BindCard(cards.Get("Card1"), "Connected ovens", model.GetVariable("ConnectedOvens"), null, model.GetVariable("ConnectedRatioText"), model.GetVariable("ConnectedText"));
            BindCard(cards.Get("Card2"), "Active alarms", model.GetVariable("ActiveAlarms"), string.Empty, null, model.GetVariable("AlarmText"));
            BindCard(cards.Get("Card3"), "Live power", model.GetVariable("PowerText"), "kW", null, model.GetVariable("ConnectedText"));
            BindCard(cards.Get("Card4"), "Energy today", model.GetVariable("EnergyText"), "kWh", null, model.GetVariable("DayText"));
        }

        var content = screens?.Get("OvensOee/div") as ColumnLayout;
        if (content == null)
        {
            Log.Error("DesignTimeNetLogic1", "UI/Screens/MainWindowAccessible/OvensOee/div not found.");
            return;
        }
        foreach (var name in new[] { "OeeMetrics", "OvenCharts", "AnalyticsInfo", "OvenAnalytics" })
            content.Get(name)?.Delete();
        content.VerticalGap = 16;

        var metrics = InformationModel.MakeObject<RowLayout>("OeeMetrics");
        metrics.HorizontalAlignment = HorizontalAlignment.Stretch;
        metrics.Height = 124;
        metrics.HorizontalGap = 16;
        content.Add(metrics);
        foreach (var metric in new[] { ("Availability", "Availability"), ("Performance", "Performance"), ("Quality", "Quality"), ("Oee", "OEE") })
        {
            var card = InformationModel.MakeObject<Panel>(metric.Item1, Optix_Demo_OvenDemo_Server.ObjectTypes.Card);
            card.Width = 210;
            card.Height = 124;
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            metrics.Add(card);
            BindCard(card, metric.Item2, model.GetVariable(metric.Item1 + "Text"), string.Empty, null, null);
        }

        var charts = InformationModel.MakeObject<WebBrowser>("OvenCharts");
        charts.Height = 300;
        charts.HorizontalAlignment = HorizontalAlignment.Stretch;
        charts.URL = ResourceUri.FromProjectRelativePath("eCharts/oven-analytics.html");
        content.Add(charts);
        charts.Add(InformationModel.MakeObject<NetLogicObject>("AnalyticsChartLogic", FTOptix.NetLogic.ObjectTypes.NetLogic));

        var info = InformationModel.MakeObject<RowLayout>("AnalyticsInfo");
        info.Height = 28;
        info.HorizontalAlignment = HorizontalAlignment.Stretch;
        info.HorizontalGap = 24;
        content.Add(info);
        AddLabel(info, "ReportingDay", 150).TextVariable.SetDynamicLink(model.GetVariable("DayText"));
        AddLabel(info, "Coverage", 240).TextVariable.SetDynamicLink(model.GetVariable("CoverageText"));
        AddLabel(info, "LastUpdated", 150).TextVariable.SetDynamicLink(model.GetVariable("UpdatedText"));

        var grid = InformationModel.MakeObject<DataGrid>("OvenAnalytics");
        grid.Width = 980;
        grid.Height = 320;
        grid.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.VerticalAlignment = VerticalAlignment.Stretch;
        grid.Model = rows.NodeId;
        grid.Query = "SELECT * FROM Model";
        grid.AutoRefreshTime = 1000;
        content.Add(grid);
        AddColumn(grid, "DeviceId", "Oven", 140);
        AddColumn(grid, "State", "State", 80);
        AddColumn(grid, "EnergyText", "Energy (kWh)", 108);
        AddColumn(grid, "CyclesText", "Cycles", 66);
        AddColumn(grid, "GoodText", "Good", 60);
        AddColumn(grid, "RejectedText", "Rejected", 76);
        AddColumn(grid, "RunTimeText", "Run time", 100);
        AddColumn(grid, "AvailabilityText", "Availability", 96);
        AddColumn(grid, "PerformanceText", "Performance", 104);
        AddColumn(grid, "QualityText", "Quality", 78);
        AddColumn(grid, "OeeText", "OEE", 78);
        Log.Info("DesignTimeNetLogic1", "Analytics page built.");
    }

    private const string OvenAlias = "OvenDetailsAlias";

    [ExportMethod]
    public void BuildOvenDetails()
    {
        var content = Project.Current.Get("UI/UITypes/Dialogs/OvenDetails/OvenDetail/div/Body/Content") as ColumnLayout;
        if (content == null)
        {
            Log.Error("DesignTimeNetLogic1", "UI/UITypes/Dialogs/OvenDetails/OvenDetail/div/Body/Content not found.");
            return;
        }
        var choices = FleetAnalyticsCollector.EnsureFolder(Project.Current.Get("Model"), "LocalRecipeChoices");
        foreach (var name in new[] { "StatusSection", "ProductionSection", "CycleSection", "LocalSection" })
            content.Get(name)?.Delete();
        content.VerticalGap = 16;

        var status = AddSection(content, "StatusSection", "Status");
        AddRow(status, "Mode", "Mode", "ModeText");
        AddRow(status, "State", "State", "StateText");
        AddRow(status, "Connection", "Connection", "ConnectionText");
        AddRow(status, "Temperature", "Temperature / setpoint", "TemperatureText");
        AddRow(status, "Phase", "Phase", "PhaseText");
        AddRow(status, "Batch", "Batch", "BatchText");
        AddRow(status, "Recipe", "Applied recipe", "RecipeText");
        AddRow(status, "Alarms", "Alarms", "AlarmText");

        var production = AddSection(content, "ProductionSection", "Production and energy");
        AddRow(production, "Power", "Power", "PowerText");
        AddRow(production, "EnergyToday", "Energy today", "EnergyTodayText");
        AddRow(production, "CyclesToday", "Cycles today", "CyclesTodayText");
        AddRow(production, "Totals", "Totals", "TotalsText");
        AddRow(production, "RunTime", "Run time", "RunTimeText");
        AddRow(production, "Uptime", "Uptime", "UptimeText");
        AddRow(production, "LastUpdate", "Last update", "LastUpdateText");

        var cycle = AddSection(content, "CycleSection", "Cycle");
        AddCycleChart(cycle, "CycleChart", 300);

        var local = AddSection(content, "LocalSection", "Local simulation");
        local.VisibleVariable.SetDynamicLinkToAlias(OvenAlias, new[] { "Details", "IsLocal" }, DynamicLinkMode.Read);
        var recipes = InformationModel.MakeObject<ComboBox>("Recipe");
        recipes.Height = 32;
        recipes.HorizontalAlignment = HorizontalAlignment.Stretch;
        recipes.Model = choices.NodeId;
        var display = InformationModel.MakeVariable<DynamicLink>("DynamicLink", FTOptix.Core.DataTypes.NodePath);
        display.Value = "{Item}/Caption";
        recipes.DisplayValuePathVariable.Add(display);
        local.Add(recipes);
        recipes.Add(InformationModel.MakeObject<NetLogicObject>("LocalRecipeComboLogic", FTOptix.NetLogic.ObjectTypes.NetLogic));
        var recipeOven = InformationModel.MakeVariable("Oven", OpcUa.DataTypes.NodeId);
        recipes.Add(recipeOven);
        recipeOven.SetDynamicLinkToAlias(OvenAlias, AttributeId.NodeId, DynamicLinkMode.Read);
        recipes.SelectedItemVariable.SetDynamicLinkToAlias(OvenAlias, new[] { "LocalSimulation", "SelectedRecipe" }, DynamicLinkMode.ReadWrite);
        var actions = AddRowLayout(local, "Actions");
        foreach (var action in new[] { ("Apply", "ApplyCommand"), ("Start", "StartCommand"), ("Stop", "StopCommand") })
        {
            var button = InformationModel.MakeObject<ToggleButton>(action.Item1);
            button.Text = action.Item1;
            button.Width = 96;
            button.Height = 32;
            actions.Add(button);
            button.ActiveVariable.SetDynamicLinkToAlias(OvenAlias, new[] { "LocalSimulation", action.Item2 }, DynamicLinkMode.ReadWrite);
        }
        var automation = AddRowLayout(local, "Automation");
        AddCheck(automation, "AutoCycle", "Auto cycle");
        AddCheck(automation, "RejectNextCycle", "Reject next cycle");
        var faults = AddRowLayout(local, "Faults");
        AddCheck(faults, "SimulateFault", "Process fault");
        AddCheck(faults, "PauseTelemetry", "Pause telemetry");
        AddRow(local, "Pending", "Pending recipe", "LocalSimulation", "PendingRecipe");
        AddRow(local, "Result", "Last result", "LocalSimulation", "LastResult");
        Log.Info("DesignTimeNetLogic1", "Oven details built.");
    }

    // Re-runnable: makes the OEE chart follow the session theme.
    [ExportMethod]
    public void ThemeAnalyticsChart()
    {
        var charts = Project.Current.Get("UI/Screens/MainWindowAccessible/OvensOee/div/OvenCharts");
        if (charts == null)
        {
            Log.Error("DesignTimeNetLogic1", "UI/Screens/MainWindowAccessible/OvensOee/div/OvenCharts not found.");
            return;
        }
        charts.Get("AnalyticsChartLogic")?.Delete();
        charts.Add(InformationModel.MakeObject<NetLogicObject>("AnalyticsChartLogic", FTOptix.NetLogic.ObjectTypes.NetLogic));
        Log.Info("DesignTimeNetLogic1", "OEE chart now follows the theme.");
    }

    // Re-runnable: rewrites the base colors of the Dark stylesheet (tokens linked to them, e.g. Accent -> Brand400, follow).
    [ExportMethod]
    public void ApplySoftDarkPalette()
    {
        var palette = new System.Collections.Generic.Dictionary<string, uint>
        {
            ["Bg"] = 0xFF2B3646, ["Surface"] = 0xFF354357, ["SurfaceSoft"] = 0xFF425269,
            ["Text"] = 0xFFF1F5F9, ["TextSoft"] = 0xFFC0CDDC,
            ["Line"] = 0xFF5B6E86, ["Line2"] = 0xFF4B5D73,
            ["Brand400"] = 0xFF3F86D6, ["AccentColor"] = 0xFF7FB6F5,
            ["Ok"] = 0xFF3AA76D, ["Warn"] = 0xFFF0C040, ["Danger"] = 0xFFE0605B,
            ["HighColor"] = 0xFF5A2F33, ["HighText"] = 0xFFFFB3AD,
            ["MedColor"] = 0xFF5A4A26, ["MedText"] = 0xFFFFD78A,
            ["InfoColor"] = 0xFF3D4C61, ["InfoText"] = 0xFFC5D0DE,
            ["GoodColor"] = 0xFF25503A, ["GoodText"] = 0xFFA8E6C0,
        };
        var missing = new System.Collections.Generic.List<string>();
        foreach (var token in palette)
        {
            var variable = Project.Current.GetVariable("UI/StyleSheets/Dark/" + token.Key);
            if (variable == null) missing.Add(token.Key);
            else variable.Value = token.Value;
        }
        Log.Info("DesignTimeNetLogic1", $"Dark palette applied ({palette.Count - missing.Count} colors){(missing.Count > 0 ? ", missing: " + string.Join(", ", missing) : string.Empty)}.");
    }

    // Re-runnable: gives the base stylesheet, Light and Dark the same explicit label type scale (Dark used to inherit a different one).
    [ExportMethod]
    public void StandardizeLabelFonts()
    {
        var reference = Project.Current.Get("UI/StyleSheets/Light/LabelStyles/H1");
        var sizeType = reference?.GetVariable("FontSize")?.DataType;
        var weightType = reference?.GetVariable("FontWeight")?.DataType;
        if (sizeType == null || weightType == null)
        {
            Log.Error("DesignTimeNetLogic1", "Light/LabelStyles/H1 must have FontSize and FontWeight to be used as reference.");
            return;
        }
        var scale = new (string Name, float Size, int Weight)[]
        {
            ("Default", 16f, 400), ("H1", 32f, 700), ("H2", 24f, 600), ("H3", 12f, 700), ("H4", 12f, 400), ("H5", 12f, 700),
        };
        var changed = 0;
        foreach (var sheet in new[] { "UI/BaseStyleSheet", "UI/StyleSheets/Light", "UI/StyleSheets/Dark" })
        {
            foreach (var entry in scale)
            {
                var style = Project.Current.Get($"{sheet}/LabelStyles/{entry.Name}");
                if (style == null)
                {
                    Log.Warning("DesignTimeNetLogic1", $"{sheet}/LabelStyles/{entry.Name} not found.");
                    continue;
                }
                SetStyleValue(style, "FontSize", sizeType, entry.Size);
                SetStyleValue(style, "FontWeight", weightType, entry.Weight);
                changed++;
            }
        }
        var darkDefault = Project.Current.Get("UI/StyleSheets/Dark/LabelStyles/Default") as LabelStyle;
        var darkText = Project.Current.GetVariable("UI/StyleSheets/Dark/Text");
        if (darkDefault != null && darkText != null)
            darkDefault.TextColorVariable.SetDynamicLink(darkText);
        Log.Info("DesignTimeNetLogic1", $"Label font scale applied to {changed} styles.");
    }

    private static void SetStyleValue(IUANode style, string name, NodeId dataType, object value)
    {
        var variable = style.GetVariable(name);
        if (variable == null)
        {
            variable = InformationModel.MakeVariable(name, dataType);
            style.Add(variable);
        }
        variable.Value = new UAValue(value);
    }

    // Re-runnable: Acknowledge/Confirm appear only while the alarm still waits for that action, so auto-acknowledged or auto-confirmed alarms never show them.
    [ExportMethod]
    public void BuildAlarmRowActions()
    {
        var content = Project.Current.Get("UI/UITypes/ListViewRows/ActiveAlarmListRow/Content");
        var acknowledge = content?.Get("Acknowledge") as Item;
        var confirm = content?.Get("Confirm") as Item;
        if (acknowledge == null || confirm == null)
        {
            Log.Error("DesignTimeNetLogic1", "ActiveAlarmListRow/Content must contain the Acknowledge and Confirm buttons.");
            return;
        }
        BindVisible(acknowledge, "!{0}", "{RowItem}/AckedState/Id");
        BindVisible(confirm, "{0} && !{1}", "{RowItem}/AckedState/Id", "{RowItem}/ConfirmedState/Id");
        // Alarms have no DeviceId (the message already names the oven) and the leftover SourceNode label printed the input variable name.
        content.Get("OvenName")?.Delete();
        Project.Current.Get("UI/UITypes/ListViewRows/ActiveAlarmListRow/Label1")?.Delete();
        Log.Info("DesignTimeNetLogic1", "Alarm row actions now follow the alarm state.");
    }

    private static void BindVisible(Item item, string expression, params string[] paths)
    {
        var visible = item.VisibleVariable;
        foreach (var child in visible.Children.Where(child => child is Converter || child is DynamicLink).ToList()) child.Delete();
        var evaluator = InformationModel.MakeObject<ExpressionEvaluator>("ExpressionEvaluator1");
        evaluator.Expression = expression;
        for (var i = 0; i < paths.Length; i++)
        {
            var source = InformationModel.MakeVariable("Source" + i, OpcUa.DataTypes.BaseDataType);
            var link = InformationModel.MakeVariable<DynamicLink>("DynamicLink", FTOptix.Core.DataTypes.NodePath);
            link.Value = paths[i];
            source.Add(link);
            evaluator.Refs.AddReference(FTOptix.CoreBase.ReferenceTypes.HasSource, source);
        }
        visible.SetConverter(evaluator);
    }

    // Re-runnable: fills the empty middle of the Fleet Overview with the fleet status and recent activity panel (served by FleetOverviewCollector).
    [ExportMethod]
    public void BuildFleetOverviewPanel()
    {
        var middle = Project.Current.Get("UI/Screens/MainWindowAccessible/OvensOverview/div/MiddleInfo");
        if (middle == null)
        {
            Log.Error("DesignTimeNetLogic1", "UI/Screens/MainWindowAccessible/OvensOverview/div/MiddleInfo not found.");
            return;
        }
        middle.Get("FleetPanel")?.Delete();
        var panel = InformationModel.MakeObject<WebBrowser>("FleetPanel");
        panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.VerticalAlignment = VerticalAlignment.Stretch;
        panel.URL = ResourceUri.FromProjectRelativePath("eCharts/fleet-overview-light-en-US.html");
        middle.Add(panel);
        var logic = InformationModel.MakeObject<NetLogicObject>("AnalyticsChartLogic", FTOptix.NetLogic.ObjectTypes.NetLogic);
        var page = InformationModel.MakeVariable("LocalizedPage", OpcUa.DataTypes.String);
        page.Value = "fleet-overview";
        logic.Add(page);
        panel.Add(logic);
        Log.Info("DesignTimeNetLogic1", "Fleet overview panel built.");
    }

    // Re-runnable: OvenEdgeManagerBehavior writes the alarm input itself; the stock evaluator on Connection raised and cleared the alarm at every start.
    [ExportMethod]
    public void FixConnectionAlarmInput()
    {
        var removed = 0;
        var inputs = Descendants(Project.Current.Get("Model")).OfType<IUAVariable>()
            .Where(variable => variable.BrowseName == "InputValue" && variable.Owner?.BrowseName == "ConnectionError").ToList();
        foreach (var input in inputs)
        {
            foreach (var child in input.Children.Where(child => child is Converter || child is DynamicLink).ToList())
            {
                child.Delete();
                removed++;
            }
        }
        Log.Info("DesignTimeNetLogic1", $"Connection alarm input: {removed} converters removed from {inputs.Count} inputs.");
    }

    // Re-runnable: alarm messages of OvenEdgeManager (type and instances) become plain text; their session-language formatter crashed the runtime when a new oven was accepted.
    [ExportMethod]
    public void FixAlarmMessageConverters()
    {
        var removed = 0;
        var messages = Descendants(Project.Current.Get("Model")).OfType<IUAVariable>()
            .Where(variable => variable.BrowseName == "Message" && (variable.Owner?.BrowseName == "ConnectionError" || variable.Owner?.BrowseName == "ProcessError")).ToList();
        foreach (var message in messages)
        {
            var driven = message.Children.Where(child => child is Converter || child is DynamicLink).ToList();
            foreach (var child in driven)
            {
                child.Delete();
                removed++;
            }
            var manager = message.Owner.Owner;
            if (driven.Count > 0 && manager != null && manager.BrowseName.StartsWith("OvenEdgeManager_", StringComparison.Ordinal))
            {
                var id = manager.BrowseName.Substring("OvenEdgeManager_".Length);
                var text = message.Owner.BrowseName == "ConnectionError" ? $"Oven {id} is disconnected" : $"Oven {id}: process fault";
                message.Value = new UAValue(new LocalizedText(text, "en-US"));
            }
        }
        Log.Info("DesignTimeNetLogic1", $"Alarm messages: {removed} converters removed from {messages.Count} variables.");
    }

    // Re-runnable: Chat menu notification shows the fleet-wide unread messages (it was bound to the pending onboarding requests and lit up on every message), and each oven in the Chat list gets its own unread badge.
    [ExportMethod]
    public void BuildChatNotifications()
    {
        var chat = Project.Current.Get("Model/Chat");
        if (chat == null)
        {
            Log.Error("DesignTimeNetLogic1", "Model/Chat not found.");
            return;
        }
        var unread = FleetAnalyticsCollector.EnsureVariable(chat, "UnreadCount", OpcUa.DataTypes.Int32, 0);
        var available = FleetAnalyticsCollector.EnsureVariable(chat, "UnreadAvailable", OpcUa.DataTypes.Boolean, false);

        var notifications = Descendants(Project.Current.Get("UI")).OfType<IUAObject>().Where(node => node.BrowseName == "ChatNotification").ToList();
        foreach (var notification in notifications)
        {
            var count = Descendants(notification).OfType<Label>().FirstOrDefault(label => label.BrowseName == "AlarmCount");
            if (count != null) Bind(count.TextVariable, unread);
            if (notification is Item item) Bind(item.VisibleVariable, available);
            // The stock handler forced Visible to true on every chat revision.
            foreach (var handler in notification.Children.OfType<FTOptix.CoreBase.EventHandler>().ToList()) handler.Delete();
        }

        var content = Project.Current.Get("UI/UITypes/ListViewRows/Chat/FleetListSelectableRow/Content");
        var badgeType = notifications.FirstOrDefault()?.ObjectType;
        if (content == null || badgeType == null)
        {
            Log.Warning("DesignTimeNetLogic1", $"Row badge not created: Chat list row found = {content != null}, ChatNotification nodes = {notifications.Count}.");
            return;
        }
        if (content.Get("UnreadBadge") == null)
        {
            var badge = InformationModel.MakeObject("UnreadBadge", badgeType.NodeId);
            content.Add(badge);
            var badgeCount = Descendants(badge).OfType<Label>().FirstOrDefault(label => label.BrowseName == "AlarmCount");
            // Same red dot and white digits as the menu notification.
            if (Descendants(badge).OfType<Ellipse>().FirstOrDefault() is Ellipse dot) dot.FillColor = new Color(0xFF, 0xC9, 0x36, 0x3B);
            if (badgeCount != null) badgeCount.TextColor = new Color(0xFF, 0xFF, 0xFF, 0xFF);
            if (badgeCount != null)
            {
                ClearConverters(badgeCount.TextVariable);
                var link = InformationModel.MakeVariable<DynamicLink>("DynamicLink", FTOptix.Core.DataTypes.NodePath);
                link.Value = "{RowItem}/Chat/UnreadCount";
                badgeCount.TextVariable.Add(link);
            }
            if (badge is Item badgeItem) BindVisible(badgeItem, "{0} > 0", "{RowItem}/Chat/UnreadCount");
        }
        Log.Info("DesignTimeNetLogic1", $"Chat notifications bound: {notifications.Count} menu entries.");
    }

    // Re-runnable: removes fixed light fills that ignore the theme and softens the neon status colors.
    [ExportMethod]
    public void FixThemeOverrides()
    {
        var removed = 0;
        var logFill = Project.Current.Get("UI/UITypes/ListViewRows/RuntimeOutputListRow/Background/FillColor");
        if (logFill != null) { logFill.Delete(); removed++; }

        var cardStrip = Descendants(Project.Current.Get("UI/UITypes/OvenObjects"))
            .Where(node => node.BrowseName == "Alarms").Select(node => node.Get("Background/FillColor")).FirstOrDefault(fill => fill != null);
        if (cardStrip != null) { cardStrip.Delete(); removed++; }

        var recolored = 0;
        var inputStyle = Project.Current.Get("UI/StyleSheets/Dark/InputBoxStyles/InputBoxStyle1") as InputBoxStyle;
        var textToken = Project.Current.GetVariable("UI/StyleSheets/Dark/Text");
        if (inputStyle != null && textToken != null)
        {
            inputStyle.TextColorVariable.SetDynamicLink(textToken);
            removed++;
        }
        var statusColors = new System.Collections.Generic.Dictionary<uint, uint> { [0xFF37EB00] = 0xFF35C46A, [0xFFEB001B] = 0xFFE5484D };
        foreach (var variable in Descendants(Project.Current.Get("Converters")).OfType<IUAVariable>())
        {
            var argb = ArgbOf(variable);
            if (argb.HasValue && statusColors.TryGetValue(argb.Value, out var replacement))
            {
                variable.Value = replacement;
                recolored++;
            }
        }
        Log.Info("DesignTimeNetLogic1", $"Theme overrides fixed: {removed} nodes changed, {recolored} status colors softened.");
    }

    // Re-runnable: Dark gets the Light label scale and Text token (it used to inherit another one), and labels with a fixed black text follow the theme.
    [ExportMethod]
    public void FixDarkThemeFonts()
    {
        StandardizeLabelFonts();
        var linked = 0;
        foreach (var label in Descendants(Project.Current.Get("UI")).OfType<Label>().ToList())
        {
            var color = label.TextColorVariable;
            if (color.Children.Any(child => child is DynamicLink || child is Converter) || ArgbOf(color) != 0xFF000000u) continue;
            var link = InformationModel.MakeVariable<DynamicLink>("DynamicLink", FTOptix.Core.DataTypes.NodePath);
            link.Value = "{Session}/StyleSheet/Text";
            color.Add(link);
            linked++;
        }
        Log.Info("DesignTimeNetLogic1", $"Dark fonts fixed: {linked} labels with fixed black text now follow the theme.");
    }

    private static System.Collections.Generic.IEnumerable<IUANode> Descendants(IUANode root)
    {
        if (root == null) yield break;
        var pending = new System.Collections.Generic.Stack<IUANode>(new[] { root });
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;
            foreach (var child in node.Children) pending.Push(child);
        }
    }

    private static uint? ArgbOf(IUAVariable variable)
    {
        if (variable.DataType != FTOptix.Core.DataTypes.Color) return null;
        var value = variable.Value.Value;
        if (value is uint argb) return argb;
        if (value is Color color) return color.ARGB;
        return null;
    }

    [ExportMethod]
    public void BuildSettings()
    {
        var page = Project.Current.Get("UI/Screens/MainWindowAccessible/Settings/div") as ColumnLayout;
        if (page == null)
        {
            Log.Error("DesignTimeNetLogic1", "UI/Screens/MainWindowAccessible/Settings/div not found.");
            return;
        }
        foreach (var child in page.Children.OfType<IUAObject>().ToList())
            if (child.BrowseName == "PageTitle" || child.BrowseName.EndsWith("Section", StringComparison.Ordinal) || child.BrowseName == "SettingsLogic")
                child.Delete();
        page.VerticalGap = 24;
        page.LeftMargin = 32;
        page.RightMargin = 32;
        page.TopMargin = 24;

        var step = "title";
        try
        {
        var title = InformationModel.MakeObject<Label>("PageTitle");
        title.Text = "Settings";
        title.Style = "H2";
        title.Height = 40;
        page.Add(title);

        var languageSection = AddSection(page, "LanguageSection", "Language");
        step = "language";
        var languageRow = AddSettingRow(languageSection, "Language", "Display language");
        var languageBox = InformationModel.MakeObject<ComboBox>("Control", Optix_Demo_OvenDemo_Server.ObjectTypes.LocaleComboBox);
        languageBox.Width = 240;
        languageBox.Height = 32;
        languageBox.SelectedValue = string.Empty;
        languageBox.Add(InformationModel.MakeObject<NetLogicObject>("LocaleComboBoxLogic", FTOptix.NetLogic.ObjectTypes.NetLogic));
        languageRow.Add(languageBox);

        var appearance = AddSection(page, "AppearanceSection", "Appearance");
        step = "appearance and units";
        AddSwitch(AddSettingRow(appearance, "DarkTheme", "Dark theme"));

        var units = AddSection(page, "UnitsSection", "Units");
        AddSwitch(AddSettingRow(units, "Imperial", "Imperial units (°F)"));

        var fleet = AddSection(page, "FleetSection", "Fleet view");
        step = "fleet view";
        var viewType = Project.Current.GetVariable("Model/HMISettings/ViewType");
        var plannedHours = Project.Current.GetVariable("Model/HMISettings/AnalyticsPlannedHours");
        if (viewType == null || plannedHours == null)
        {
            Log.Error("DesignTimeNetLogic1", $"Model/HMISettings variables missing: ViewType={viewType != null}, AnalyticsPlannedHours={plannedHours != null}.");
            return;
        }
        AddSwitch(AddSettingRow(fleet, "ListView", "List view")).CheckedVariable.SetDynamicLink(viewType, DynamicLinkMode.ReadWrite);

        var analytics = AddSection(page, "AnalyticsSection", "Analytics");
        step = "analytics";
        var plannedRow = AddSettingRow(analytics, "PlannedHours", "Planned production hours per day");
        var planned = InformationModel.MakeObject<SpinBox>("Control");
        planned.Width = 120;
        planned.Height = 32;
        planned.MinValue = 1;
        planned.MaxValue = 24;
        plannedRow.Add(planned);
        planned.ValueVariable.SetDynamicLink(plannedHours, DynamicLinkMode.ReadWrite);

        step = "settings logic";
        page.Add(InformationModel.MakeObject<NetLogicObject>("SettingsLogic", FTOptix.NetLogic.ObjectTypes.NetLogic));
        Log.Info("DesignTimeNetLogic1", "Settings built.");
        }
        catch (Exception error)
        {
            Log.Error("DesignTimeNetLogic1", $"BuildSettings failed at step '{step}': {error}");
        }
    }

    // Adds the keys of ProjectFiles/translations.csv that are missing and fills empty cells; existing translations are never overwritten.
    [ExportMethod]
    public void ApplyTranslations()
    {
        var dictionary = Project.Current.Get("Translations/LocalizationDictionary1") as IUAVariable;
        var csv = ResourceUri.FromProjectRelativePath("translations.csv").Uri;
        if (dictionary == null || !System.IO.File.Exists(csv) || !(dictionary.Value.Value is Array current))
        {
            Log.Error("DesignTimeNetLogic1", "Translations dictionary or translations.csv not found.");
            return;
        }

        var columns = current.GetLength(1);
        var rows = new System.Collections.Generic.List<string[]>();
        for (var r = 0; r < current.GetLength(0); r++)
            rows.Add(Enumerable.Range(0, columns).Select(c => Convert.ToString(current.GetValue(r, c)) ?? string.Empty).ToArray());
        var index = rows.Skip(1).ToDictionary(row => row[0], StringComparer.Ordinal);

        var imported = ParseCsv(System.IO.File.ReadAllText(csv, System.Text.Encoding.UTF8));
        var map = imported[0].Select((locale, i) => (locale, i)).Where(pair => pair.i > 0).Select(pair => (pair.i, column: Array.IndexOf(rows[0], pair.locale))).Where(pair => pair.column > 0).ToList();
        int added = 0, filled = 0;
        foreach (var line in imported.Skip(1).Where(line => line.Length > 0 && line[0].Length > 0))
        {
            if (!index.TryGetValue(line[0], out var row))
            {
                row = new string[columns];
                for (var c = 0; c < columns; c++) row[c] = string.Empty;
                row[0] = line[0];
                rows.Add(row);
                index[line[0]] = row;
                added++;
            }
            foreach (var (source, column) in map)
                if (row[column].Length == 0 && source < line.Length && line[source].Length > 0)
                {
                    row[column] = line[source];
                    filled++;
                }
        }

        var result = new string[rows.Count, columns];
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < columns; c++)
                result[r, c] = rows[r][c];
        dictionary.Value = new UAValue(result);
        Log.Info("DesignTimeNetLogic1", $"Translations merged: {added} keys added, {filled} cells filled.");
    }

    private static System.Collections.Generic.List<string[]> ParseCsv(string text)
    {
        var records = new System.Collections.Generic.List<string[]>();
        var fields = new System.Collections.Generic.List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ';') { fields.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(field.ToString());
                field.Clear();
                records.Add(fields.ToArray());
                fields.Clear();
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(fields.ToArray());
        }
        return records;
    }

    // Switches fixed English navigation/view titles to translatable keys (run ApplyTranslations first).
    [ExportMethod]
    public void LocalizeNavigationTexts()
    {
        var titles = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal) { "Alarms", "Fleet Overview", "Loggers", "OEE", "Oven Demo - Supervisor", "Ovens", "Recipes", "Grid", "List" };
        var namespaceIndex = Project.Current.NodeId.NamespaceIndex;
        var changed = 0;
        var pending = new System.Collections.Generic.Stack<IUANode>();
        pending.Push(Project.Current.Get("UI"));
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            foreach (var child in node.Children)
                pending.Push(child);
            if (node is IUAVariable variable && variable.Value.Value is LocalizedText text && titles.Contains(text.Text) && string.IsNullOrEmpty(text.TextId))
            {
                variable.Value = new UAValue(new LocalizedText(namespaceIndex, text.Text));
                changed++;
            }
        }
        Log.Info("DesignTimeNetLogic1", $"Navigation texts localized: {changed}.");
    }

    private static RowLayout AddSettingRow(IUANode section, string name, string caption)
    {
        var row = AddRowLayout(section, name);
        row.HorizontalGap = 16;
        AddLabel(row, "Caption", 280).Text = caption;
        return row;
    }

    private static Switch AddSwitch(IUANode row)
    {
        var toggle = InformationModel.MakeObject<Switch>("Control");
        toggle.Width = 56;
        toggle.Height = 28;
        toggle.VerticalAlignment = VerticalAlignment.Center;
        toggle.Checked = false;
        row.Add(toggle);
        return toggle;
    }

    // Reusable: call it again with another parent (for example an oven card) to get the same chart bound to that parent's oven.
    private static void AddCycleChart(IUANode parent, string name, float height)
    {
        var chart = InformationModel.MakeObject<Panel>(name);
        chart.Height = height;
        chart.HorizontalAlignment = HorizontalAlignment.Stretch;
        parent.Add(chart);
        var browser = InformationModel.MakeObject<WebBrowser>("Chart");
        browser.HorizontalAlignment = HorizontalAlignment.Stretch;
        browser.VerticalAlignment = VerticalAlignment.Stretch;
        chart.Add(browser);
        var oven = InformationModel.MakeVariable("Oven", OpcUa.DataTypes.NodeId);
        chart.Add(oven);
        oven.SetDynamicLinkToAlias(OvenAlias, AttributeId.NodeId, DynamicLinkMode.Read);
        chart.Add(InformationModel.MakeObject<NetLogicObject>("OvenCycleChartLogic", FTOptix.NetLogic.ObjectTypes.NetLogic));
    }

    private static ColumnLayout AddSection(IUANode parent, string name, string title)
    {
        var section = InformationModel.MakeObject<ColumnLayout>(name);
        section.HorizontalAlignment = HorizontalAlignment.Stretch;
        section.VerticalGap = 8;
        parent.Add(section);
        var heading = InformationModel.MakeObject<Label>("Title");
        heading.Text = title;
        heading.Style = "H3";
        heading.Height = 28;
        section.Add(heading);
        return section;
    }

    private static RowLayout AddRowLayout(IUANode parent, string name)
    {
        var row = InformationModel.MakeObject<RowLayout>(name);
        row.Height = 32;
        row.HorizontalGap = 8;
        row.HorizontalAlignment = HorizontalAlignment.Stretch;
        parent.Add(row);
        return row;
    }

    private static void AddRow(IUANode section, string name, string title, params string[] path)
    {
        var row = AddRowLayout(section, name);
        row.Height = 28;
        row.HorizontalGap = 16;
        AddLabel(row, "Caption", 180).Text = title;
        var value = AddLabel(row, "Value", 360);
        value.HorizontalAlignment = HorizontalAlignment.Stretch;
        value.TextVariable.SetDynamicLinkToAlias(OvenAlias, path.Length == 1 ? new[] { "Details", path[0] } : path, DynamicLinkMode.Read);
    }

    private static void AddCheck(IUANode parent, string name, string text)
    {
        var check = InformationModel.MakeObject<CheckBox>(name);
        check.Text = text;
        check.Width = 160;
        check.Height = 28;
        parent.Add(check);
        check.CheckedVariable.SetDynamicLinkToAlias(OvenAlias, new[] { "LocalSimulation", name }, DynamicLinkMode.ReadWrite);
    }

    private static void BindCard(IUANode card, string title, IUAVariable value, string unit, IUAVariable unitVariable, IUAVariable detail)
    {
        if (card == null) return;
        card.DisplayName = new LocalizedText(title, "en-US");
        card.Description = new LocalizedText(title, "en-US");
        var caption = card.GetVariable("Content/CardDescriptionText/Text");
        if (caption != null)
        {
            ClearConverters(caption);
            caption.Value = new UAValue(new LocalizedText(title, "en-US"));
        }
        Bind(card.GetVariable("ValueToDisplay"), value);
        var units = card.GetVariable("ValueAddInfo");
        if (units != null)
        {
            ClearConverters(units);
            if (unitVariable != null) units.SetDynamicLink(unitVariable);
            else units.Value = unit ?? string.Empty;
        }
        var information = card.GetVariable("Content/AdditionalInfo/Text");
        if (information != null)
        {
            ClearConverters(information);
            if (detail != null) information.SetDynamicLink(detail);
            else information.Value = new UAValue(new LocalizedText(string.Empty, "en-US"));
        }
        var stalePointer = card.GetVariable("DataPointer");
        if (stalePointer != null)
        {
            ClearConverters(stalePointer);
            stalePointer.Value = value.NodeId;
        }
    }

    private static void Bind(IUAVariable target, IUAVariable source)
    {
        if (target == null || source == null) return;
        ClearConverters(target);
        target.SetDynamicLink(source);
    }

    private static void ClearConverters(IUAVariable variable)
    {
        foreach (var child in variable.Children.ToList())
            if (child.BrowseName.StartsWith("DynamicLink", StringComparison.Ordinal) || child.BrowseName.StartsWith("StringFormatter", StringComparison.Ordinal) || child.BrowseName.StartsWith("ConditionalConverter", StringComparison.Ordinal))
                child.Delete();
    }

    private static Label AddLabel(IUANode parent, string name, float width)
    {
        var label = InformationModel.MakeObject<Label>(name);
        label.Width = width;
        label.Height = 28;
        label.VerticalAlignment = VerticalAlignment.Center;
        parent.Add(label);
        return label;
    }

    private static void AddColumn(DataGrid grid, string field, string title, float width)
    {
        var column = InformationModel.MakeObject<DataGridColumn>(field);
        column.Title = title;
        column.Width = width;
        var template = InformationModel.MakeObject<DataGridLabelItemTemplate>("DataItemTemplate");
        column.DataItemTemplate = template;
        var link = InformationModel.MakeVariable<DynamicLink>("DynamicLink", FTOptix.Core.DataTypes.NodePath);
        link.Value = "{Item}/" + field;
        template.TextVariable.Add(link);
        grid.Columns.Add(column);
    }
}
