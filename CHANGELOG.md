# Changelog

## 0.3.1 (2026-11-19)

- **Smoother nights in very big restaurants:** at night the game re-checks every frame whether customers can walk to every chair. With hundreds of chairs that made building at night laggy. In restaurants with more than 64 chairs the mod now spreads those checks over a few frames, so a chair's tick or cross can take up to half a second to update after you move something. The check at the start of the day is still complete.
- **Seat counts over tables** at night are worked out from cached paths in very big restaurants (more than 64 seats), recomputed every 30 frames instead of every frame, so a count can take up to half a second to update. The start of the day still uses a full check.
- **Customers no longer teleport around in very big restaurants.** In restaurants bigger than the game's original area, the mod raises how much path-finding Unity does each frame (the game's default was too low for big restaurants with many customers, so customers waited for a path and the game then warped them).
- **Bigger restaurants:** the maximum size is now 63 tiles from the centre in any direction (was 30). Players on older versions can still play together; they just can't paint past 30.
- **No more black edges around very big restaurants:** in settings with grass or snow, the ground is extended to at least 40 tiles around the restaurant, matching the original ground exactly. Street settings (City, Turbo) keep their dark backdrop, as in the base game.
- **Lighting of far rooms** in big restaurants uses far fewer textures, and its work is spread over frames instead of a hitch four times a second.
- **Optional profiler** for performance reports: off unless a file named `profile.flag` is in the mod's folder when the game starts. It then writes to the game log which game systems take the most time during 30 s of night and 30 s of day. During the first day with customers it also measures customer movement for 90 s, temporarily switching the path-finding budget between the game's default and the raised one, then puts it back.
- **Fixed:** the game log reported version 0.2.0 for 0.3.0.

## 0.3.0 (2026-11-17)

- **Appliances no longer block floor edits.** You can repaint the floor under an appliance into another room, or move walls around it, and it stays where it is.
  - **Removing the floor under an appliance** is allowed with a warning: the game throws away appliances left outdoors at the start of the day, so the editor marks them in yellow. Painting the floor back under them saves them.
  - **Still refused:** removing the floor under appliances that can't be moved or thrown away, and building floor over things that can't be moved or belong outdoors.
  - **Wall-mounted appliances** keep at least one solid wall: an edit that takes away the last one (or puts a door or hatch in it) is refused. If their wall moves, they turn to face one that's left.
  - **Tables and chairs** follow the new walls: tables split by a new wall stop being one group, tables brought together in one room join up, and chairs left across a wall from their table are cleared (the game puts new ones down).
- **Compatibility:** the editor's network messages changed again, so every player needs the same version.
- **Camera controls and other mods' windows:** while a menu, popup or another mod's window has your input (for example Blueprint Shop's shop window), the camera ignores your mouse wheel, middle button and controller. Scrolling in such a window no longer zooms the camera behind it. In local co-op only the busy player's devices are ignored.

## 0.2.0 (2026-11-17)

- **Camera area:** a third editor mode. Drag a box to choose what the day camera frames, for example to leave out a wing you don't need to watch. It's saved with the run, stays put when you resize the restaurant, and can be reset to the whole restaurant.
- **Room for the player cards:** during play the restaurant is framed a little higher, so the cards at the bottom of the screen no longer cover the front row.
- **Personal zoom and pan while playing:**
  - mouse wheel and middle-drag;
  - on a controller, RB + right stick and the right stick;
  - middle click / right-stick click resets the view.
- **Follow mode** (F, or RB + right-stick click): while you're zoomed in, the view keeps your chef on screen. Off by default; the choice is remembered.
- **Camera Controls in the pause menu:** a panel like the Recipes one explains the camera controls, with pages for keyboard and mouse, controller, and the camera area.
- **Zoom in the editor:** Ctrl + mouse wheel, or RB + right stick.
- **Street settings (Turbo):** the neighbouring-buildings fix from 0.1.0 is now verified in-game.
- **Compatibility:** the editor's network messages changed, so every player still needs the same version of the mod.

## 0.1.0 (2026-11-15, first public release)

Reshape your restaurant at night.

- **Floor Plan terminal** at night, in front of the restaurant.
- **Draft editor:**
  - paint floor into existing rooms, new dining rooms or new kitchens, or remove floor (grow, shrink, move walls between rooms);
  - set any wall between two rooms to a wall, a serving hatch or a door, with holograms of drafted walls;
  - Shift+drag box painting, room/wall-type picking, and undo;
  - top-down camera that pans beyond the restaurant (WASD / left stick);
  - Apply rebuilds the restaurant once; Cancel changes nothing.
- **Rules:**
  - appliances block edits;
  - every floor tile must be reachable from the front door;
  - the front stays the front;
  - up to 30 tiles from the centre.
- **Watching:** other players can watch the editor's draft live.
- **Outdoor scenery** on or beside new floor is removed.
- **Street settings (such as Turbo):** neighbouring buildings, the street and the front border are refitted to the new size. *Not yet verified in-game.*
- **Night lighting** covers the whole restaurant, including beyond the base game's 32 x 32 tile lighting map.
- **Saves:** the game saves edited layouts itself; the mod adds nothing to save files.
- **Online play hasn't been tested yet.** Every player needs the mod.
