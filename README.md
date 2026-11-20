# Easy Floor Planner

A PlateUp! mod that lets you reshape your restaurant during the night: grow or shrink it, move the walls between rooms, add new kitchens and dining rooms, turn any internal wall into a serving hatch or a door, add doors to the outside, and move the front door.

## Installing

1. **Subscribe to Harmony** on the Steam Workshop (id `2898033283`). It's required.
2. **Download** `EasyFloorPlanner-<version>.zip` from the [Releases page](https://github.com/Zambie223/EasyFloorPlanner/releases).
3. **Unzip it into PlateUp's `Mods` folder,** so you end up with `...\PlateUp\PlateUp\Mods\EasyFloorPlanner\EasyFloorPlanner.dll`.
   - To find the folder in Steam: right-click PlateUp, then Manage, then Browse local files, and open the `PlateUp` folder.
   - Create the `Mods` folder if it doesn't exist.
   - Keep the `.pdb` next to the `.dll`: it adds line numbers to error reports.
4. **Recommended: install [Mod List](https://github.com/Zambie223/PlateUp-ModList) 0.2.0 or later** the same way. Easy Floor Planner works without it, but its [settings](#settings) are only shown through Mod List (see [Why Mod List?](#why-mod-list)).
5. **Start the game.** At night, a **Floor Plan** terminal appears in front of your restaurant.

**To uninstall,** delete the `Mods\EasyFloorPlanner` folder. Edited layouts stay as they are (see [Saves](#saves)).

## How it works

- **Opening the editor:** at night, a **Floor Plan** terminal stands outside, in front of your restaurant near the practice-mode pad. Press interact on it, or choose **Floor Planner** in the pause menu (at night), which works wherever you are. Only one player can edit at a time. The host can allow only their own players to edit (the **Who can edit** [setting](#settings)); other players' terminal then reads **Host only**, and they can still watch.
- **Watching:** while someone is editing, the terminal reads **Watch**. Press it to see their draft live on your screen: the same overlay, their cursor in their player colour, and their current brush. Watching is read-only; press Back (Esc on keyboard) to stop. Players on the editor's own screen (local co-op) already see the draft, so the terminal does nothing for them.
- **Drafting:** nothing changes until you apply. Your edits are drawn as a coloured overlay on the floor:
  - room colours: blue shades for kitchens, warm shades for dining rooms, purple for other rooms;
  - stronger colour where a tile changes, and red where floor is removed;
  - **orange** for floor that can't be reached from the front door (add a door);
  - green bars for doors and orange bars for hatches, brighter where they're new.
  - These are the standard colours. A **colourblind-friendly** palette is a [setting](#settings); the editor names its colours in its messages either way.
- **Applying:** press Apply and the restaurant is rebuilt once with all your changes, and the editor closes (or stays open, with the **Apply keeps the editor open** setting). Press Cancel and nothing changes; if you have changes that aren't applied, the editor asks you to press Cancel again first.

### Brushes (painting floor)

- **An existing room** (Kitchen, Dining room, ...): grows that room onto outdoor tiles, or moves the wall by taking tiles from a neighbouring room. Right-click a room (or press Grab on a controller) to pick it as your brush.
- **New dining room / New kitchen:** starts a separate room next to the building. It needs a door before you can apply.
- **Outdoors (remove floor):** shrinks the restaurant.

### Walls

Switch to wall mode to set the walls. It has its own brushes: **Wall**, **Hatch (serving window)**, **Door** and **Front door**. Click a wall (or drag along several) to make it the brush's type. The cursor shows the brush's colour (grey for wall, orange for hatch, green for door, blue for the front door), and right-clicking a wall picks up its type as the brush.

- **Doors to the outside:** the Door brush also works on the building's side and back walls (not the front wall, which keeps the front door, the queue and the setting's front decorations). Customers and chefs can both use them; customers still queue at the front door.
- **A yard round the building:** while the restaurant has a side or back door, chefs can step out into a 2-tile yard all round the building (the invisible fence that keeps chefs near the restaurant moves out to match, and in City and Turbo the neighbouring buildings stand back to leave an alley). Appliances still can't be put down outside. Scenery right outside a new door is cleared.
- **Moving the front door:** click a spot on the front wall with the Front door brush. The old spot becomes wall, and the queue, the nameplate, the reroll and practice terminals, the outside bin and the setting's front decorations (path, fence gap) follow. Nothing can stand on the tile just inside the new spot.

Every wall you've changed shows a hologram of its new state, drawn like a floor plan: a solid bar for a wall, a window for a hatch, and an opening with a swinging door for a door. The wall under your cursor shows a fainter hologram of what clicking it would make.

Removing a door is always allowed while drafting. If that cuts a room off, the room turns orange and Apply waits until there's a way in again (or until you remove the room).

### Camera area

A big or oddly shaped restaurant (say, a long wing of automation you don't need to watch) makes the game zoom out to fit everything. The third mode, **Camera area**, lets you choose what the day camera frames instead. Drag a box over the part you want to see; right click (Grab on a controller) goes back to the whole restaurant. The area is outlined in light blue, and applying it on its own doesn't rebuild anything.

- The area applies during the day. At night the game still shows the whole restaurant, so you can plan.
- It applies to every player, and it's saved with the run.
- It stays put when you resize the restaurant later. (The whole-restaurant setting follows every resize.)
- Players who walk outside the area are still kept on screen, as in the base game.

### Controls

| | Keyboard and mouse | Controller |
|---|---|---|
| Move the view | W A S D | Left stick |
| Point at a tile or wall | Mouse | The centre of the screen (move the view) |
| Paint floor / set a wall | Left click (drag to paint) | Interact (hold to paint) |
| Paint a filled box (up to 127 x 127; its size shows while you drag) | Shift + left drag (Esc while dragging cancels it) | |
| Pick the room (or wall type) under the cursor | Right click | Grab |
| Change brush (room, or wall / hatch / door) | Q / E or mouse wheel | Secondary 1 |
| Draw the camera area (camera area mode) | Left drag | Hold Interact and move the view |
| Floor → walls → camera area | Tab | Secondary 2 |
| Zoom | Ctrl + mouse wheel | Hold RB + right stick up/down |
| Undo | Ctrl+Z | Stop moving |
| Apply | Enter | Menu select |
| Cancel (press twice if you have changes) | Esc | Back |

Your chef stands still while the editor is open (or while you're watching), and the movement controls move the view instead, so you can build beyond what's on screen. The view also turns to look straight down, so walls don't hide the floor behind them. It goes back to normal when you close the editor. Other players keep playing.

## Camera while playing

The mod also helps with the view during normal play:

- **Room for the player cards:** the restaurant is framed a little higher, so the player cards at the bottom of the screen don't cover the front row.
- **Your own zoom and pan:** these only change your screen. In local co-op everyone shares one screen, so they change it for everyone there.

| | Keyboard and mouse | Controller |
|---|---|---|
| Zoom in / out | Mouse wheel | Hold RB + right stick up/down |
| Pan | Hold the middle mouse button and drag | Right stick |
| Reset the view | Middle click | Click the right stick |
| Follow my chef on / off | F | Hold RB + click the right stick |

At normal zoom you can pan a little (enough to see past the edges), and further when zoomed in. In a restaurant bigger than the screen you can always pan far enough to see every edge of it.

**Follow** keeps your chef (or all of this machine's chefs, in local co-op) away from the screen edges while you're zoomed in. It's off by default, and the setting is remembered. Turn it off when you want the view to stay where you put it.

How fast the camera zooms and pans, and how far out it can zoom, are [settings](#settings).

The controls are ignored while a menu, a popup or the pause screen is open, and while another mod's window (such as a shop) has your input, so scrolling in that window doesn't zoom the camera behind it. In local co-op only the busy player's devices are ignored. Leaving the restaurant resets the view.

You don't need to remember any of this: the pause menu has a **Camera Controls** button. It opens a panel like the Recipes one, starting on the page for your controller, and shows whether follow is on.

## Settings

The settings are in **Pause → Mods**, on the settings gear next to Easy Floor Planner. That screen comes from [Mod List](https://github.com/Zambie223/PlateUp-ModList) 0.2.0 or later. Each player has their own settings, kept on their own computer, except **Who can edit**, where the host's setting counts.

| Setting | Default | What it does |
|---|---|---|
| Follow your chef | Off | The same as F / RB + click the right stick (see [Camera while playing](#camera-while-playing)). |
| Zoom speed | 100% | How fast the mouse wheel and the controller zoom (25% to 300%). |
| Pan speed | 100% | How fast the controller pans, and how fast the editor's view moves. Dragging with the middle mouse button is unaffected. |
| Furthest zoom-out | Normal | Normal, Further or Furthest. Further out widens the camera's view, so the edges of the screen look a little stretched. |
| Editor colours | Standard | Standard, or Colourblind-friendly: colours chosen to stay distinct with red-green and blue-yellow colour blindness. Kitchens blue, dining rooms orange, unreachable floor white, removed floor black, doors orange, the front door blue, hatches black. |
| Remember mode and brush | Off | The editor opens with the mode and brush you last closed it with, instead of floor painting. |
| Hide controls legend | Off | Hides the two lines of control hints at the bottom of the editor, for when you know the controls. |
| Apply keeps the editor open | Off | Applying rebuilds the restaurant and leaves the editor open; Cancel closes it. |
| Who can edit | Everyone | Host only: only the players on the host's computer can open the editor. Everyone can still watch. |
| Weather everywhere | On | Rain and snow over all of a big restaurant. Off: only near the middle, as in the base game. |
| Profile next night and day | (button) | Writes which parts of the game cost the most frame time, over 30 seconds of night and 30 of day, to `Player.log`. Useful for performance reports. |

### Why Mod List?

Mod List is a separate mod, by the same author, that adds a **Mods** button to the pause menu listing the mods you have installed and their versions, with a settings page for each mod that has settings. We recommend it with Easy Floor Planner because:

- **It's the only way to change the settings in game.** Without it, every setting keeps its default (or the value you last chose with Mod List installed).
- **It shows which version you have.** Online, every player needs the same version of Easy Floor Planner; with Mod List, each player can check theirs from the pause menu.
- **It's optional on both sides.** Neither mod needs the other: Easy Floor Planner doesn't reference Mod List, and Mod List finds the settings by itself. You can add or remove it at any time.

## Rules

- **Appliances can stay where they are.** You can repaint the floor under an appliance into another room, or move walls around it, and it stays put. Table groups and chairs re-form to match the new walls.
  - **Removing the floor under an appliance** is allowed, but the game throws away anything left outdoors at the start of the day. The editor marks these appliances in yellow and warns you; move them back inside before the day starts (or paint the floor back under them).
  - **Some appliances can't go outdoors** (ones that can't be moved or thrown away): the floor under them can't be removed. Likewise, floor can't be built over things that can't be moved or belong outdoors. The tile flashes red.
  - **Wall-mounted appliances** need a solid wall beside them: an edit that would take away their last wall (or put a door or hatch in it) is refused. If their wall moves, they turn to face one that's left.
- **Outdoor scenery never blocks.** Trees, rocks, bushes and flowers on new floor, or right beside it (so tree tops don't hang over it), are removed when you apply.
- **Street settings (such as Turbo):** the neighbouring buildings, the street and the front border move to fit the new size, so new floor never ends up inside a neighbouring building.
- **Every floor tile must be reachable from the front door,** through rooms and doors (hatches don't count).
- **The front of the restaurant stays the front:** it can't grow toward the street. The front door can move along the front wall, but not to another side; other special doors (garage, greenhouse) can't be moved.
- **Rooms can be reached through doors to the outside too:** a room whose only way in is its own side door is fine, since people can walk round the building.
- **Maximum size:** up to 63 tiles from the centre in any direction (127 x 127 tiles).
  - In settings with grass or snow, the ground is extended under very large restaurants, matching the original, so there are no black edges. Street settings (City, Turbo) keep their dark backdrop.
  - The game's own day camera can't fit a restaurant much wider than about 45 tiles; use the camera zoom and pan (see Camera) or set a camera area.
- New rooms can be a kitchen or a dining room. Garden tiles can't be edited yet.

## Requirements

- The **Harmony** Workshop item (id `2898033283`).
- Recommended: **[Mod List](https://github.com/Zambie223/PlateUp-ModList) 0.2.0 or later**, for the [settings](#settings).
- **Online play: every player needs this mod, the same version.** Online play hasn't been tested yet.

## Compatibility

- **Blueprint Shop:** compatible from Blueprint Shop 0.2.1 (earlier versions would turn the Floor Plan terminal into a second shop).
- **Kitchen Designer Plus:** compatible. Both mods enlarge the area customers can walk in, and whichever is larger wins.
- Other mods that rewrite the restaurant's layout during a run will probably conflict.

## Saves

The edited layout is saved by the game itself, the same way the original layout is. So is the camera area: it's kept in the game's own camera markers. The mod adds nothing of its own to save files, so a run edited with the mod still loads without it, and the layout stays as you left it.

However, the mod also keeps very large restaurants working: it extends the area customers can walk in, and the point the game uses to check that chairs are reachable. Without the mod, a restaurant that was grown beyond the base game's usual size can have floor customers can't walk on, or chairs they never use. Small edits within the original footprint are unaffected.

## Known limitations

- The terminal reuses the reroll button's model, relabelled "Floor Plan", with drafting tools in place of its dice.
- Very large trees two tiles from new floor can still overhang its edge slightly.
- The game's night lighting only covers tiles up to 16 from the middle of the map; the mod lights anything further out itself.
- The editor's "reachable from the front door" check looks at rooms and doors, not furniture. A dining room whose only way in is through a kitchen full of counters passes the check, but customers can't get there. The game marks such chairs with a "?".
- Every apply rebuilds the restaurant's walls and walking paths, which can cause a brief hitch on very large restaurants.

## Troubleshooting

Logs go to `Player.log` in `%USERPROFILE%\AppData\LocalLow\It's Happening\PlateUp\`. Every line from this mod starts with `[Easy Floor Planner]`.

## Building from source

You only need this to work on the mod. To play, use the released files.

**Requirements:**
- .NET SDK 8 or newer. The mod targets `netstandard2.0`.
- PlateUp! installed through Steam.
- The **Harmony** Workshop item (id `2898033283`) subscribed, so its `0Harmony.dll` is on disk. The mod compiles against it and never ships its own copy.

**Game path:** the build looks for the game in the default Steam library. If yours is elsewhere, either:
- create `mods\Directory.Build.local.props` (git-ignored):
  ```xml
  <Project><PropertyGroup>
    <PlateUpGameDir>D:\SteamLibrary\steamapps\common\PlateUp\PlateUp</PlateUpGameDir>
  </PropertyGroup></Project>
  ```
- or set the `PLATEUP_GAME_DIR` environment variable.

**Build:** run `dotnet build mods/EasyFloorPlanner -c Release`.
- The DLL and PDB are copied to `<game>\Mods\EasyFloorPlanner\`.
- Add `-p:DeployMod=false` to build without copying.
- The game must be restarted to pick up a rebuilt DLL.

**Automated in-game test:** Debug builds contain a self-test, and `tools/Run-SelfTest.ps1` runs it hands-free. It needs AutoHotkey v2.
- It launches the game through Steam and gets through the menus.
- It starts a restaurant, then makes and checks layout edits.
- Add `-Setting Turbo` (or another setting's name) to start in that setting.
- **Saves are protected.** The mod blocks save writes while the test runs, and the script backs up your saves and checks they're unchanged.
- **Hands off:** don't use the PC while it runs, since keys go to the game window.
- Output goes to `_testruns\` (git-ignored).
- Release builds never contain the test.

**Never commit or redistribute game DLLs, Harmony, or decompiled game code.** Game references are compile-only (`Private=false`).

---

### Responsible AI use disclosure

This mod was written with the assistance of an AI coding tool (Claude, by Anthropic), under the direction of the author (Zambie). Its behavior is based on the game's own code, which was read to match how the base game builds and uses the restaurant layout, rather than on AI recall. Changes are tested in-game with an automated test before release, except where the changelog or the known limitations say otherwise.
