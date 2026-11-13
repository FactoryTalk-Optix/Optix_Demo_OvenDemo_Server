# Oven Demo - Supervisor (Server)

**FactoryTalk Optix 1.8 | MQTT | RecipeX | SQLite | ECharts | OPC UA | 6 languages | Light/Dark theme**

A fleet supervisor for a distributed pizza-oven plant. One Optix application monitors every connected oven in real time,
manages the recipe lifecycle from draft to release, pushes recipes to the ovens, computes OEE, collects alarms and chats
with the operators on the machines.

It is the "head office" half of the OvenDemo NextGen pair. The "oven" half is the
**Optix_Demo_OvenDemo_Client** project (see its `OvenDemo_Client_Overview.md`), a simulated oven with its own HMI.

---

## 1. What the demo wants to show

| Message | How the demo proves it |
|---|---|
| One Optix application can supervise a whole fleet, not only one machine. | Ovens appear, disconnect and reconnect live in the fleet overview, grid, alarms and OEE pages. |
| Adding a machine takes minutes and no engineering. | The oven asks to join, an operator clicks **Accept**, telemetry starts flowing. No project edit, no restart. |
| IT/OT convergence with open protocols. | MQTT for telemetry, recipes, chat and onboarding; OPC UA available per oven; one embedded broker, no external infrastructure. |
| Recipes are governed, not copied around. | Draft -> In review -> Approved -> Deprecated workflow, then one-click distribution with a delivery matrix per oven. |
| The same project serves a kiosk, a tablet and a browser. | Native engine plus WebUI (https, port 8443), responsive layout, 6 languages, Light/Dark. |

---

## 2. Demo walkthrough (about 5-7 minutes)

1. **Fleet Overview** - KPI cards (online ovens, alarms, power, daily production), fleet state counters and a live activity feed
   (connected, cycle started/completed/rejected, fault, recipe applied...). Below, the grid of monitored ovens.
2. **Oven details** (click an oven) - status, production counters, live cycle chart (temperature vs. setpoint per phase).
   For locally simulated ovens the dialog also contains the controls to apply a recipe, start/stop cycles, inject a fault,
   pause telemetry or force a rejected cycle.
3. **Onboarding** - start a client, press **Request onboarding** on it. A badge appears on **Device registry**; accept the
   request and the oven shows up in the fleet within seconds.
4. **Recipe orchestration** - create or open a recipe, move it to Approved, send it to the fleet. The deployment matrix shows
   Queued / Sent / Delivered / Applied per oven. The oven decides when to apply it.
5. **Chat** - pick an oven, send a message, watch it become *delivered* when the operator's screen acknowledges it. Unread
   badges appear on both sides.
6. **Alarms** - stop a client (or inject a fault on a simulated oven): a connection or process alarm is raised, can be
   acknowledged and confirmed, and stays in the history.
7. **OEE** - Availability x Performance x Quality per oven and for the fleet, with daily history and charts.
8. **Settings** - switch language, theme, units and planned production hours on the fly.

---

## 3. Features

### Operations

| Page | What it does |
|---|---|
| **Fleet Overview** | KPI cards, fleet state counters (running, idle, offline, fault), recent-activity feed (events are localized), grid of monitored ovens. |
| **Fleet Operations** | All ovens as cards (Grid) or rows (List), with search by site or device; entry point to the oven details dialog. |
| **Fleet alarm events** | Active alarms (acknowledge / confirm actions) and alarm history from the event logger. |
| **Device registry** | Pending onboarding requests (accept / discard), registered ovens with connection state, offboarding, registry log. |
| **Chat** | One conversation per oven, persistent history, delivery status, unread badges. |

### Process

| Page | What it does |
|---|---|
| **Recipe orchestration** | RecipeX recipe library with a per-session editor (PreHeat / Bake / Cooldown phases), workflow status, send to fleet, delivery matrix. |
| **OEE** | Fleet and per-oven Availability, Performance, Quality and OEE (energy, cycles, good / rejected, run time); daily history stored in SQLite; ECharts page that follows the theme. |

### IT / OT

| Page | What it does |
|---|---|
| **Connectivity** | Connectivity health: MQTT broker parameters, configured subscriptions and the topics they map to, database size, session type and screen resolution, runtime output log. |
| **HMI Settings** | Language, dark theme, measurement units, fleet view (grid / list), planned production hours per day. |
| **User management** | Users and groups (standard Optix template). |

### Functions in detail

- **Live telemetry** - every oven publishes an `OvenType` snapshot: state, phase, temperature and setpoint, batch id, applied
  recipe, door, power, energy, cycles, uptime and the counters used for OEE.
- **Connection supervision** - a behavior per oven (`OvenEdgeManager`) watches the telemetry timestamp. No change for 10 s
  means *disconnected*; a connection alarm is raised and cleared automatically.
- **One-click onboarding** - the oven sends its id and its OPC UA endpoint, the supervisor creates the oven's registry entry,
  a dedicated MQTT client (`Sup_<DeviceId>`) and an OPC UA client (enabled only while the oven is connected).
  Removing an oven deletes its MQTT and OPC UA clients, its registry entry and its chat history; a declined request is simply discarded and the oven is told.
- **Governed recipes** - only *Approved* recipes (or *Deprecated* status updates) leave the supervisor. Each recipe carries a
  content hash (SHA-256, first 16 hex chars) so the supervisor and the oven agree on what was delivered. An oven that
  reconnects announces its catalog (`recipe.hello`) and receives what it is missing. The supervisor never forces a recipe
  on a machine: the oven applies it (at the next cycle boundary if a cycle is running) and reports back for information.
- **Reliable chat** - messages are queued while the oven is offline and flushed on reconnect. Status: queued -> sent -> delivered
  (acknowledged by the other side). Duplicates are discarded, history is capped at 500 messages per conversation.
- **Alarms** - per oven *ConnectionError* and *ProcessError* alarms, active list with acknowledge / confirm, history in SQLite.
  The history does not duplicate events after a supervisor restart.
- **OEE** - computed from the cumulative counters sent by the ovens, with counter-epoch handling so an oven restart does not
  corrupt the day. Planned production hours are configurable.
- **Local simulation** - ovens that are not registered through onboarding are simulated by the supervisor itself. The project
  ships with two sample ovens, so the demo is alive even with no client running.
- **Localization and themes** - English, Spanish, Italian, French, German and Chinese; Light and Dark palettes, also applied to
  the ECharts pages.

---

## 4. How it works

```text
                          +--------------------------------------------+
                          |          OvenDemo Supervisor (this)         |
                          |--------------------------------------------|
                          |  UI: Native + WebUI (https :8443)           |
                          |  Embedded MQTT broker        :8883          |
                          |  OPC UA server               :59100         |
                          |  RecipeX + SQLite (recipes, chat, OEE, logs)|
                          |  NetLogic: onboarding, recipes, chat, OEE   |
                          +----------------------+---------------------+
                                                 |
                   MQTT (one Sup_<DeviceId> client per oven)  +  OPC UA
                                                 |
              +----------------------+-----------+-----------+----------------------+
              |                      |                       |                      |
        OvenDemo Client 1      OvenDemo Client 2       OvenDemo Client N      local simulated ovens
```

### MQTT topics

| Topic | Direction | Content |
|---|---|---|
| `OnboardingRequest` | oven -> supervisor | `{Sender, Content (OPC UA endpoint), Timestamp}` |
| `OnboardingResult` | supervisor -> oven | Status: 0 none, 1 pending, 2 onboarded, 3 declined, 4 timeout |
| `<DeviceId>` | oven -> supervisor | Telemetry (Optix Default JSON of `OvenType`) |
| `chat/<DeviceId>/to-edge`, `chat/<DeviceId>/to-supervisor` | both | Chat message / acknowledgement (QoS 1) |
| `recipes/<DeviceId>/to-edge`, `recipes/<DeviceId>/to-supervisor` | both | Recipe delivery, hello/catalog, apply result |

### Technology

| Area | Technology |
|---|---|
| Platform | FactoryTalk Optix Studio 1.8.0.555, .NET NetLogic (C#) |
| UI | Native + WebUI engines, responsive layout, StyleSheets Light/Dark, 6 languages |
| Messaging | Embedded `MQTTBroker`, `MQTTClient` / `MQTTSubscriber` created at runtime per oven |
| Recipes | `RecipeX` with a custom `RecipeSchema`, per-session edit buffer, workflow metadata |
| Persistence | `SQLiteStore` (recipes, delivery journal, chat, OEE history, event logger), retentive model for fleet, managers, MQTT/OPC UA clients |
| Alarms | `Alarm` module (`OffNormalAlarmController`, `DigitalAlarm`), `EventLogger` |
| Charts | Apache ECharts in `WebBrowser` widgets (fleet overview, OEE, oven cycle) fed by runtime-generated data files |
| Interop | `OPCUAServer` and `OPCUAClient` (one client per onboarded oven) |

### Where to look in the code

| File | Purpose |
|---|---|
| `ProjectFiles/NetSolution/OnboardingLogic.cs`, `PendingRequestRowLogic.cs`, `DeviceRegistryRowLogic.cs` | Onboarding handshake, per-oven MQTT client helper, offboarding |
| `OvenEdgeManagerBehavior.cs` | Connection supervision and connection alarm |
| `RecipeOrchestratorLogic.cs`, `OvensRecipesLogic.cs`, `RecipeListLogic.cs` | Recipe editor, workflow, distribution and delivery matrix |
| `ChatLogic.cs` | Chat history, queue, acknowledgements |
| `ProjectLogic.cs` | Local oven simulation, OEE collector, fleet overview feed |
| `FleetGridLogic.cs`, `OvenCycleChartLogic.cs`, `AnalyticsChartLogic.cs` | Oven details texts, cycle and analytics charts |
| `OpcUaClientRuntimeHelper.cs` | OPC UA client per oven |
| `DesignTimeNetLogic1.cs` | Design-time builders and fixes, run from Studio (not part of the runtime) |
| `ProjectFiles/eCharts/` | ECharts pages, scripts and styles |

---

## 5. Run it

**Prerequisites:** FactoryTalk Optix Studio 1.8.0.555 or later. No external broker or database is needed.

1. Open `Optix_Demo_OvenDemo_Server.optix` in Studio and start it (Emulator).
2. The WebUI is available at `https://<server-ip>:8443`; the broker listens on port `8883`, the OPC UA server on `59100`.
3. Start one or more **Optix_Demo_OvenDemo_Client** projects, set the supervisor
   IP in their settings and request onboarding.
4. Accept the request in **Device registry**.

Without any client the two sample ovens run in local simulation, so every page already shows data.

---

## 6. Good to know

- The broker accepts at most 25 connections, one of which is used by onboarding, so the per-oven client design supports up to
  24 ovens.
- The demo broker is anonymous and not encrypted (it is meant for a trade-show LAN). Production use needs TLS, authentication
  and rights on the recipe actions.
- Texts generated by code at runtime (some detail texts, alarm messages) are English only; see `TODO.md`.
- The recipe edit lock is demo-grade (one session at a time per Draft).
