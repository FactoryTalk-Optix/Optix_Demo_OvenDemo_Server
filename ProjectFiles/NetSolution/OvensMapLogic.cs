#region Using directives
using System.IO;
using System.Linq;
using System.Text;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.UI;
using UAManagedCore;
#endregion

public class OvensMapLogic : BaseNetLogic
{
    public override void Start()
    {
        generateMapTask = new LongRunningTask(GenerateAndLoadMap, LogicObject);
        generateMapTask.Start();
    }

    public override void Stop()
    {
        dataRefreshTask?.Dispose();
        dataRefreshTask = null;
        generateMapTask?.Dispose();
        generateMapTask = null;
    }

    [ExportMethod]
    public void GenerateAndLoadMap()
    {
        var ovens = ovensFolder.Children.OfType<OptixOven>().ToList();

        double centerLat = ovens.Count > 0 ? ovens.Average(o => o.Latitude) : 45.0;
        double centerLon = ovens.Count > 0 ? ovens.Average(o => o.Longitude) : 10.0;

        bool isMapDarkMode = LogicObject.GetVariable("IsDark").Value;
        string tileLayerUrl = isMapDarkMode
            ? "https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png"
            : "https://{s}.basemaps.cartocdn.com/rastertiles/voyager/{z}/{x}/{y}{r}.png";

        var markerDefs = new StringBuilder();
        foreach (var oven in ovens)
        {
            string jsId = EscapeJs(oven.OvenId);
            double lat = oven.Latitude;
            double lon = oven.Longitude;

            markerDefs.AppendLine(
                $@"  markers['{jsId}'] = L.marker([{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}], {{icon: ovenIcon}}).addTo(map);");
        }

        string js = $@"var map = L.map('map').setView([{centerLat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {centerLon.ToString(System.Globalization.CultureInfo.InvariantCulture)}], 5);

L.tileLayer('{tileLayerUrl}', {{
  attribution: '\u00a9 <a href=""https://www.openstreetmap.org/copyright"">OpenStreetMap</a> contributors \u00a9 <a href=""https://carto.com/attributions"">CARTO</a>',
  subdomains: 'abcd',
  maxZoom: 19
}}).addTo(map);

var ovenIcon = L.icon({{
  iconUrl: './oven-marker.svg',
  iconSize: [40, 52],
  iconAnchor: [20, 50],
  popupAnchor: [0, -52]
}});

var markers = {{}};
{markerDefs}

function buildPopup(oven) {{
  return '<b>' + oven.name + '</b><br>ID: ' + oven.id +
         '<br>Temperature: ' + oven.temperature + ' \u00b0C' +
         '<br>Cycles: ' + oven.cycles;
}}

function refreshData() {{
  fetch('./ovens-data.json?t=' + Date.now())
    .then(function(r) {{ return r.json(); }})
    .then(function(data) {{
      data.forEach(function(oven) {{
        var m = markers[oven.id];
        if (!m) return;
        var popup = m.getPopup();
        if (popup) {{
          popup.setContent(buildPopup(oven));
        }} else {{
          m.bindPopup(buildPopup(oven));
        }}
      }});
    }})
    .catch(function() {{}});
}}

refreshData();
setInterval(refreshData, 3000);
";

        string html = @"<!DOCTYPE html>
<html>
<head>
  <meta charset=""utf-8"" />
  <title>Ovens Map</title>
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
  <link rel=""stylesheet"" href=""./leaflet.css"" />
  <style>
    html, body { margin: 0; padding: 0; height: 100%; }
    #map { height: 100%; width: 100%; }
  </style>
</head>
<body>
  <div id=""map""></div>
  <script src=""./leaflet.js""></script>
  <script src=""./ovens-map.js""></script>
</body>
</html>";

        string resDir = ResourceUri.FromProjectRelativePath("res").Uri;
        Directory.CreateDirectory(resDir);

        WriteOvensDataJson(ovens, resDir);
        File.WriteAllText(Path.Combine(resDir, "ovens-map.js"), js, Encoding.UTF8);
        string htmlPath = Path.Combine(resDir, "OvensMap.html");
        File.WriteAllText(htmlPath, html, Encoding.UTF8);

        var browser = Owner.Get<WebBrowser>("WebBrowser");
        if (browser != null)
        {
            browser.URLVariable.Value = ResourceUri.FromProjectRelativePath("res/OvensMap.html");
            System.Threading.Thread.Sleep(1000);
            browser.Refresh();
        }

        dataRefreshTask = new PeriodicTask(RefreshOvensData, System.TimeSpan.FromSeconds(3), LogicObject);
        dataRefreshTask.Start();
    }

    private void RefreshOvensData()
    {
        var ovens = ovensFolder.Children.OfType<OptixOven>().ToList();
        string resDir = ResourceUri.FromProjectRelativePath("res").Uri;
        WriteOvensDataJson(ovens, resDir);
        if (ovens.Count != ovensCount)
        {
            var browser = Owner.Get<WebBrowser>("WebBrowser");
            if (browser != null)
            {
                browser.Refresh();
            }
            else
            {
                Log.Warning("WebBrowser control not found for refreshing");
            }
        }
        ovensCount = ovens.Count;
    }

    private static void WriteOvensDataJson(System.Collections.Generic.List<OptixOven> ovens, string resDir)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < ovens.Count; i++)
        {
            var o = ovens[i];
            if (i > 0)
                sb.Append(',');
            sb.Append($@"{{""id"":""{EscapeJson(o.OvenId)}"",""name"":""{EscapeJson(o.OvenName)}"",""temperature"":{o.CurrentTemperature},""cycles"":{o.Cycles}}}");
        }
        sb.Append(']');

        string tmpPath = Path.Combine(resDir, "ovens-data.tmp");
        string finalPath = Path.Combine(resDir, "ovens-data.json");
        File.WriteAllText(tmpPath, sb.ToString(), Encoding.UTF8);
        File.Move(tmpPath, finalPath, overwrite: true);
    }

    private static string EscapeJs(string value) =>
        (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

    private static string EscapeJson(string value) =>
        (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");

    private readonly IUANode ovensFolder = Project.Current.Get("Model/Ovens");
    private LongRunningTask generateMapTask;
    private PeriodicTask dataRefreshTask;
    private int ovensCount = 0;
}
