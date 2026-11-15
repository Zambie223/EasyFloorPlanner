# Easy Floor Planner

A PlateUp! mod that lets you reshape your restaurant during the night: grow or shrink it, move the walls between rooms, add new kitchens and dining rooms, and turn any internal wall into a serving hatch or a door.

## Installing

1. **Subscribe to Harmony** on the Steam Workshop (id `2898033283`). It's required.
2. **Download** `EasyFloorPlanner-<version>.zip` from the [Releases page](https://github.com/Zambie223/EasyFloorPlanner/releases).
3. **Unzip it into PlateUp's `Mods` folder,** so you end up with `...\PlateUp\PlateUp\Mods\EasyFloorPlanner\EasyFloorPlanner.dll`.
   - To find the folder in Steam: right-click PlateUp, then Manage, then Browse local files, and open the `PlateUp` folder.
   - Create the `Mods` folder if it doesn't exist.
   - Keep the `.pdb` next to the `.dll`: it adds line numbers to error reports.
4. **Start the game.** At night, a **Floor Plan** terminal appears in front of your restaurant.

**To uninstall,** delete the `Mods\EasyFloorPlanner` folder. Edited layouts stay as they are (see [Saves](#saves)).

## How it works

- **Opening the editor:** at night, a **Floor Plan** terminal stands outside, in front of your restaurant near the practice-mode pad. Press interact on it. Only one player can edit at a time.
- **Watching:** while someone is editing, the terminal reads **Watch**. Press it to see their draft live on your screen: the same overlay, their cursor in their player colour, and their current brush. Watching is read-only; press Back (Esc on keyboard) to stop. Players on the editor's own screen (local co-op) already see the draft, so the terminal does nothing for them.
- **Drafting:** nothing changes until you apply. Your edits are drawn as a coloured overlay on the floor:
  - room colours: blue shades for kitchens, warm shades for dining rooms, purple for other rooms;
  - stronger colour where a tile changes, and red where floor is removed;
  - **orange** for floor that can't be reached from the front door (add a door);
  - green bars for doors and orange bars for hatches, brighter where they're new.
- **Applying:** press Apply and the restaurant is rebuilt once with all your changes. Press Cancel and nothing changes.

### Brushes (painting floor)

- **An existing room** (Kitchen, Dining room, ...): grows that room onto outdoor tiles, or moves the wall by taking tiles from a neighbouring room. Right-click a room (or press Grab on a controller) to pick it as your brush.
- **New dining room / New kitchen:** starts a separate room next to the building. It needs a door before you can apply.
- **Outdoors (remove floor):** shrinks the restaurant.

### Walls

Switch to wall mode to set the walls between rooms. It has its own brushes: **Wall**, **Hatch (serving window)** and **Door**. Click a wall (or drag along several) to make it the brush's type. The cursor shows the brush's colour (grey for wall, orange for hatch, green for door), and right-clicking a wall picks up its type as the brush.

Every wall you've changed shows a hologram of its new state, drawn like a floor plan: a solid bar for a wall, a window for a hatch, and an opening with a swinging door for a door. The wall under your cursor shows a fainter hologram of what clicking it would make.

Removing a door is always allowed while drafting. If that cuts a room off, the room turns orange and Apply waits until there's a way in again (or until you remove the room).

### Controls

| | Keyboard and mouse | Controller |
|---|---|---|
| Move the view | W A S D | Left stick |
| Point at a tile or wall | Mouse | The centre of the screen (move the view) |
| Paint floor / set a wall | Left click (drag to paint) | Interact (hold to paint) |
| Paint a filled box | Shift + left drag (Esc while dragging cancels it) | |
| Pick the room (or wall type) under the cursor | Right click | Grab |
| Change brush (room, or wall / hatch / door) | Q / E or mouse wheel | Secondary 1 |
| Floor ↔ wall mode | Tab | Secondary 2 |
| Undo | Ctrl+Z | Stop moving |
| Apply | Enter | Menu select |
| Cancel | Esc | Back |

Your chef stands still while the editor is open (or while you're watching), and the movement controls move the view instead, so you can build beyond what's on screen. The view also turns to look straight down, so walls don't hide the floor behind them. It goes back to normal when you close the editor. Other players keep playing.

## Rules

- **Appliances block edits.** A tile with an appliance on it can't change; move the appliance first. The tile flashes red.
- **Outdoor scenery never blocks.** Trees, rocks, bushes and flowers on new floor, or right beside it (so tree tops don't hang over it), are removed when you apply.
- **Street settings (such as Turbo):** the neighbouring buildings, the street and the front border move to fit the new size, so new floor never ends up inside a neighbouring building.
- **Every floor tile must be reachable from the front door,** through rooms and doors (hatches don't count).
- **The front of the restaurant stays the front:** it can't grow toward the street. The front door and other special doors can't be moved.
- **Maximum size:** up to 30 tiles from the centre in any direction.
- New rooms can be a kitchen or a dining room. Garden tiles can't be edited yet.

## Requirements

- The **Harmony** Workshop item (id `2898033283`).
- **Online play: every player needs this mod, the same version.** Online play hasn't been tested yet.

## Compatibility

- **Blueprint Shop:** compatible from Blueprint Shop 0.2.1 (earlier versions would turn the Floor Plan terminal into a second shop).
- **Kitchen Designer Plus:** compatible. Both mods enlarge the area customers can walk in, and whichever is larger wins.
- Other mods that rewrite the restaurant's layout during a run will probably conflict.

## Saves

The edited layout is saved by the game itself, the same way the original layout is. The mod adds nothing of its own to save files, so a run edited with the mod still loads without it, and the layout stays as you left it.

However, the mod also keeps very large restaurants working: it extends the area customers can walk in, and the point the game uses to check that chairs are reachable. Without the mod, a restaurant that was grown beyond the base game's usual size can have floor customers can't walk on, or chairs they never use. Small edits within the original footprint are unaffected.

## Known limitations

- The terminal reuses the reroll button's model, relabelled "Floor Plan".
- Very large trees two tiles from new floor can still overhang its edge slightly.
- The game's night lighting only covers tiles up to 16 from the middle of the map; the mod lights anything further out itself.
- Rain and snow only fall within a fixed area around the original restaurant, so parts of the map built far out stay dry. (It never falls inside rooms.)
- The fix for street settings such as Turbo (neighbouring buildings moving to fit a grown restaurant) hasn't been verified in-game yet.
- Doors can only go between two indoor rooms (no side doors to the outside yet).
- The editor's "reachable from the front door" check looks at rooms and doors, not furniture. A dining room whose only way in is through a kitchen full of counters passes the check, but customers can't get there. The game marks such chairs with a "?".
- Every apply rebuilds the restaurant's walls and walking paths, which can cause a brief hitch on very large restaurants.

## Troubleshooting

Logs go to `Player.log` in `%USERPROFILE%\AppData\LocalLow\It's Happening\PlateUp\`. Every line from this mod starts with `[Easy Floor Planner]`.

---

### Responsible AI use disclosure

This mod was written with the assistance of an AI coding tool (Claude, by Anthropic), under the direction of the author (Zambie). Its behavior is based on the game's own code, which was read to match how the base game builds and uses the restaurant layout, rather than on AI recall. Changes are tested in-game with an automated test before release, except where the changelog or the known limitations say otherwise.
