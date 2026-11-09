# LMU local game REST API

Le Mans Ultimate runs an HTTP server on the local machine while the game is open.
The in-game UI is built on it, so anything the menus can do (pick a series, a track, a
car, build a session, load it, leave it) can be scripted. We first mapped it in April
2026 with scripts that set up and loaded sessions.

This file records the calls, the payload shape that worked, and the quirks that cost
time. Nothing here comes from official documentation: it is what
we observed against game version 1.23 era builds. Field lists are the fields we read,
not the full response.

Scripts that use it, all in `scripts/`:

| Script | What it does |
|---|---|
| `_test_flow.py` | The setup-and-load flow, one step at a time, printing every status and body |
| `probe_api.py` | First probe: set track, set car, load default setup |
| `_list_tracks.py`, `_list_folders.py`, `dump_tracks.py` | List tracks and car folders, dump track JSON (`tracks_dump.json`) |

Those scripts live in a separate project, not in this repository. In this repository the
read-only probe is `lmucareer lmu state|dump|diff` (`src/LmuCareer.Cli/LmuProbe.cs`); see
[Probing the session preset](#probing-the-session-preset).

**November 2026 update.** Everything below was rechecked on game build 14200 (track
version 1.31) on 2026-11-09, and the April calls still work. That pass added the
[session preset fields](#session-preset-fields) a race needs, the
[live settings API](#settings-options) (AI Strength and the like), how
[Settings.JSON](#settingsjson-read-at-start-written-at-exit) is read and written, and the
[full route list](#every-route-the-game-knows) from the game exe.

## Basics

- **Base URL**: `http://localhost:6397`. `probe_api.py`'s docstring mentions 5397, but
  every script defaults to 6397 and that is the port that answered.
- **Reachable only while the game runs**, and only from the same machine.
- **No authentication.** No token, cookie or header.
- **Bodies**: most POSTs take JSON. Two calls (`/rest/race/track` and
  `/rest/garage/SetCurrentVehicle`) want the body sent as `Content-Type: text/plain`;
  see each entry.
- **Status codes**: setters answer `200` or `204`. Treat both as success.
- **A connection error during a load means the game crashed.** The scripts stop the
  run when `loadGame` raises instead of retrying.

## Endpoint index

| Method | Path | Group |
|---|---|---|
| GET | `/navigation/state` | Navigation |
| POST | `/navigation/action/NAV_TO_MAIN_MENU` | Navigation |
| GET | `/rest/sessions/getAllVehicles` | Catalog |
| GET | `/rest/sessions/getTracksAll` | Catalog |
| GET | `/rest/race/track/{id}/image` | Catalog |
| GET | `/rest/race/track/{id}/thumbnail` | Catalog |
| POST | `/rest/sessions/Championship/setCurrentChampionshipTemplate` | Session setup |
| POST | `/rest/race/track` | Session setup |
| POST | `/rest/sessions/SessionPresets/getDefaultPresetForTrack` | Session setup |
| POST | `/rest/sessions/SessionPresets/applyPreset` | Session setup |
| POST | `/rest/garage/SetCurrentVehicle` | Session setup |
| POST | `/rest/garage/setup/default` | Garage |
| POST | `/rest/sessions/SessionPresets/requestPreset` | Session setup |
| POST | `/rest/sessions/SaveLoad/generateSaveFileFromSessionPreset` | Load |
| POST | `/rest/sessions/SaveLoad/loadGame` | Load |
| POST | `/rest/sessions/returnToMonitor` | Exit |
| GET | `/rest/options/settings` | Settings |
| POST | `/rest/options/long`, `/rest/options/float` | Settings |
| POST | `/rest/sessions/settings` | Session setup (not used yet) |

## Navigation

### `GET /navigation/state`

Where the game is. Polled every 2 seconds to wait for a load or a return to the menu.

Response (fields we read):

```json
{
  "state": {
    "navigationState": "NAV_MAIN_MENU",
    "gameState": "GSTATE_..."
  }
}
```

Values we relied on:

| Field | Value | Meaning |
|---|---|---|
| `navigationState` | `NAV_MAIN_MENU`, `NAV_SETUP` | In the menus; safe to set up a new session |
| `navigationState` | `NAV_EVENT` | Inside a loaded session |
| `gameState` | `GSTATE_DYN` | Physics running: the car is loaded on track |

A failed request or an empty `state` means the game is not reachable. The scripts use
this call as their "is LMU running" check.

### `POST /navigation/action/NAV_TO_MAIN_MENU`

No body. Sends the UI back to the main menu. Always called after `returnToMonitor`,
never alone from inside a session (see Exit).

## Catalog

### `GET /rest/sessions/getAllVehicles`

Every car entry the game knows, one per livery/entry, so a single car model appears
many times. Response is a JSON array.

Fields we read:

| Field | Example / meaning |
|---|---|
| `id` | Opaque string. Pass it to `SetCurrentVehicle` |
| `vehFile` | Path to the `.veh` file. The folder after `Vehicles` is the car model, e.g. `...\Vehicles\<Folder>\...` |
| `fullPathTree` | Human path including series and year, e.g. contains `WEC` or `ELMS` and `2025` |
| `classes` | Array, e.g. `["Hypercar"]`, `["GT3"]`, `["LMGT3"]` |
| `primaryVehFile` | Bool. Used as a tiebreak when picking one entry per model |

To get one entry per car model, the scripts group by the `vehFile` folder and keep the
entry whose `fullPathTree` matches the wanted series, then the newest year, then
`primaryVehFile`.

```python
parts = re.split(r"[/\\]+", v["vehFile"])
folder = parts[parts.index("Vehicles") + 1]
```

### `GET /rest/sessions/getTracksAll`

Every track layout. JSON array; 35 entries on the build we dumped
(`scripts/tracks_dump.json` holds the full response).

One entry, weather arrays trimmed:

```json
{
  "id": "1264a521f81475e528c45fb44879b79985c46f24",
  "sceneDesc": "PORTIMAOELMS",
  "cmpName": "PortimaoWEC_2023",
  "trackName": "Algarve International Circuit",
  "properTrackName": "Algarve International Circuit",
  "eventName": "4 Hours of Portimao",
  "grandPrixName": "4 Hours of Portimao",
  "location": "Portimão, Portugal",
  "countryCode": "PT",
  "trackLength": "4.653",
  "corners": "15",
  "openingYear": "2008",
  "type": "Road Course",
  "version": "1.23",
  "isOwned": true,
  "dlcAppID": 0,
  "premId": 999998,
  "officialEvent": false,
  "sceneSig": "aac86a8d...",
  "image": "/rest/race/track/1264a521.../image",
  "thumbnail": "rest/race/track/1264a521.../thumbnail",
  "defaultRaceLengthLaps": 0,
  "defaultRaceLengthTime": 360,
  "defaultPracticeStartTime": 660,
  "defaultQualifyStartTime": 915,
  "defaultRaceStartTime": 870,
  "defaultPracticeWeather": [{"Duration": 30, "Humidity": 60, "RainChance": 0, "Sky": 1, "StartTime": 540, "Temperature": 11, "WindDirection": 1, "WindSpeed": 17}],
  "defaultQualifyWeather": ["..."],
  "defaultRaceWeather": ["..."]
}
```

Notes:

- `id` goes to `POST /rest/race/track`. `sceneDesc` goes to `getDefaultPresetForTrack`.
- `sceneDesc` is the layout key. Main layouts end in
  `WEC` or `ELMS` (`SPAWEC`, `SPAELMS`); alternates carry a suffix (`SPAWEC_ENDCE`,
  `BAHRAINWEC_OUTER`, `PAULRICARD1A-V2-SHORT`). The scripts select a series' main
  layouts with the regex `WEC$` or `ELMS$`.
- `cmpName` (`PortimaoWEC_2023`) is the same identifier the online backend (Nakama)
  used for tracks.
- Text fields come back in a non UTF-8 codepage (`Portim�o`), so decode accented
  names with care.
- Times (`*StartTime`) are minutes from midnight; `defaultRaceLengthTime` is minutes.
- `LIVERYSHOWROOM` is in the list and is not a racing track.

### `GET /rest/race/track/{id}/image`, `GET /rest/race/track/{id}/thumbnail`

Track artwork, linked from each track entry. We never fetched them; they are listed
because the catalog hands them out. Note `thumbnail` is returned without a leading
slash.

## Session setup

The order below is the one that works. Each setter was followed by a 0.5 s pause.

### `POST /rest/sessions/Championship/setCurrentChampionshipTemplate`

Picks the series ruleset, which decides the BoP that will apply.

```json
{"name": "wec2025"}
```

Template names we used: `wec2023`, `wec2024`, `wec2025`, `wec2026`, `elms2025`.
Only needs calling when the series changes.

### `POST /rest/race/track`

Body is the track `id` as raw text, **not JSON**:

```python
s.post(f"{base}/rest/race/track", data=track["id"],
       headers={"Content-Type": "text/plain"})
```

### `POST /rest/sessions/SessionPresets/getDefaultPresetForTrack`

```json
{"trackName": "SPAWEC"}
```

`trackName` is the `sceneDesc`, not `trackName` from the catalog. Returns a full
session preset object (same shape as `requestPreset` below). Fails with a non 200
when the track is not valid for the current championship.

### `POST /rest/sessions/SessionPresets/applyPreset`

Body: the preset object exactly as `getDefaultPresetForTrack` returned it. Applies the
track defaults (sessions, weather, grid) and **resets the player car** to the
preset's default.

### `POST /rest/garage/SetCurrentVehicle`

Body is a JSON string sent as text:

```python
s.post(f"{base}/rest/garage/SetCurrentVehicle",
       data=json.dumps({"id": vehicle["id"]}),
       headers={"Content-Type": "text/plain"})
```

**Call it after `applyPreset`**, or the preset overwrites the car. `probe_api.py`
sent the bare id as the body; the `{"id": ...}` form is the one the working loop uses.

### `POST /rest/sessions/SessionPresets/requestPreset`

No body (`json=None`). Returns the current session preset, now holding the chosen
track and car. Fields we read or changed:

| Path | Use |
|---|---|
| `Player.DRIVER.Vehicle` | The car the session will load. Check it here to confirm `SetCurrentVehicle` stuck |
| `Player["Game Options"].Opponents` | Set to `0` |
| `Player["Game Options"]["Drivers Per Vehicle AI"]` | Set to `0` |
| `Grid` | Set to `[]` to drop the AI field |

Emptying the grid and opponents makes a load much faster, which helps for test loads.

## Garage

### `POST /rest/garage/setup/default`

No body. Loads the default setup for the current car. Used only in `probe_api.py`;
it does not load the car into a session.

## Load

### `POST /rest/sessions/SaveLoad/generateSaveFileFromSessionPreset`

Body: the (edited) preset from `requestPreset`. Returns a save object. Treat it as
opaque and pass it on unchanged.

### `POST /rest/sessions/SaveLoad/loadGame`

```json
{"save": <object returned by generateSaveFileFromSessionPreset>}
```

Starts loading the session. The call returns before the load finishes. Poll
`/navigation/state` until `navigationState == "NAV_EVENT"` and
`gameState == "GSTATE_DYN"`; allow up to 120 s. If the request itself raises a
connection error, the game has crashed.

## Exit

### `POST /rest/sessions/returnToMonitor`

No body. Leaves the car and returns to the session monitor screen. Then call
`NAV_TO_MAIN_MENU` and poll until `navigationState` is `NAV_MAIN_MENU` or `NAV_SETUP`
(up to 45 s).

The scripts pause 3 s before `returnToMonitor`, 3 s before `NAV_TO_MAIN_MENU`, and
4 s after reaching the menu before the next load. Going faster than that was
unreliable.

## Full flow

Start of run: if `navigationState` is not a menu state, call `returnToMonitor` then
`NAV_TO_MAIN_MENU` and wait.

Per car and track:

1. `setCurrentChampionshipTemplate` (only when the series changes)
2. `POST /rest/race/track` with the track `id` as text
3. `getDefaultPresetForTrack` with `sceneDesc`
4. `applyPreset` with that preset
5. `SetCurrentVehicle` with `{"id": ...}` as text
6. `requestPreset`, then empty `Grid` and set opponents to 0
7. `generateSaveFileFromSessionPreset` with that preset
8. `loadGame` with `{"save": ...}`
9. Poll `/navigation/state` for `NAV_EVENT` + `GSTATE_DYN`
10. Wait 3 s, `returnToMonitor`, wait 3 s, `NAV_TO_MAIN_MENU`, poll for menu, wait 4 s

Minimal Python:

```python
import json, time, requests

base = "http://localhost:6397"
s = requests.Session()

def state():
    return s.get(f"{base}/navigation/state", timeout=5).json().get("state", {})

def wait(nav, game=None, timeout=120):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        st = state()
        if st.get("navigationState") in nav and (game is None or st.get("gameState") in game):
            return st
        time.sleep(2)
    return None

def load(champ, track, vehicle):
    s.post(f"{base}/rest/sessions/Championship/setCurrentChampionshipTemplate", json={"name": champ})
    s.post(f"{base}/rest/race/track", data=track["id"], headers={"Content-Type": "text/plain"})
    preset = s.post(f"{base}/rest/sessions/SessionPresets/getDefaultPresetForTrack",
                    json={"trackName": track["sceneDesc"]}).json()
    s.post(f"{base}/rest/sessions/SessionPresets/applyPreset", json=preset)
    s.post(f"{base}/rest/garage/SetCurrentVehicle", data=json.dumps({"id": vehicle["id"]}),
           headers={"Content-Type": "text/plain"})
    preset = s.post(f"{base}/rest/sessions/SessionPresets/requestPreset", json=None).json()
    preset["Grid"] = []
    opts = preset.get("Player", {}).get("Game Options") or {}
    opts["Opponents"] = 0
    opts["Drivers Per Vehicle AI"] = 0
    save = s.post(f"{base}/rest/sessions/SaveLoad/generateSaveFileFromSessionPreset", json=preset).json()
    s.post(f"{base}/rest/sessions/SaveLoad/loadGame", json={"save": save})
    return wait({"NAV_EVENT"}, {"GSTATE_DYN"})

def leave():
    time.sleep(3)
    s.post(f"{base}/rest/sessions/returnToMonitor")
    time.sleep(3)
    s.post(f"{base}/navigation/action/NAV_TO_MAIN_MENU")
    wait({"NAV_MAIN_MENU", "NAV_SETUP"}, timeout=45)
    time.sleep(4)
```

## Session preset fields

The preset from `requestPreset` (and the one `applyPreset`/`generateSaveFileFromSessionPreset`
take) has three top-level keys: `Grid`, `Player` and `Weather`. `Player` holds `DRIVER`,
`Game Options`, `Mechanical Failures`, `Race Conditions` and `SCENE`
(`{"Scene Description": "IMOLAWEC"}`).

Found by dumping the preset, changing one thing in LMU's race weekend menu, and dumping
again (2026-11-09, build 14200, Imola, Ferrari 296 LMGT3). **Confirmed** means a diff showed
the menu writing it; **seen** means the field is there with a plausible value but no diff
has proved it yet.

### Race settings a Factory Seat briefing uses

| Briefing setting | Preset field | Values | Status |
|---|---|---|---|
| Race length | `Player["Game Options"]["Race Time"]` | Minutes (15, 30, ...) | Confirmed |
| | `Player["Game Options"]["Race Finish Criteria"]` | `2` = timed. Was `-1` in a fresh preset | Confirmed |
| | `Player["Game Options"]["Race Length"]` | Race minutes / 1440 (30 min = 0.0208). Set it to match | Confirmed |
| Time Scale | `Player["Race Conditions"].RaceTimeScale` | `1`-`60` = the X1-X60 multiplier (`-1` = race distance percentage, `0` as found in a fresh preset). The menu's **Advanced** Time Scale | Confirmed |
| Fuel Usage | `Player["Game Options"]["Fuel Consumption Multiplier"]` | `1` = Real; x2, x3 presumably `2`, `3` | Seen |
| Tyre Wear | `Player["Game Options"]["Tire Wear Multiplier"]` | Same as fuel | Seen |
| Race start time | `Player["Race Conditions"].RaceStartingTime` | Minutes from midnight; `-1` = the track default (`defaultRaceStartTime`) | Seen |
| Damage | `Player["Game Options"]["Damage Multiplier"]` | Percent (`100`) | Seen |
| AI field | `Grid` | Array of vehicle `id`s from `getAllVehicles`. `Grid[0]` is the player's car | Confirmed |
| Sessions | `Player["Race Conditions"]["Run Practice1"]`..`["Run Practice4"]`, `["Run Warmup"]`, `["Num Qual Sessions"]`, `["Num Race Sessions"]` | Bools / counts | Seen |
| Weather | `Weather.<Practice\|Qualifying\|Race>.Weather[0..4]` | See [Weather slots](#weather-slots) | Confirmed |
| AI Strength | Not in the preset | See [Settings (options)](#settings-options) | Confirmed |

Other fields in the preset as found (defaults on that build):

- `Game Options`: `Opponents` 0 (the grid decides the field, not this), `Drivers Per Vehicle AI` 1,
  `Drivers Per Vehicle Player` 1, `CrashRecovery` 3, `Race Laps` 0, `Qualifying Laps` 255,
  `practice length` 180, `qualifying length` 60, `warmup length` 10, `Tire Warmers` false,
  `Starting Pos` 0, `Stop go penalties` true, `Allowed Vehicles.Optional` (e.g.
  `["Ferrari_296_LMGT3"]`).
- `Race Conditions`: `Weather` 4 (weather mode), `TimeScaledWeather` true (weather moves with
  the scaled clock), `Formation Lap` 1, `Flag Rules` 2, `BlueFlags` 1, `ParcFerme` 3,
  `Track Limits Rules` 1, `Practice1StartingTime` 720, `QualifyingStartingTime` -1.
- `Mechanical Failures`: `Failure Rate` 1 (0 none, 1 normal, 2 time-scaled).
- `Weather.<session>.Road`: `RealRoad` (`"preset:Saturated.rrbin"` and the like), `WaterDepth`.

### Not the clock: the RealRoad time scales

`RealRoadTimeScalePractice`, `RealRoadTimeScaleQualifying` and `RealRoadTimeScaleRace` (in
`Race Conditions`) are how fast the track rubbers in, not the clock. Settings.JSON's own
description: `-1` scaled with session length, `0` static, `0.1`-`0.9` slow, `1` normal,
`2` 2x. The menu's per-session time scale (race 2x → `2.0`, qualifying 3x → `3.0`) writes
these. Leave them at the defaults.

### Weather slots

Each session has five slots, the session split into fifths. A slot holds `Sky`,
`RainChance`, `Temperature`, `Humidity`, `WindSpeed` and `WindDirection`, with no start
time or duration (the track catalog's `default*Weather` entries have those).

- `Sky` `5` is the first sky with rain: setting it in the menu also set that slot's
  `RainChance` from 0 to 75 by itself. Send both.
- Every track's default weather only uses `Sky` 0-3, so a race on track defaults is dry.
- rFactor 2's scale runs to 10 (drizzle, light rain, rain, heavy rain, storm). Not yet
  checked which value makes the AI pit for wets. A wet race needs that, since Factory Seat
  only counts a wet round when some car ran wet tyres.
- Raising `Sky` in the menu also dropped that slot's `Temperature` by a degree.

### What `generateSaveFileFromSessionPreset` returns

Plain JSON, not compressed or signed. Top level: `SessionPreset` (the preset as sent),
`Weather`, `RealRoad`, `VehicleSetup`, `aiVehicles` (empty: the AI is filled in on load),
`playerVehicle` (`{"slotID": -2}`), `allowedVehiclesFilter`, `sessionTimescale` (the
`RaceTimeScale`, e.g. `4.0`), `startTime`/`timeOfDay` (seconds from midnight), `endET`,
`saveVersion` (18), `uniqueSessionID` and session state fields. No AI Strength.

## Settings (options)

LMU's menus read and change the player settings (Settings.JSON's contents) through these
calls. Found in the menu code (`Bin/UI.zip`, `start/assets/app-*.js`) and tested.

### `GET /rest/options/settings`

Every setting, keyed by name (224 on build 14200; prefixes `GAMEOPT`, `DRIVEAIDS`,
`GRAPHOPT`, `SOUNDOPT`, `CONTROL`, `DISPLAYOPT`, `REPLAYOPT`, `SERVEROPT`, `MULTI`, `MP`,
`VIDEOOPT`, `COPY`):

```json
"GAMEOPT_ai_driverstrength": {
  "currentValue": 87, "maxValue": 105, "minValue": 75, "stepValue": 1,
  "stringValue": "Intermediate (87%)", "valueType": "LONG"
}
```

Ones a career uses: `GAMEOPT_ai_driverstrength` (75-105), `GAMEOPT_ai_aggression`
(0 Low, 1 Normal, 2 High), `GAMEOPT_damagemultiplier`.

### `POST /rest/options/{valueType}`

`valueType` in lower case (`long`, `float`), body JSON:

```json
{"optionEntry": "GAMEOPT_ai_driverstrength", "value": 95}
```

Answers `200` with the setting as it now is
(`{"currentValue":95,...,"stringValue":"Advanced (95%)","valueType":"LONG"}`). Takes effect
in the game at once: a value set this way was what LMU wrote to Settings.JSON at exit.

**The menu doesn't show it.** The UI keeps its own copy of the settings
(`useSettingsStore`), fetched once, so the AI Strength slider kept showing the old value
after an API change, even on a newly opened page. And when the player moves that slider,
the menu writes its own value back. Set AI Strength last, after the player is done in
the menus, right before `loadGame`.

### AI Strength in `loadGame`

The menu's own loader sends `{"save": <save>, "patch": {"aiStrength": N, "coopGameID": ...}}`
to `loadGame` when it starts from a weekend save. Not tested with a generated save yet; a
second way to set the race's AI Strength.

### `GET /rest/sessions/` and `POST /rest/sessions/settings`

`GET /rest/sessions/` returns the current session settings as `SESSSET_*` entries in the
same `{currentValue, stringValue, ...}` shape as the options, for example
`SESSSET_race_timescale: "X5" (5)`, `SESSSET_Fuel_Usage: "X2" (2)`,
`SESSSET_Tire_Wear: "X3" (3)`, `SESSSET_AI_Strength: "97%" (97)`,
`SESSSET_race_starting_time: "3:00 PM" (900)`, `SESSSET_race_time` (10),
`SESSSET_pract1: "Off"`, `SESSSET_Grid_Position: "Random" (0)`. The best way to check what
a loaded race will run with.

`POST /rest/sessions/settings` with `{"sessionSetting": <name>, "value": <step value>}` is
how the menu changes one of them. Not needed when sending a whole preset; not tested.

## Tested end to end: a race built from the API

2026-11-09, build 14200: Imola, the #21 Ferrari 296 LMGT3 and five other WEC 2026 GT3 cars,
10-minute race only, Time Scale X5, start 15:00, Fuel x2, Tyres x3, damage 100,
AI Strength 97. Flow: `setCurrentChampionshipTemplate` → `race/track` →
`getDefaultPresetForTrack` → `applyPreset` → `SetCurrentVehicle` → `requestPreset`, edit
→ `applyPreset` (edited) → `options/long` (AI Strength) → `requestPreset` →
`generateSaveFileFromSessionPreset` → `loadGame` with `patch.aiStrength`.

The loaded session had all of it: `navigation/state` `RACE1` only;
`GetSessionsInfoForEvent` one `RACE` of `lengthTime` 10; `watch/sessionInfo` 6 vehicles and
`endEventTime` 600; `GetGameState` `timeOfDay` 54000 (15:00); and the `SESSSET_*` values
above. Notes from the run:

- **The menu's race weekend screen showed a different grid** from the one loaded. Like
  the settings, the menu shows its own copy; the loaded race used the API's grid.
- `navigation/state` reported `PRACTICE1` for a moment right after `loadGame`, then `RACE1`.
- **The RealRoad time scales carried over** from earlier menu changes (qualifying 3x,
  race 2x) through `getDefaultPresetForTrack`, so set them explicitly.
- `Starting Pos` 0 is `SESSSET_Grid_Position` "Random". Set it if the start position matters.
- The session opens in the monitor (`inMonitor: true`, `MultiStintState: MONITOR_MENU`);
  the player drives from there.
- After the flag the player is still on track (`NAV_REALTIME`, `gamePhase` `CHECKERED`,
  `MultiStintState` `END_OF_RACE_GRACE`, `raceFinished` true).

**Results.** LMU wrote `UserData\Log\Results\2026_11_09_12_04_13-98R1.xml` when the player
left the session: `<Setting>Race Weekend</Setting>`, `<RaceTime>10`, `<FuelMult>2`,
`<TireMult>3`, the six cars, track folder `ImolaWEC_2024`, layout `layoutImola`. So a race
loaded through the API counts as an offline race weekend. Factory Seat's `RoundMatcher`
returned **Match** for it against a round with the same settings. Settings.JSON, written at
the same moment, held AI Strength 97.

## Settings.JSON: read at start, written at exit

`UserData\player\Settings.JSON` is LMU's copy of the player settings and the last session
setup (the same `Game Options` and `Race Conditions` names as the preset). Tested
2026-11-09:

- **Read only at startup.** A value written while the game ran (AI Strength 93, then 87)
  never reached the menu, even after reopening the page. Written while the game was closed
  (87), it was the value after the next start.
- **Overwritten with what the game holds in memory** at exit, and also when a race session
  ends (written in the same second as the race's results file). An edit made while the game
  runs is lost.
- That end-of-race write records the settings the race actually ran with (AI Strength,
  `RaceTimeScale`, fuel, tyres, `Race Time`, `RaceStartingTime`).
- So while LMU runs, change settings through the options API, never the file. The file
  shows the game's settings as of its last exit.
- It keeps Windows (CRLF) line endings. Git Bash's `sed -i` converts them, so edit it
  byte for byte if you have to.

## Probing the session preset

`lmucareer lmu` in this repository (`src/LmuCareer.Cli/LmuProbe.cs`) only makes reading calls:

```
dotnet run --project src/LmuCareer.Cli -- lmu state
dotnet run --project src/LmuCareer.Cli -- lmu dump <label>      # to lmu-probe/<label>/
dotnet run --project src/LmuCareer.Cli -- lmu diff <a> <b>      # preset paths that changed
```

A dump holds `state.json`, `preset.json`, `tracks.json` and `vehicles.json`. Dump, change one
setting in LMU's menu, dump again, diff. `requestPreset` follows the menu as you change it.
Dumps contain the in-game driver name; `lmu-probe/` is git-ignored.

## Every route the game knows

From the strings in `Le Mans Ultimate.exe` (build 14200). The routes in the sections above
are the ones tested; for the rest only the name is known.

<details>
<summary>178 routes</summary>

- **garage**: `/`, `PitMenu/loadPitMenu`, `PitMenu/receivePitMenu`, `SetCurrentVehicle`, `SetPreviewSaveFile`, `UIScreen/CarSetupOverview`, `UIScreen/CoopOverview`, `UIScreen/DriverHandOffStintEnd`, `UIScreen/DriverHandOffStintStart`, `UIScreen/PitCarReview`, `UIScreen/RepairAndRefuel`, `UIScreen/SessionSetup`, `UIScreen/TireManagement`, `brakeinfo`, `drive`, `getPlayerGarageData`, `getVehicleCondition`, `isRefreshInProgress`, `refreshSetups`, `setup`, `setup/compare`, `setup/default`, `setup/notes`, `showOnlyRelevantSetups`, `summary`, `tireinfo`, `toRaceMenu`
- **options**: `ApplyVideoOptions`, `UIScreen/Controls`, `assign/cancel`, `assign/changestatus`, `assign/confirm`, `commandline`, `display`, `float`, `getAllHapticsDevices`, `getAllResolutions`, `getAllSoundDevices`, `getLanguage`, `graphics/confirmgraphics`, `graphics/resetgraphics`, `liveInputs`, `long`, `occlusionCullingSupported`, `resetVRView`, `setConfigControl`, `setControls`, `setHapticsDevice`, `setInMenuGfxEffects`, `setInputAxisProperties`, `setSoundDevice`, `settings`, `unsetConfigControl`
- **race**: `car`, `car/{id}/image`, `getAllowedToStartRacing`, `startRace`, `track`, `track/{id}/trackmap`
- **sessions**: `/`, `Championship/getCurrentChampTemplate`, `Championship/getGrid`, `Championship/setCurrentChampionshipTemplate`, `Coop/setCoopDriverID`, `FFtoRaceEnd`, `GetGameState`, `GetSessionsInfoForEvent`, `MultiStintRace/Drive`, `MultiStintRace/UnPause`, `MultiStintRace/setDriverInfo`, `SaveLoad/compressSaveFile`, `SaveLoad/decompressSaveFile`, `SaveLoad/deleteSaveFile`, `SaveLoad/doesBackupExistForThisSession`, `SaveLoad/generateSaveFileFromSessionPreset`, `SaveLoad/getEveryLocalSave`, `SaveLoad/getNumSaves`, `SaveLoad/getSaveJSON`, `SaveLoad/isSaveNameValid`, `SaveLoad/loadGame`, `SaveLoad/saveGame`, `SaveLoad/saveLastBackup`, `SaveLoad/saveTemplateToFile`, `SessionPresets/applyPreset`, `SessionPresets/getDefaultPresetForTrack`, `SessionPresets/requestPreset`, `ai/TakeDriverControl`, `ai/forcePlayerVehAiPit`, `amount`, `clearEventNotification`, `continueGame`, `getAllAvailableVehicles`, `getAllVehicles`, `getTracksAll`, `getTracksInSeries`, `notifyInPauseSettings`, `opponents`, `opponents/all`, `playVOTrigger`, `playerSettings/backupPlayerSettings`, `playerSettings/restorePlayerSettingsFromBackup`, `raceControlVerification`, `restartStintAvailable`, `resumePitStop`, `returnToMonitor`, `saveload/getSaveFileJSONFromFilename`, `setEventNotification`, `setHudOnWatchScreen`, `settings`, `weather`, `weather/{session}/{node}/{setting}`, `weather/{session}/{preset}`, `{session}/sessions`
- **profile**: `/`, `DLC/viewDLC`, `eacActive`, `firstRun`, `getAuthSessionTicket`, `inDevMode`, `profileInfo/getProfileInfo`, `profileInfo/setProfileInfo`
- **multiplayer**: `cancelJoinRequest`, `exitVehicle`, `join`, `join/state`, `steam/status`, `takeControlOfVehicle`, `teams`
- **replay**: `CameraController/getCameraInfo`, `CameraController/setCamera`, `CameraController/setCameraByName/{cameraName}`, `isActive`, `toggleactive`
- **watch**: `focus`, `focus/{cameraType}/{trackSideGroup}/{shouldAdvance}`, `focus/{slotid}`, `focusBackward`, `focusForward`, `getBookmarkedTimestamps`, `getIncidentsList/{minTimeBetweenContacts}`, `play/{id}`, `replay/getReplayFolder`, `replay/setCurrentMetadata`, `replay/setReplayFolder`, `replay/setReplayUIVisible`, `replayCommand/{command}`, `replays`, `replaytime/{time}`, `sessionInfo`, `standings`, `standings/history`, `trackmap`
- **other**: `hud`, `hud/toggle/{component}`, `hud/toggleAllComponents/{visible}`, `liveryeditor/setCamera/{camera}`, `liveryeditor/showRegionTexture/{active}`, `liveryeditor/submitCustomSkin`, `materialeditor/...` (7), `chat/`, `cancelSteamAuth`, `start/openExternalBrowserToURL`, `strategy/overall`, `strategy/pitstop-estimate`, `strategy/usage`

All under `/rest/`.

</details>

`getAuthSessionTicket` and `cancelSteamAuth` touch the player's Steam login. Studio 397's
November 2026 statement was about tools using that login against their online services, so
leave those alone.
