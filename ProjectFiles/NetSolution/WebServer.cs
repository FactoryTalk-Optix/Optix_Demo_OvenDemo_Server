#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.Core;
#endregion

public class WebServer : BaseNetLogic
{
    private HttpListener _listener;
    private Thread _listenerThread;
    private volatile bool _running;
    private int _activePort;

    public override void Start()
    {
        _running = true;

        // Try to find an available port starting from 8080
        int[] portCandidates = { 9000, 9001, 9002, 9003, 10000, 10001 };
        
        bool started = false;
        foreach (int port in portCandidates)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");

            try
            {
                _listener.Start();
                _activePort = port;
                started = true;
                break;
            }
            catch (HttpListenerException ex)
            {
                Log.Debug("WebServer", $"Port {port} unavailable: {ex.Message}");
                try { _listener.Close(); } catch { }
                _listener = null;
            }
        }

        if (!started)
        {
            Log.Error("WebServer", "Failed to find an available port. All candidates blocked.");
            return;
        }

        _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "PizzaWebServer" };
        _listenerThread.Start();
        Log.Info("WebServer", $"Pizza restaurant web server started at http://localhost:{_activePort}/");
    }

    public override void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listenerThread?.Join(2000);
        _listener?.Close();
        Log.Info("WebServer", "Pizza restaurant web server stopped.");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // HTTP dispatch
    // ──────────────────────────────────────────────────────────────────────────

    private void ListenLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _listener.GetContext();
            }
            catch (HttpListenerException)
            {
                break; // listener was stopped
            }
            catch (Exception ex)
            {
                Log.Warning("WebServer", $"GetContext error: {ex.Message}");
                continue;
            }

            ThreadPool.QueueUserWorkItem(_ => HandleContext(ctx));
        }
    }

    private void HandleContext(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (path == "/api/ovens" && ctx.Request.HttpMethod == "GET")
                ServeOvensJson(ctx);
            else if (path == "/" || path == "/index.html")
                ServeHtml(ctx);
            else
                ServeNotFound(ctx);
        }
        catch (Exception ex)
        {
            Log.Warning("WebServer", $"Request handling error: {ex.Message}");
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // /api/ovens  – read directly from Model/Ovens
    // ──────────────────────────────────────────────────────────────────────────

    private void ServeOvensJson(HttpListenerContext ctx)
    {
        var payload = BuildOvensPayload();
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private List<Dictionary<string, object>> BuildOvensPayload()
    {
        var result = new List<Dictionary<string, object>>();

        var ovensContainer = Project.Current.Get<IUANode>("Model/Ovens");
        if (ovensContainer == null)
        {
            Log.Warning("WebServer", "Model/Ovens not found");
            return result;
        }

        foreach (var child in ovensContainer.Children.OfType<OptixOven>())
        {
            try
            {
                result.Add(new Dictionary<string, object>
                {
                    ["OvenName"]          = child.OvenName,
                    ["OvenId"]            = child.OvenId,
                    ["LastUpdate"]        = child.LastUpdate,
                    ["Cycles"]            = child.Cycles,
                    ["PowerConsumption"]  = child.PowerConsumption,
                    ["GasConsumption"]    = child.GasConsumption,
                    ["CurrentPhase"]      = child.CurrentPhase,
                    ["CurrentTemperature"]= child.CurrentTemperature,
                    ["CurrentRecipe"]     = child.CurrentRecipe,
                    ["Latitude"]          = child.Latitude,
                    ["Longitude"]         = child.Longitude,
                    ["CycleTime"]         = child.CycleTime,
                    ["LastJobStarted"]    = child.LastJobStarted
                });
            }
            catch (Exception ex)
            {
                Log.Warning("WebServer", $"Error reading oven {child.BrowseName}: {ex.Message}");
            }
        }

        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // / – serve the Three.js restaurant page
    // ──────────────────────────────────────────────────────────────────────────

    private void ServeHtml(HttpListenerContext ctx)
    {
        var bytes = Encoding.UTF8.GetBytes(RestaurantHtml);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private void ServeNotFound(HttpListenerContext ctx)
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Embedded Three.js HTML
    // ──────────────────────────────────────────────────────────────────────────


    private const string RestaurantHtml = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8""/>
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0""/>
<title>Pizza Franchise – Oven Monitor</title>
<style>
  * { margin:0; padding:0; box-sizing:border-box; }
  body { background:#0e0500; font-family:'Segoe UI',sans-serif; overflow:hidden; }
  #canvas-container { width:100vw; height:100vh; }
  #ui-overlay {
    position:fixed; top:12px; left:12px; z-index:10;
    background:rgba(0,0,0,.65); border:1px solid #ff6b00;
    border-radius:8px; padding:10px 14px; color:#fff; min-width:260px;
    max-height:92vh; overflow-y:auto;
  }
  #ui-overlay h2 { color:#ff9800; margin-bottom:8px; font-size:1rem; letter-spacing:.05em; }
  .oven-card {
    background:rgba(255,255,255,.07); border-radius:6px;
    padding:6px 10px; margin-bottom:6px; border-left:3px solid #ff6b00;
    font-size:.78rem; line-height:1.6;
  }
  .oven-card .name { font-weight:700; color:#ffcc80; font-size:.88rem; }
  .phase1 { border-color:#4caf50; }
  .phase2 { border-color:#ff9800; }
  .phase3 { border-color:#f44336; }
  #status { position:fixed; bottom:10px; right:14px; font-size:.72rem; color:#aaa; z-index:10; }
</style>
</head>
<body>
<div id=""canvas-container""></div>
<div id=""ui-overlay"">
  <h2>🍕 Pizza Franchise – Ovens</h2>
  <div id=""oven-list"">Loading…</div>
</div>
<div id=""status"">Connecting…</div>

<script type=""importmap"">
{
  ""imports"": {
    ""three"": ""https://cdn.jsdelivr.net/npm/three@0.164.1/build/three.module.js"",
    ""three/addons/"": ""https://cdn.jsdelivr.net/npm/three@0.164.1/examples/jsm/""
  }
}
</script>

<script type=""module"">
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

// ════════════════════════════════════════════════════════════════════════════
// SCENE SETUP
// ════════════════════════════════════════════════════════════════════════════
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setSize(innerWidth, innerHeight);
renderer.setPixelRatio(devicePixelRatio);
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.05;
document.getElementById('canvas-container').appendChild(renderer.domElement);

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x031521);
scene.fog = new THREE.Fog(0x031521, 36, 82);

const camera = new THREE.PerspectiveCamera(55, innerWidth / innerHeight, 0.1, 200);
camera.position.set(0, 18, 14);
camera.lookAt(0, 0, 0);

const controls = new OrbitControls(camera, renderer.domElement);
controls.target.set(0, 0, 0);
controls.maxPolarAngle = Math.PI / 2.2;
controls.minDistance = 8;
controls.maxDistance = 50;

addEventListener('resize', () => {
  camera.aspect = innerWidth / innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(innerWidth, innerHeight);
});

// ════════════════════════════════════════════════════════════════════════════
// LIGHTING
// ════════════════════════════════════════════════════════════════════════════
scene.add(new THREE.HemisphereLight(0xd9f8ff, 0x041018, 0.52));
scene.add(new THREE.AmbientLight(0xffffff, 0.15));

const sun = new THREE.DirectionalLight(0xcff8ff, 0.95);
sun.position.set(10, 18, 8);
sun.castShadow = true;
sun.shadow.mapSize.set(2048, 2048);
Object.assign(sun.shadow.camera, { near:0.5, far:60, left:-25, right:25, top:25, bottom:-25 });
scene.add(sun);

[-8, -3, 3, 8].forEach(x => {
  const pl = new THREE.PointLight(0x22d0ff, 0.65, 14);
  pl.position.set(x, 4.5, -1);
  scene.add(pl);
});

// ════════════════════════════════════════════════════════════════════════════
// PROCEDURAL TEXTURES (canvas-generated; no external assets)
// ════════════════════════════════════════════════════════════════════════════
function makeCanvasTexture(size, draw, repeatX = 1, repeatY = 1) {
  const cnv = document.createElement('canvas');
  cnv.width = size; cnv.height = size;
  const ctx = cnv.getContext('2d');
  draw(ctx, size);
  const tex = new THREE.CanvasTexture(cnv);
  tex.wrapS = THREE.RepeatWrapping;
  tex.wrapT = THREE.RepeatWrapping;
  tex.repeat.set(repeatX, repeatY);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.anisotropy = 8;
  return tex;
}

function makeCanvasTextureWH(width, height, draw, repeatX = 1, repeatY = 1) {
  const cnv = document.createElement('canvas');
  cnv.width = width; cnv.height = height;
  const ctx = cnv.getContext('2d');
  draw(ctx, width, height);
  const tex = new THREE.CanvasTexture(cnv);
  tex.wrapS = THREE.RepeatWrapping;
  tex.wrapT = THREE.RepeatWrapping;
  tex.repeat.set(repeatX, repeatY);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.anisotropy = 8;
  return tex;
}

function makeWoodTexture(baseColor, dark) {
  return makeCanvasTexture(512, (ctx, s) => {
    ctx.fillStyle = baseColor; ctx.fillRect(0, 0, s, s);
    for (let i = 0; i < 60; i++) {
      const y = Math.random() * s;
      const h = 1 + Math.random() * 3;
      ctx.globalAlpha = 0.15 + Math.random() * 0.25;
      ctx.fillStyle = dark;
      ctx.fillRect(0, y, s, h);
    }
    ctx.globalAlpha = 0.4;
    for (let i = 0; i < 6; i++) {
      const x = Math.random() * s, y = Math.random() * s, r = 4 + Math.random() * 10;
      const grad = ctx.createRadialGradient(x, y, 1, x, y, r);
      grad.addColorStop(0, dark);
      grad.addColorStop(1, 'rgba(0,0,0,0)');
      ctx.fillStyle = grad;
      ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.fill();
    }
    ctx.globalAlpha = 1;
  });
}

function makeBrickTexture() {
  return makeCanvasTexture(512, (ctx, s) => {
    ctx.fillStyle = '#0b2a39'; ctx.fillRect(0, 0, s, s);
    const bw = 64, bh = 28;
    for (let row = 0; row * bh < s; row++) {
      const offset = (row % 2) * (bw / 2);
      for (let col = -1; col * bw < s + bw; col++) {
        const x = col * bw + offset;
        const y = row * bh;
        const tint = 80 + Math.floor(Math.random() * 70);
        const cyan = 140 + Math.floor(Math.random() * 70);
        ctx.fillStyle = `rgb(${tint}, ${Math.floor(cyan*0.85)}, ${cyan})`;
        ctx.fillRect(x + 2, y + 2, bw - 4, bh - 4);
        ctx.fillStyle = 'rgba(0,0,0,0.15)';
        for (let k = 0; k < 5; k++) {
          ctx.fillRect(x + 2 + Math.random() * (bw - 4), y + 2 + Math.random() * (bh - 4), 2, 2);
        }
      }
    }
  }, 4, 2);
}

function makeFloorTileTexture() {
  return makeCanvasTexture(512, (ctx, s) => {
    const tile = 64;
    ctx.fillStyle = '#0a2432'; ctx.fillRect(0, 0, s, s);
    for (let y = 0; y < s; y += tile) {
      for (let x = 0; x < s; x += tile) {
        const v = 120 + Math.floor(Math.random() * 35);
        ctx.fillStyle = `rgb(${Math.floor(v*0.75)}, ${Math.floor(v*0.95)}, ${v + 18})`;
        ctx.fillRect(x + 2, y + 2, tile - 4, tile - 4);
        ctx.strokeStyle = 'rgba(0,180,240,0.22)';
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(x + Math.random() * tile, y);
        ctx.bezierCurveTo(
          x + Math.random() * tile, y + Math.random() * tile,
          x + Math.random() * tile, y + Math.random() * tile,
          x + Math.random() * tile, y + tile
        );
        ctx.stroke();
      }
    }
  }, 6, 4);
}

function makePlasterTexture() {
  return makeCanvasTexture(256, (ctx, s) => {
    ctx.fillStyle = '#d8f5ff'; ctx.fillRect(0, 0, s, s);
    for (let i = 0; i < 1500; i++) {
      ctx.fillStyle = `rgba(0,0,0,${Math.random() * 0.06})`;
      ctx.fillRect(Math.random() * s, Math.random() * s, 1, 1);
    }
  }, 3, 3);
}

function makeMarbleTexture() {
  return makeCanvasTexture(512, (ctx, s) => {
    ctx.fillStyle = '#e6fbff'; ctx.fillRect(0, 0, s, s);
    ctx.strokeStyle = 'rgba(70,150,180,0.45)';
    for (let i = 0; i < 30; i++) {
      ctx.lineWidth = 0.5 + Math.random() * 1.5;
      ctx.beginPath();
      ctx.moveTo(0, Math.random() * s);
      ctx.bezierCurveTo(
        s * Math.random(), s * Math.random(),
        s * Math.random(), s * Math.random(),
        s, Math.random() * s
      );
      ctx.stroke();
    }
  }, 2, 2);
}

function makePizzaTopTexture() {
  return makeCanvasTexture(256, (ctx, s) => {
    const grad = ctx.createRadialGradient(s/2, s/2, 30, s/2, s/2, s/2);
    grad.addColorStop(0, '#fff2c4');
    grad.addColorStop(1, '#e2b76f');
    ctx.fillStyle = grad; ctx.fillRect(0, 0, s, s);
    for (let i = 0; i < 35; i++) {
      const x = Math.random() * s, y = Math.random() * s, r = 4 + Math.random() * 10;
      ctx.fillStyle = `rgba(180,${30 + Math.random()*30},20,0.55)`;
      ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI*2); ctx.fill();
    }
    for (let i = 0; i < 14; i++) {
      const x = 30 + Math.random() * (s - 60);
      const y = 30 + Math.random() * (s - 60);
      ctx.fillStyle = '#a02020';
      ctx.beginPath(); ctx.arc(x, y, 12, 0, Math.PI*2); ctx.fill();
      ctx.fillStyle = '#7a1010';
      for (let k = 0; k < 4; k++) {
        ctx.beginPath();
        ctx.arc(x + (Math.random()-0.5)*12, y + (Math.random()-0.5)*12, 1.6, 0, Math.PI*2);
        ctx.fill();
      }
    }
    ctx.fillStyle = '#2e7d32';
    for (let i = 0; i < 12; i++) {
      ctx.beginPath();
      ctx.ellipse(Math.random()*s, Math.random()*s, 4, 2, Math.random()*Math.PI, 0, Math.PI*2);
      ctx.fill();
    }
  });
}

const T = {
  woodTable:   makeWoodTexture('#0f4b61', '#062737'),
  woodChair:   makeWoodTexture('#0a3348', '#031b2b'),
  woodCounter: makeWoodTexture('#0a3f57', '#042636'),
  brick:       makeBrickTexture(),
  floorTile:   makeFloorTileTexture(),
  plaster:     makePlasterTexture(),
  marble:      makeMarbleTexture(),
  pizzaTop:    makePizzaTopTexture()
};

// ════════════════════════════════════════════════════════════════════════════
// MATERIALS
// ════════════════════════════════════════════════════════════════════════════
const M = {
  floor:     new THREE.MeshStandardMaterial({ map: T.floorTile, roughness: 0.7, metalness: 0.05 }),
  tiles:     new THREE.MeshStandardMaterial({ map: T.floorTile, roughness: 0.7, metalness: 0.05 }),
  wall:      new THREE.MeshStandardMaterial({ map: T.plaster, roughness: 0.95 }),
  counter:   new THREE.MeshStandardMaterial({ map: T.woodCounter, roughness: 0.6 }),
  counterTop:new THREE.MeshStandardMaterial({ map: T.marble, roughness: 0.25, metalness: 0.05 }),
  brick:     new THREE.MeshStandardMaterial({ map: T.brick, roughness: 0.9 }),
  table:     new THREE.MeshStandardMaterial({ map: T.woodTable, roughness: 0.55 }),
  chair:     new THREE.MeshStandardMaterial({ map: T.woodChair, roughness: 0.65 }),
  skin:      new THREE.MeshStandardMaterial({ color: 0xffcc99, roughness: 0.85 }),
  apron:     new THREE.MeshStandardMaterial({ color: 0x00aeef, roughness: 0.55, metalness: 0.05 }),
  pizza:     new THREE.MeshStandardMaterial({ map: T.pizzaTop, roughness: 0.85 }),
  doorDark:  new THREE.MeshStandardMaterial({ color: 0x041118, roughness: 0.35, metalness: 0.65 })
};

// ════════════════════════════════════════════════════════════════════════════
// HELPERS
// ════════════════════════════════════════════════════════════════════════════
function box(w,h,d,m) {
  const o = new THREE.Mesh(new THREE.BoxGeometry(w,h,d), m);
  o.castShadow = true; o.receiveShadow = true; return o;
}
function cyl(rt,rb,h,seg,m) {
  const o = new THREE.Mesh(new THREE.CylinderGeometry(rt,rb,h,seg), m);
  o.castShadow = true; o.receiveShadow = true; return o;
}
function sph(r,m) {
  const o = new THREE.Mesh(new THREE.SphereGeometry(r,12,12), m);
  o.castShadow = true; return o;
}
function place(o,x,y,z) { o.position.set(x,y,z); return o; }

// ════════════════════════════════════════════════════════════════════════════
// RESTAURANT SHELL (no roof, no front wall - top-down friendly)
// ════════════════════════════════════════════════════════════════════════════
const ROOM_W = 28, ROOM_D = 20, ROOM_H = 5;

const floor = box(ROOM_W, 0.2, ROOM_D, M.tiles);
place(floor, 0, -0.1, 0);
scene.add(floor);

// Back wall
const backWall = box(ROOM_W, ROOM_H, 0.2, M.wall);
place(backWall, 0, ROOM_H/2, -ROOM_D/2);
scene.add(backWall);

// Left & right walls
[-ROOM_W/2, ROOM_W/2].forEach(x => {
  const w = box(0.2, ROOM_H, ROOM_D, M.wall);
  place(w, x, ROOM_H/2, 0);
  scene.add(w);
});

// Counter (separating ovens area from dining)
const COUNTER_Z = -4;
const counter = box(ROOM_W - 2, 1.0, 1.0, M.counter);
place(counter, 0, 0.5, COUNTER_Z);
scene.add(counter);
const counterTop = box(ROOM_W - 2, 0.06, 1.2, M.counterTop);
place(counterTop, 0, 1.04, COUNTER_Z);
scene.add(counterTop);

// Back-of-ovens feature wall + FTOptix logo panel
const backFeature = box(ROOM_W - 3.2, 2.2, 0.18, new THREE.MeshStandardMaterial({
  color: 0x052437,
  roughness: 0.65,
  metalness: 0.15,
  emissive: 0x03111a,
  emissiveIntensity: 0.35
}));
place(backFeature, 0, 2.85, -ROOM_D/2 + 0.14);
scene.add(backFeature);

function makeOptixLogoTexture() {
  return makeCanvasTextureWH(1400, 550, (ctx, w, h) => {
    ctx.fillStyle = '#041a28';
    ctx.fillRect(0, 0, w, h);

    // FT bug from the official SVG path geometry
    const bugMain = new Path2D('m 194.93,64.61 a 1.78,1.78 0 0 1 -1.87,1.88 h -17.44 a 0.65,0.65 0 0 0 -0.73,0.73 v 55.47 a 1.85,1.85 0 0 1 -0.52,1.36 1.87,1.87 0 0 1 -1.36,0.52 h -13.57 a 1.87,1.87 0 0 1 -1.36,-0.52 1.85,1.85 0 0 1 -0.52,-1.36 V 67.22 a 0.64,0.64 0 0 0 -0.73,-0.73 l -43.64,-0.1 a 0.64,0.64 0 0 0 -0.73,0.73 v 12.4 a 0.64,0.64 0 0 0 0.73,0.73 h 20.15 a 1.8,1.8 0 0 1 1.88,1.88 v 11 A 1.78,1.78 0 0 1 133.34,95 h -20.15 a 0.64,0.64 0 0 0 -0.73,0.73 v 27 a 1.78,1.78 0 0 1 -1.88,1.88 H 97 a 1.87,1.87 0 0 1 -1.36,-0.52 1.85,1.85 0 0 1 -0.52,-1.36 V 53.46 A 1.83,1.83 0 0 1 95.64,52.1 1.87,1.87 0 0 1 97,51.58 h 96.05 a 1.78,1.78 0 0 1 1.87,1.88 z M 218.4,45.61 181,8.27 A 21.14,21.14 0 0 0 166.06,2.1 H 114.83 A 21.12,21.12 0 0 0 99.91,8.27 L 62.49,45.64 a 17,17 0 0 0 -5,12 v 52.76 a 21.26,21.26 0 0 0 6.24,15 l 36.18,36.15 a 21.07,21.07 0 0 0 14.88,6.15 h 26.64 l 0.08,30 a 1.27,1.27 0 0 0 2.17,0.9 l 74.69,-74.4 a 17,17 0 0 0 5,-12 V 57.67 a 17,17 0 0 0 -5,-12');
    const bugShadow = new Path2D('M 53.67,128.36 A 23,23 0 0 1 46.94,112.12 V 55.19 a 18.4,18.4 0 0 1 5.38,-13 L 92.47,2.1 H 88.59 A 21.3,21.3 0 0 0 73.53,8.33 L 35.77,46.05 a 17.18,17.18 0 0 0 -5,12.14 v 53.24 A 21.44,21.44 0 0 0 37,126.61 l 36.52,36.48 a 21.23,21.23 0 0 0 15,6.22 H 95 a 23,23 0 0 1 -2.25,-1.94 z');

    ctx.save();
    // Use the original logo viewBox proportions for the bug, with the same translate from the SVG groups.
    const vbW = 500.3948, vbH = 196.87396;
    const bugW = h * 1.95;
    const bugH = h * 0.90;
    const bugX = w * 0.06;
    const bugY = h * 0.08;
    ctx.translate(bugX, bugY);
    ctx.scale(bugW / vbW, bugH / vbH);
    ctx.translate(-30.769888, -2.0999957);

    ctx.fillStyle = '#2aace2';
    ctx.fill(bugShadow);
    ctx.fillStyle = '#00aeef';
    ctx.fill(bugMain);
    ctx.restore();

    // Optix wordmark (clean, no neon/glow)
    ctx.fillStyle = '#00aeef';
    ctx.font = `700 ${Math.floor(h * 0.42)}px Barlow, Arial, sans-serif`;
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    ctx.fillText('Optix', w * 0.46, h * 0.46);

  }, 1, 1);
}

const optixPanelTex = makeOptixLogoTexture();
const optixPanel = box(8.4, 3.2, 0.07, new THREE.MeshStandardMaterial({
  map: optixPanelTex,
  roughness: 0.28,
  metalness: 0.08,
  emissive: 0x000000,
  emissiveIntensity: 0
}));
place(optixPanel, 0, 2.9, -ROOM_D/2 + 0.24);
scene.add(optixPanel);

// Two visible warm spotlights mounted near the top logo corners
const logoSpotMountMat = new THREE.MeshStandardMaterial({ color: 0x2a3038, roughness: 0.45, metalness: 0.55 });
const logoSpotTarget = new THREE.Vector3(0, 2.9, -ROOM_D / 2 + 0.25);
[-1, 1].forEach(side => {
  const x = side * 4.45;
  const y = 4.62;
  const z = -ROOM_D / 2 + 0.34;

  // Mount bracket that sticks out slightly from the wall/frame
  const mount = box(0.14, 0.14, 0.08, logoSpotMountMat);
  place(mount, x, y, z - 0.06);
  scene.add(mount);

  // Spotlight can body
  const can = cyl(0.07, 0.09, 0.24, 12, logoSpotMountMat);
  can.rotation.x = Math.PI / 2;
  place(can, x, y - 0.02, z + 0.05);
  scene.add(can);

  // Warm focused beam toward the logo center
  const sp = new THREE.SpotLight(0xffc58a, 5.2, 22, Math.PI / 7, 0.35, 1.05);
  sp.position.set(x, y - 0.02, z + 0.10);
  sp.target.position.copy(logoSpotTarget);
  sp.castShadow = true;
  scene.add(sp.target);
  scene.add(sp);

  // Visible volumetric beam cone (subtle)
  const from = sp.position.clone();
  const dir = logoSpotTarget.clone().sub(from);
  const len = dir.length();
  const beam = new THREE.Mesh(
    new THREE.CylinderGeometry(0.28, 0.018, len, 14, 1, true),
    new THREE.MeshBasicMaterial({
      color: 0xffc58a,
      transparent: true,
      opacity: 0.14,
      blending: THREE.AdditiveBlending,
      depthWrite: false,
      side: THREE.DoubleSide
    })
  );
  beam.position.copy(from).addScaledVector(dir, 0.5);
  beam.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.clone().normalize());
  scene.add(beam);
});

// Pendant lamps over the counter
const lampShadeMat = new THREE.MeshStandardMaterial({ color: 0x8b3a1a, roughness: 0.5, metalness: 0.4 });
const lampInsideMat = new THREE.MeshBasicMaterial({ color: 0xffd28a });
const cordMat = new THREE.MeshStandardMaterial({ color: 0x111111, roughness: 0.9 });
const pendantLights = [];
[-8, -3, 3, 8].forEach(x => {
  const cord = cyl(0.012, 0.012, 1.6, 6, cordMat);
  place(cord, x, ROOM_H - 0.8, COUNTER_Z);
  scene.add(cord);
  const shade = new THREE.Mesh(
    new THREE.ConeGeometry(0.32, 0.45, 16, 1, true),
    lampShadeMat
  );
  shade.castShadow = true;
  place(shade, x, ROOM_H - 1.6, COUNTER_Z);
  scene.add(shade);
  const bulb = sph(0.10, lampInsideMat);
  place(bulb, x, ROOM_H - 1.65, COUNTER_Z);
  scene.add(bulb);

  // Real light cast from each pendant lamp (bulb glow + cone downlight)
  const bulbLight = new THREE.PointLight(0xffc27a, 1.1, 7, 2);
  bulbLight.position.set(x, ROOM_H - 1.66, COUNTER_Z);
  scene.add(bulbLight);

  const downLight = new THREE.SpotLight(0xffd39b, 2.0, 13, Math.PI / 5, 0.45, 1.5);
  downLight.position.set(x, ROOM_H - 1.45, COUNTER_Z);
  downLight.target.position.set(x, 0.9, COUNTER_Z + 0.1);
  scene.add(downLight.target);
  scene.add(downLight);

  pendantLights.push({ bulbLight, downLight, seed: Math.random() * Math.PI * 2 });
});

// Potted plants in the dining area corners
function buildPlant(x, z) {
  const g = new THREE.Group();
  const potMat = new THREE.MeshStandardMaterial({ color: 0x6d4c41, roughness: 0.7 });
  const leafMat = new THREE.MeshStandardMaterial({ color: 0x2e7d32, roughness: 0.8 });
  const pot = cyl(0.32, 0.24, 0.5, 16, potMat);
  place(pot, 0, 0.25, 0); g.add(pot);
  const trunk = cyl(0.04, 0.05, 0.5, 6, new THREE.MeshStandardMaterial({ color: 0x4e342e, roughness: 0.9 }));
  place(trunk, 0, 0.75, 0); g.add(trunk);
  for (let i = 0; i < 12; i++) {
    const leaf = sph(0.18 + Math.random() * 0.12, leafMat);
    leaf.scale.set(1, 0.6, 1);
    place(leaf,
      (Math.random() - 0.5) * 0.5,
      1.0 + Math.random() * 0.5,
      (Math.random() - 0.5) * 0.5);
    g.add(leaf);
  }
  g.position.set(x, 0, z);
  scene.add(g);
}
buildPlant(-ROOM_W/2 + 1.0, ROOM_D/2 - 1.5);
buildPlant( ROOM_W/2 - 1.0, ROOM_D/2 - 1.5);
buildPlant(-ROOM_W/2 + 1.0, 6);
buildPlant( ROOM_W/2 - 1.0, 6);

// ════════════════════════════════════════════════════════════════════════════
// PERSON BUILDER (consistent child indices)
// children: [0]torso [1]head [2]lleg [3]rleg [4]larm [5]rarm
// ════════════════════════════════════════════════════════════════════════════
function buildPerson(shirtColor) {
  const g = new THREE.Group();
  const shirt = new THREE.MeshStandardMaterial({ color: shirtColor, roughness: 0.85 });
  const torso = box(0.36, 0.5, 0.22, shirt);     place(torso,  0,    0.85, 0); g.add(torso);
  const head  = sph(0.14, M.skin);               place(head,   0,    1.25, 0); g.add(head);
  const lleg  = box(0.14, 0.5, 0.14, shirt);     place(lleg,  -0.1,  0.35, 0); g.add(lleg);
  const rleg  = box(0.14, 0.5, 0.14, shirt);     place(rleg,   0.1,  0.35, 0); g.add(rleg);
  const larm  = box(0.1,  0.4, 0.1,  M.skin);    place(larm,  -0.24, 0.85, 0); g.add(larm);
  const rarm  = box(0.1,  0.4, 0.1,  M.skin);    place(rarm,   0.24, 0.85, 0); g.add(rarm);
  return g;
}

// ════════════════════════════════════════════════════════════════════════════
// DINING TABLES & SEATS
// ════════════════════════════════════════════════════════════════════════════
const TABLE_POSITIONS = [
  { x: -8, z: 4 }, { x: -3, z: 4 }, { x: 3, z: 4 }, { x: 8, z: 4 },
  { x: -8, z: 8 }, { x: -3, z: 8 }, { x: 3, z: 8 }, { x: 8, z: 8 }
];

const tables = TABLE_POSITIONS.map(pos => {
  const tbl = cyl(0.7, 0.7, 0.08, 16, M.table);
  place(tbl, pos.x, 0.78, pos.z);
  scene.add(tbl);
  const leg = cyl(0.06, 0.06, 0.78, 8, M.table);
  place(leg, pos.x, 0.39, pos.z);
  scene.add(leg);

  // Two place settings per table: one for each chair, facing the seated customer
  const plateMat = new THREE.MeshStandardMaterial({ color: 0xfafafa, roughness: 0.25, metalness: 0.0 });
  const cutleryMat = new THREE.MeshStandardMaterial({ color: 0xd0d6da, roughness: 0.25, metalness: 0.85 });
  const glassMat = new THREE.MeshPhysicalMaterial({
    color: 0xffffff, roughness: 0.05, metalness: 0.0,
    transmission: 0.85, thickness: 0.2, transparent: true, opacity: 0.6
  });
  const wineMat = new THREE.MeshStandardMaterial({ color: 0x7a1f1f, roughness: 0.3, transparent: true, opacity: 0.85 });
  const plates = [];
  [-0.24, 0.24].forEach(dz => {
    const plate = cyl(0.30, 0.30, 0.02, 28, plateMat);
    place(plate, pos.x, 0.84, pos.z + dz);
    scene.add(plate);
    // Plate rim
    const rim = new THREE.Mesh(
      new THREE.TorusGeometry(0.30, 0.012, 6, 24),
      plateMat
    );
    rim.castShadow = true;
    rim.rotation.x = Math.PI/2;
    place(rim, pos.x, 0.853, pos.z + dz);
    scene.add(rim);
    plates.push({ x: pos.x, y: 0.855, z: pos.z + dz });

    const fork = box(0.02, 0.01, 0.24, cutleryMat);
    place(fork, pos.x - 0.24, 0.845, pos.z + dz);
    scene.add(fork);

    const knife = box(0.02, 0.01, 0.24, cutleryMat);
    place(knife, pos.x + 0.24, 0.845, pos.z + dz);
    scene.add(knife);

    // Wine glass
    const glass = cyl(0.055, 0.04, 0.13, 12, glassMat);
    place(glass, pos.x + 0.40, 0.91, pos.z + dz);
    scene.add(glass);
    const wine = cyl(0.048, 0.038, 0.07, 12, wineMat);
    place(wine, pos.x + 0.40, 0.89, pos.z + dz);
    scene.add(wine);
    const stem = cyl(0.008, 0.008, 0.10, 8, glassMat);
    place(stem, pos.x + 0.40, 0.80, pos.z + dz);
    scene.add(stem);
    const base = cyl(0.06, 0.06, 0.01, 12, glassMat);
    place(base, pos.x + 0.40, 0.755, pos.z + dz);
    scene.add(base);
  });

  // 2 chairs per table (front and back relative to table)
  const chairs = [];
  [-1.0, 1.0].forEach(dz => {
    const seatY = 0.45;
    const seat = box(0.5, 0.06, 0.5, M.chair);
    place(seat, pos.x, seatY, pos.z + dz);
    scene.add(seat);
    const back = box(0.5, 0.55, 0.06, M.chair);
    place(back, pos.x, 0.72, pos.z + dz + (dz < 0 ? -0.22 : 0.22));
    scene.add(back);
    // 4 legs per chair
    const legH = seatY - 0.03;
    [[-0.22, -0.22], [0.22, -0.22], [-0.22, 0.22], [0.22, 0.22]].forEach(([lx, lz]) => {
      const leg = box(0.05, legH, 0.05, M.chair);
      place(leg, pos.x + lx, legH / 2, pos.z + dz + lz);
      scene.add(leg);
    });
    chairs.push({ x: pos.x, z: pos.z + dz, dz, tableX: pos.x, tableZ: pos.z, occupied: false });
  });
  return { pos, chairs, plates };
});

// ════════════════════════════════════════════════════════════════════════════
// DOOR FRAMES (entrance + exit, visible against open front side)
// ════════════════════════════════════════════════════════════════════════════
function buildDoor(x, z, isEntrance) {
  const g = new THREE.Group();
  const frameMat = new THREE.MeshStandardMaterial({ map: T.woodChair, roughness: 0.55 });
  const signMat  = new THREE.MeshStandardMaterial({ color: isEntrance ? 0x2e7d32 : 0xc62828, roughness: 0.6, emissive: isEntrance ? 0x0a2010 : 0x200808, emissiveIntensity: 0.4 });

  // Side posts
  [-0.85, 0.85].forEach(dx => {
    const post = box(0.18, 2.6, 0.18, frameMat);
    place(post, dx, 1.3, 0);
    g.add(post);
  });
  // Top lintel
  const lintel = box(2.0, 0.25, 0.25, frameMat);
  place(lintel, 0, 2.55, 0);
  g.add(lintel);
  // Sign above the door
  const sign = box(1.4, 0.45, 0.06, signMat);
  place(sign, 0, 3.0, 0);
  g.add(sign);

  // Sign text
  const cnv = document.createElement('canvas');
  cnv.width = 256; cnv.height = 80;
  const cx = cnv.getContext('2d');
  cx.fillStyle = isEntrance ? '#2e7d32' : '#c62828'; cx.fillRect(0, 0, 256, 80);
  cx.fillStyle = '#fff'; cx.font = 'bold 42px sans-serif'; cx.textAlign = 'center';
  cx.fillText(isEntrance ? 'ENTRANCE' : 'EXIT', 128, 55);
  const tex = new THREE.CanvasTexture(cnv);
  const lbl = new THREE.Sprite(new THREE.SpriteMaterial({ map: tex, transparent: true }));
  lbl.scale.set(2.0, 0.6, 1);
  place(lbl, 0, 3.7, 0);
  g.add(lbl);

  // Floor mat indicator
  const mat = box(1.6, 0.05, 1.0, signMat);
  place(mat, 0, 0.025, isEntrance ? -0.6 : 0.6);
  g.add(mat);

  g.position.set(x, 0, z);
  scene.add(g);
  return g;
}
buildDoor(-12, ROOM_D/2 - 0.2, true);   // ENTRANCE on the left
buildDoor( 12, ROOM_D/2 - 0.2, false);  // EXIT on the right

// ════════════════════════════════════════════════════════════════════════════
// OVENS (dynamic, behind counter)
// ════════════════════════════════════════════════════════════════════════════
const OVEN_Z = -ROOM_D/2 + 1.6;
const PHASE_COLORS = [0x4caf50, 0xff9800, 0xf44336];
const FLAME_COLORS = [0xffbf69, 0xff8c2a, 0xff4d1f];
const ovenMeshes = new Map();
const ovenGlows = [];

function createOvenMesh(oven) {
  const g = new THREE.Group();

  const ovenShellMat = new THREE.MeshStandardMaterial({
    color: 0x0d3f57,
    roughness: 0.55,
    metalness: 0.25,
    emissive: 0x04141e,
    emissiveIntensity: 0.35
  });
  const body = box(1.4, 0.9, 1.4, ovenShellMat);  place(body, 0, 0.45, 0); g.add(body);
  const dome = new THREE.Mesh(
    new THREE.SphereGeometry(0.75, 12, 6, 0, Math.PI*2, 0, Math.PI/2),
    ovenShellMat); dome.castShadow = true;
  place(dome, 0, 0.9, 0); g.add(dome);
  const door = box(0.55, 0.4, 0.05, M.doorDark);  place(door, 0, 0.35, 0.74); g.add(door);

  // Volumetric flame: two cones + ember disk in the oven mouth.
  const flameOuterMat = new THREE.MeshStandardMaterial({
    color: FLAME_COLORS[1],
    emissive: FLAME_COLORS[1],
    emissiveIntensity: 0.9,
    roughness: 0.35,
    metalness: 0.0,
    transparent: true,
    opacity: 0.88
  });
  const flameInnerMat = new THREE.MeshStandardMaterial({
    color: 0xfff0a8,
    emissive: 0xffb84d,
    emissiveIntensity: 1.1,
    roughness: 0.25,
    metalness: 0.0,
    transparent: true,
    opacity: 0.85
  });
  const flameOuter = new THREE.Mesh(new THREE.ConeGeometry(0.12, 0.28, 10), flameOuterMat);
  flameOuter.castShadow = false;
  place(flameOuter, 0, 0.53, 0.58); g.add(flameOuter);
  const flameInner = new THREE.Mesh(new THREE.ConeGeometry(0.06, 0.18, 10), flameInnerMat);
  flameInner.castShadow = false;
  place(flameInner, 0, 0.50, 0.59); g.add(flameInner);

  // Warm light glowing from inside the oven mouth
  const ovenLight = new THREE.PointLight(0xff7a20, 1.6, 3.5, 2);
  ovenLight.position.set(0, 0.45, 0.6);
  g.add(ovenLight);
  // Glowing ember inside the door opening
  const emberMat = new THREE.MeshBasicMaterial({ color: 0xff7a1f, transparent: true, opacity: 0.72 });
  const ember = new THREE.Mesh(
    new THREE.CircleGeometry(0.18, 24),
    emberMat
  );
  ember.scale.set(1, 0.72, 1);
  place(ember, 0, 0.35, 0.78); g.add(ember);

  // Name label sprite
  const cnv = document.createElement('canvas');
  cnv.width = 256; cnv.height = 64;
  const cx = cnv.getContext('2d');
  cx.fillStyle = 'rgba(0,0,0,0.65)'; cx.fillRect(0, 0, 256, 64);
  cx.fillStyle = '#ffcc80'; cx.font = 'bold 28px sans-serif'; cx.textAlign = 'center';
  cx.fillText(oven.OvenName, 128, 42);
  const tex = new THREE.CanvasTexture(cnv);
  const lbl = new THREE.Sprite(new THREE.SpriteMaterial({ map: tex, transparent: true }));
  lbl.scale.set(1.6, 0.4, 1);
  place(lbl, 0, 1.85, 0); g.add(lbl);

  // Temp sprite
  const tcnv = document.createElement('canvas'); tcnv.width = 256; tcnv.height = 48;
  const tcx = tcnv.getContext('2d');
  const ttex = new THREE.CanvasTexture(tcnv);
  const tspr = new THREE.Sprite(new THREE.SpriteMaterial({ map: ttex, transparent: true }));
  tspr.scale.set(1.4, 0.3, 1);
  place(tspr, 0, 1.45, 0); g.add(tspr);

  const entry = {
    group: g,
    flameOuter,
    flameInner,
    flameOuterMat,
    flameInnerMat,
    emberMat,
    ovenLight,
    phase: 1,
    flickerSeed: Math.random() * 100,
    tcnv,
    tcx,
    ttex,
    name: oven.OvenName
  };
  scene.add(g);
  return entry;
}

function layoutOvens() {
  const ids = [...ovenMeshes.keys()];
  const n = ids.length;
  if (n === 0) return;
  const spacing = Math.min(2.8, (ROOM_W - 4) / n);
  const startX = -((n - 1) * spacing) / 2;
  ids.forEach((id, i) => {
    ovenMeshes.get(id).group.position.set(startX + i * spacing, 0, OVEN_Z);
  });
}

function updateOvenSprite(entry, oven) {
  const phase = Math.max(1, Math.min(3, oven.CurrentPhase || 1));
  entry.phase = phase;
  const flameHex = FLAME_COLORS[phase - 1];
  entry.flameOuterMat.color.setHex(flameHex);
  entry.flameOuterMat.emissive.setHex(flameHex);
  entry.flameInnerMat.emissive.setHex(flameHex);
  entry.ovenLight.color.setHex(flameHex);
  const { tcnv, tcx, ttex } = entry;
  tcx.clearRect(0, 0, 256, 48);
  tcx.fillStyle = 'rgba(0,0,0,0.6)'; tcx.fillRect(0, 0, 256, 48);
  tcx.fillStyle = '#fff'; tcx.font = '20px sans-serif'; tcx.textAlign = 'center';
  tcx.fillText(`${oven.CurrentTemperature}°C  •  Phase ${phase}`, 128, 31);
  ttex.needsUpdate = true;
}

// ════════════════════════════════════════════════════════════════════════════
// EMPLOYEE & CUSTOMERS – STATE MACHINES
// ════════════════════════════════════════════════════════════════════════════
const COUNTER_FRONT_Z = COUNTER_Z + 1.2;   // customer-side of counter
const COUNTER_BACK_Z  = COUNTER_Z - 1.2;   // employee side
const EMPLOYEE_HOME = new THREE.Vector3(0, 0, COUNTER_BACK_Z);
const ENTRANCE = new THREE.Vector3(-12, 0, ROOM_D/2 - 1);
const EXIT     = new THREE.Vector3( 12, 0, ROOM_D/2 - 1);

// Employee
const employee = buildPerson(0xffd54f);
employee.position.copy(EMPLOYEE_HOME);
scene.add(employee);

const employeeState = {
  state: 'idle',          // 'idle' | 'going_to_oven' | 'going_to_deliver' | 'returning'
  target: EMPLOYEE_HOME.clone(),
  pizza: null,
  servingCustomer: null,
  speed: 5.0
};

// Customers
const customers = [];
const queue = []; // ordered list of customer refs that are queueing/at counter

function pickFreeChair() {
  // Find tables that still have at least one free chair, then pick one at random.
  // Each table has only 2 chairs by construction, so max 2 customers per table.
  const candidates = [];
  for (const t of tables) {
    const freeChairs = t.chairs.filter(ch => !ch.occupied);
    if (freeChairs.length > 0) candidates.push({ table: t, freeChairs });
  }
  if (candidates.length === 0) return null;
  const pick = candidates[Math.floor(Math.random() * candidates.length)];
  const chair = pick.freeChairs[Math.floor(Math.random() * pick.freeChairs.length)];
  return { table: pick.table, chair };
}

function spawnCustomer() {
  const colors = [0xe53935, 0x1565c0, 0x2e7d32, 0x6a1b9a, 0x00838f, 0xef6c00];
  const c = {
    mesh: buildPerson(colors[Math.floor(Math.random() * colors.length)]),
    state: 'walking_to_queue',  // 'walking_to_queue' | 'queuing' | 'waiting_pizza' | 'walking_to_table' | 'eating' | 'leaving'
    target: new THREE.Vector3(),
    waypoints: [],
    pizza: null,
    tablePizza: null,
    table: null,
    eatStart: 0,
    speed: 4.0 + Math.random() * 1.5
  };
  c.mesh.position.copy(ENTRANCE);
  c.mesh.position.x += (Math.random() - 0.5) * 1;
  scene.add(c.mesh);
  // Plan path from entrance to queue, avoiding tables
  c.waypoints = pathFromEntranceToQueue(c.mesh.position);
  // NOTE: do NOT consume any waypoint here; followWaypoints() will handle them.
  queue.push(c);
  customers.push(c);
  console.log('[spawn] customer at', c.mesh.position.toArray(), 'queue size', queue.length);
}

function queueSlotPosition(idx) {
  // line forms in front of counter at x=0, extending to the right
  return new THREE.Vector3(idx * 0.85, 0, COUNTER_FRONT_Z + 0.6);
}

// ════════════════════════════════════════════════════════════════════════════
// PATH PLANNING (avoid walking through tables)
// Tables are at z∈{4,8}, x∈{-8,-3,3,8}. Free aisles:
//   Z lanes: -2 (queue area), 2 (between counter and row1), 6 (between rows),
//            ROOM_D/2 - 0.4 (front lane near doors)
//   X aisles between table columns: -10.5, -5.5, 0, 5.5, 10.5
// ════════════════════════════════════════════════════════════════════════════
const FRONT_LANE_Z = ROOM_D/2 - 0.4;   // ~9.6, near the open front of the room
const SIDE_AISLE_X = ROOM_W/2 - 3.5;   // 10.5

// Approach lane Z for a chair: outside its table row
function laneZForChair(chair) {
  // chair.dz = -1 (counter side) or +1 (front side)
  // table row z=4 -> lanes 2 / 6;  table row z=8 -> lanes 6 / FRONT_LANE_Z
  const candidate = chair.tableZ + chair.dz * 2.0;
  return Math.min(candidate, FRONT_LANE_Z);
}

function followWaypoints(c, dt) {
  // Returns true while still following waypoints. Consumes a WP on arrival.
  if (!c.waypoints || c.waypoints.length === 0) return false;
  const wp = c.waypoints[0];
  const dest = new THREE.Vector3(wp.x, 0, wp.z);
  if (moveTowards(c.mesh, dest, c.speed, dt)) {
    c.waypoints.shift(); // arrived – discard this WP
  }
  return c.waypoints.length > 0; // false when last WP just consumed
}

function pathFromEntranceToQueue(fromPos) {
  // Walk straight down from the entrance along x≈-12 (between left wall and
  // leftmost table column at x=-8) until past all table rows, then go
  // diagonally to the queue slot in the open space in front of the counter.
  return [
    { x: fromPos.x, z: 2 }  // straight down, clears both table rows (z=8 and z=4)
    // after this waypoint the state machine targets the queue slot directly (diagonal)
  ];
}

function pathFromCounterToChair(fromPos, chair) {
  const lane = laneZForChair(chair);
  return [
    { x: fromPos.x, z: lane },   // climb up to approach lane keeping current X (passes between table columns at x=0)
    { x: chair.x,   z: lane },   // walk along lane to chair column
    { x: chair.x,   z: chair.z } // step into chair
  ];
}

function pathFromChairToExit(chair) {
  const lane = laneZForChair(chair);
  return [
    { x: chair.x,        z: lane },             // step out of table column
    { x: SIDE_AISLE_X,   z: lane },             // walk to right side aisle
    { x: SIDE_AISLE_X,   z: FRONT_LANE_Z },     // down to front lane
    { x: EXIT.x,         z: EXIT.z }            // out the door
  ];
}

// ════════════════════════════════════════════════════════════════════════════
// MOVEMENT / ANIMATION HELPERS
// ════════════════════════════════════════════════════════════════════════════
function moveTowards(person, target, speed, dt) {
  const dx = target.x - person.position.x;
  const dz = target.z - person.position.z;
  const dist = Math.hypot(dx, dz);
  if (dist < 0.05) return true;
  const step = Math.min(dist, speed * dt);
  person.position.x += (dx / dist) * step;
  person.position.z += (dz / dist) * step;
  person.rotation.y = Math.atan2(dx, dz);
  return false;
}

function animateWalk(person, t, speedFactor = 1) {
  const swing = Math.sin(t * 8 * speedFactor) * 0.4;
  person.children[2].rotation.x =  swing;
  person.children[3].rotation.x = -swing;
  person.children[4].rotation.x = -swing * 0.6;
  person.children[5].rotation.x =  swing * 0.6;
}

function animateIdle(person, t) {
  person.children[2].rotation.x = 0;
  person.children[3].rotation.x = 0;
  person.children[4].rotation.x = Math.sin(t * 1.5) * 0.05;
  person.children[5].rotation.x = Math.sin(t * 1.5 + 0.5) * 0.05;
}

function attachPizza(person) {
  const pizza = cyl(0.18, 0.18, 0.03, 16, M.pizza);
  place(pizza, 0, 1.05, 0.25);
  person.add(pizza);
  return pizza;
}

function detachPizza(person, pizza) {
  if (pizza && pizza.parent === person) person.remove(pizza);
}

function buildSlicedTablePizza(radius = 0.22, thickness = 0.03, slices = 8) {
  const group = new THREE.Group();
  const sliceAngle = (Math.PI * 2) / slices;
  const crustMat = new THREE.MeshStandardMaterial({ color: 0xc58a2b, roughness: 0.85 });
  const innerRadius = radius - 0.04;

  for (let i = 0; i < slices; i++) {
    const start = i * sliceAngle;
    const sliceGroup = new THREE.Group();

    // Full sector in crust color.
    const crust = new THREE.Mesh(
      new THREE.CylinderGeometry(radius, radius, thickness, 20, 1, false, start, sliceAngle),
      crustMat
    );
    crust.castShadow = true;
    crust.receiveShadow = true;
    sliceGroup.add(crust);

    // Smaller sector slightly above it to leave a visible cornicione ring.
    const slice = new THREE.Mesh(
      new THREE.CylinderGeometry(innerRadius, innerRadius, thickness * 0.72, 20, 1, false, start, sliceAngle),
      M.pizza
    );
    slice.position.y = thickness * 0.18;
    slice.castShadow = true;
    slice.receiveShadow = true;
    sliceGroup.add(slice);

    group.add(sliceGroup);
  }

  group.userData.sliceCount = slices;
  return group;
}

// ════════════════════════════════════════════════════════════════════════════
// EMPLOYEE LOGIC
// ════════════════════════════════════════════════════════════════════════════
function pickRandomOven() {
  const ids = [...ovenMeshes.keys()];
  if (ids.length === 0) return null;
  return ovenMeshes.get(ids[Math.floor(Math.random() * ids.length)]);
}

function updateEmployee(t, dt) {
  const e = employeeState;

  if (e.state === 'idle') {
    animateIdle(employee, t);
    // If first queued customer is at the counter waiting, start serving
    const front = queue[0];
    if (front && front.state === 'waiting_pizza') {
      const oven = pickRandomOven();
      if (oven) {
        e.state = 'going_to_oven';
        e.servingCustomer = front;
        e.target.set(oven.group.position.x, 0, OVEN_Z + 1.2);
      }
    }
    return;
  }

  if (e.state === 'going_to_oven') {
    animateWalk(employee, t);
    if (moveTowards(employee, e.target, e.speed, dt)) {
      // Pick up pizza
      e.pizza = attachPizza(employee);
      e.state = 'going_to_deliver';
      // Determine where customer is
      if (e.servingCustomer) {
        e.target.set(0, 0, COUNTER_BACK_Z);
      }
    }
    return;
  }

  if (e.state === 'going_to_deliver') {
    animateWalk(employee, t);
    if (moveTowards(employee, e.target, e.speed, dt)) {
      // Hand pizza to customer
      const cust = e.servingCustomer;
      if (cust && cust.state === 'waiting_pizza') {
        detachPizza(employee, e.pizza);
        cust.pizza = attachPizza(cust.mesh);
        // Customer goes to a chair
        const seat = pickFreeChair();
        if (seat) {
          seat.chair.occupied = true;
          cust.chair = seat.chair;
          cust.table = seat.table;
          cust.state = 'walking_to_table';
          cust.waypoints = pathFromCounterToChair(cust.mesh.position, seat.chair);
          // followWaypoints() will drive state walking_to_table
        } else {
          // No chairs: leave with pizza
          cust.state = 'leaving';
          cust.waypoints = [
            { x: SIDE_AISLE_X, z: cust.mesh.position.z },
            { x: SIDE_AISLE_X, z: FRONT_LANE_Z },
            { x: EXIT.x,       z: EXIT.z }
          ];
          // followWaypoints() will drive state leaving
        }
        // Remove from queue
        const idx = queue.indexOf(cust);
        if (idx >= 0) queue.splice(idx, 1);
      }
      e.pizza = null;
      e.servingCustomer = null;
      e.state = 'returning';
      e.target.copy(EMPLOYEE_HOME);
    }
    return;
  }

  if (e.state === 'returning') {
    animateWalk(employee, t);
    if (moveTowards(employee, e.target, e.speed, dt)) {
      employee.rotation.y = 0;
      e.state = 'idle';
    }
    return;
  }
}

// ════════════════════════════════════════════════════════════════════════════
// CUSTOMER LOGIC
// ════════════════════════════════════════════════════════════════════════════
function updateCustomer(c, t, dt) {
  switch (c.state) {
    case 'walking_to_queue': {
      animateWalk(c.mesh, t);
      // Follow pre-planned waypoints (avoids tables); once all consumed, head diagonally to queue slot.
      if (followWaypoints(c, dt)) break;  // still en-route to a waypoint
      if (c.waypoints && c.waypoints.length > 0) break; // arrived at last WP this frame, still has more (shouldn't happen but safe)
      const queueIdx = queue.indexOf(c);
      const target = queueSlotPosition(queueIdx >= 0 ? queueIdx : 0);
      if (moveTowards(c.mesh, target, c.speed, dt)) {
        c.state = 'queuing';
      }
      break;
    }
    case 'queuing': {
      const queueIdx = queue.indexOf(c);
      const target = queueSlotPosition(queueIdx >= 0 ? queueIdx : 0);
      const dx = target.x - c.mesh.position.x;
      const dz = target.z - c.mesh.position.z;
      const dist = Math.hypot(dx, dz);
      if (dist > 0.1) {
        animateWalk(c.mesh, t, 0.7);
        moveTowards(c.mesh, target, c.speed * 0.6, dt);
      } else {
        animateIdle(c.mesh, t);
        c.mesh.rotation.y = Math.PI; // face counter
        if (queueIdx === 0) c.state = 'waiting_pizza';
      }
      break;
    }
    case 'waiting_pizza': {
      animateIdle(c.mesh, t);
      c.mesh.rotation.y = Math.PI;
      break;
    }
    case 'walking_to_table': {
      animateWalk(c.mesh, t);
      if (followWaypoints(c, dt)) break;  // still en-route to a waypoint
      // All waypoints consumed – arrived at chair position, sit down.
        c.state = 'eating';
        c.eatStart = t;

        // Put pizza on the table, in front of the seated customer
        if (c.pizza) {
          detachPizza(c.mesh, c.pizza);
          c.pizza = null;
        }
        c.tablePizza = buildSlicedTablePizza(0.22, 0.03, 8);
        // Place pizza on the plate closest to the seated customer
        let pizzaX = c.chair.tableX;
        let pizzaY = 0.855;
        let pizzaZ = c.chair.tableZ;
        if (c.table?.plates?.length) {
          const nearestPlate = c.table.plates.reduce((best, p) => {
            const bestDist = Math.hypot(best.x - c.chair.x, best.z - c.chair.z);
            const pDist = Math.hypot(p.x - c.chair.x, p.z - c.chair.z);
            return pDist < bestDist ? p : best;
          });
          pizzaX = nearestPlate.x;
          pizzaY = nearestPlate.y;
          pizzaZ = nearestPlate.z;
        }
        place(c.tablePizza, pizzaX, pizzaY, pizzaZ);
        scene.add(c.tablePizza);

        // Sit: legs rest on the seat top
        c.mesh.position.set(c.chair.x, 0.13, c.chair.z);
        c.mesh.rotation.y = c.chair.dz < 0 ? 0 : Math.PI;
        // Bend legs forward (horizontal)
        c.mesh.children[2].rotation.x = -Math.PI / 2;
        c.mesh.children[3].rotation.x = -Math.PI / 2;
        // Lower torso, head, arms so torso sits on the legs (no gap)
        c.mesh.children[0].position.set(0,    0.67, -0.09);  // torso
        c.mesh.children[1].position.set(0,    1.07, -0.09);  // head
        c.mesh.children[4].position.set(-0.24, 0.67, -0.09); // larm
        c.mesh.children[5].position.set( 0.24, 0.67, -0.09); // rarm
      break;
    }
    case 'eating': {
      // animate arm bringing pizza to mouth
      const phase = (Math.sin((t - c.eatStart) * 3) + 1) / 2;
      c.mesh.children[4].rotation.x = -1.2 * phase;
      c.mesh.children[5].rotation.x = -0.3;
      // Eat pizza by slices (one slice per second)
      if (c.tablePizza) {
        const elapsed = t - c.eatStart;
        const sliceCount = c.tablePizza.userData.sliceCount || 8;
        const eatenSlices = Math.min(sliceCount, Math.floor(elapsed));

        // Children are slice groups: each contains [slice, crust]
        for (let i = 0; i < sliceCount; i++) {
          const visible = i >= eatenSlices;
          const sliceGroup = c.tablePizza.children[i];
          if (sliceGroup) sliceGroup.visible = visible;
        }
      }
      if (t - c.eatStart > 8) {
        // Done eating: stand up and leave
        c.mesh.position.y = 0;
        c.mesh.children[2].rotation.x = 0;
        c.mesh.children[3].rotation.x = 0;
        // Restore stand-up positions
        c.mesh.children[0].position.set(0,    0.85, 0);
        c.mesh.children[1].position.set(0,    1.25, 0);
        c.mesh.children[4].position.set(-0.24, 0.85, 0);
        c.mesh.children[5].position.set( 0.24, 0.85, 0);
        if (c.tablePizza) { scene.remove(c.tablePizza); c.tablePizza = null; }
        const leavingChair = c.chair;
        if (c.chair) { c.chair.occupied = false; c.chair = null; }
        c.state = 'leaving';
        c.waypoints = leavingChair
          ? pathFromChairToExit(leavingChair)
          : [{ x: EXIT.x, z: EXIT.z }];
        // followWaypoints() will drive state leaving
      }
      break;
    }
    case 'leaving': {
      animateWalk(c.mesh, t);
      if (followWaypoints(c, dt)) break;  // still en-route to a waypoint
      scene.remove(c.mesh);
      const i = customers.indexOf(c);
      if (i >= 0) customers.splice(i, 1);
      break;
    }
  }
}

// ════════════════════════════════════════════════════════════════════════════
// DATA POLLING
// ════════════════════════════════════════════════════════════════════════════
async function fetchOvens() {
  try {
    const resp = await fetch('/api/ovens');
    if (!resp.ok) throw new Error(resp.statusText);
    const data = await resp.json();

    // Remove ovens no longer present
    const incoming = new Set(data.map(o => o.OvenId));
    for (const [id, entry] of [...ovenMeshes]) {
      if (!incoming.has(id)) { scene.remove(entry.group); ovenMeshes.delete(id); }
    }
    // Add / update
    data.forEach(o => {
      if (!ovenMeshes.has(o.OvenId)) ovenMeshes.set(o.OvenId, createOvenMesh(o));
      updateOvenSprite(ovenMeshes.get(o.OvenId), o);
    });
    layoutOvens();

    // Maintain ambient glow lights pool
    while (ovenGlows.length < ovenMeshes.size) {
      const pl = new THREE.PointLight(0xff4500, 0, 5);
      scene.add(pl); ovenGlows.push(pl);
    }

    // UI panel
    const list = document.getElementById('oven-list');
    list.innerHTML = data.map(o => {
      const phase = Math.max(1, Math.min(3, o.CurrentPhase || 1));
      return `<div class=""oven-card phase${phase}"">
        <div class=""name"">${o.OvenName}</div>
        <div>🌡 ${o.CurrentTemperature}°C  •  Phase ${phase}</div>
        <div>🔥 Gas ${o.GasConsumption}  •  ⚡ Power ${o.PowerConsumption}</div>
        <div>🍕 ${o.CurrentRecipe}  •  Cycles ${o.Cycles}</div>
      </div>`;
    }).join('');

    const dbg = customers.map((c,i) => `#${i}:${c.state}@(${c.mesh.position.x.toFixed(1)},${c.mesh.position.z.toFixed(1)})`).join(' | ');
    document.getElementById('status').textContent =
      `Updated ${new Date().toLocaleTimeString()}  •  Ovens: ${data.length}  •  Emp: ${employeeState.state}  •  Cust: ${dbg || 'none'}`;
  } catch (e) {
    document.getElementById('status').textContent = `Error: ${e.message}`;
  }
}
fetchOvens();
setInterval(fetchOvens, 1500);

// ════════════════════════════════════════════════════════════════════════════
// ANIMATION LOOP
// ════════════════════════════════════════════════════════════════════════════
const clock = new THREE.Clock();
let spawnTimer = 0;
let lastSpawnInterval = 4;

function animate() {
  requestAnimationFrame(animate);
  const t = clock.getElapsedTime();
  let dt = clock.getDelta();
  if (!isFinite(dt) || dt <= 0) dt = 1/60;
  if (dt > 0.1) dt = 0.1;

  // Spawn customers
  spawnTimer += dt;
  if (spawnTimer > lastSpawnInterval && customers.length < 10) {
    spawnCustomer();
    spawnTimer = 0;
    lastSpawnInterval = 3 + Math.random() * 3;
  }

  updateEmployee(t, dt);
  // iterate over a copy because customers may be removed
  [...customers].forEach(c => updateCustomer(c, t, dt));

  // Oven flame flicker lights
  const ids = [...ovenMeshes.keys()];
  ids.forEach((id, i) => {
    if (i >= ovenGlows.length) return;
    const gl = ovenGlows[i];
    const entry = ovenMeshes.get(id);
    const phaseBase = entry.phase === 1 ? 0.55 : (entry.phase === 2 ? 0.8 : 1.0);
    const flicker = 0.86
      + 0.15 * Math.sin(t * 16 + entry.flickerSeed)
      + 0.08 * Math.sin(t * 31 + i * 1.7);
    const yScale = Math.max(0.55, phaseBase * flicker);
    entry.flameOuter.scale.set(1, yScale, 1);
    entry.flameInner.scale.set(1, Math.max(0.6, yScale * 1.12), 1);
    entry.flameOuter.position.y = 0.53 + (flicker - 0.86) * 0.06;
    entry.flameInner.position.y = 0.50 + (flicker - 0.86) * 0.05;
    entry.emberMat.opacity = Math.max(0.42, Math.min(0.9, 0.52 + phaseBase * 0.24 + (flicker - 0.8) * 0.22));
    entry.ovenLight.intensity = Math.max(0.9, 0.9 + phaseBase * 1.1 + (flicker - 0.86) * 0.9);
    gl.position.copy(entry.group.position).add(new THREE.Vector3(0, 0.6, 0));
    gl.intensity = Math.max(0.15, 0.2 + phaseBase * 0.55 + (flicker - 0.86) * 0.4);
    gl.color.copy(entry.flameOuterMat.color);
  });
  for (let i = ids.length; i < ovenGlows.length; i++) ovenGlows[i].intensity = 0;

  // Slight independent flicker for pendant lights above the counter
  pendantLights.forEach((p, i) => {
    const flick = 0.95 + 0.05 * Math.sin(t * 9 + p.seed) + 0.02 * Math.sin(t * 23 + i);
    p.bulbLight.intensity = 1.1 * flick;
    p.downLight.intensity = 2.0 * flick;
  });

  controls.update();
  renderer.render(scene, camera);
}

// Spawn first customer immediately so something happens right away
spawnCustomer();
animate();
</script>
</body>
</html>";
}
