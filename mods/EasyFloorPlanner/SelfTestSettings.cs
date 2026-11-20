#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Kitchen;
using Kitchen.Layouts;
using Unity.Entities;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Self-test steps for the settings and the editor changes that came with them (2026-11-19):
    /// the Mod List options page, Esc asking before it discards a draft, the box readout and the
    /// bigger box, the palettes, Apply keeping the editor open, remembering the mode and brush,
    /// host-only editing, the zoom-out limit, pan speed, and weather everywhere. Every setting is
    /// put back as it was (the player's own values, not the defaults).
    /// </summary>
    public partial class SelfTestDriver
    {
        private static Action _restoreSettings;

        // ---- the options page (contract with Mod List's options API version 1)

        private void StepSettingsOptions()
        {
            Check(typeof(ModListOptions).IsAbstract && typeof(ModListOptions).IsSealed && typeof(ModListOptions).IsPublic,
                "ModListOptions is a public static class (how Mod List finds it)");
            Check(typeof(ModListOptions).GetMethod("OnOptionsClosed", Type.EmptyTypes) != null, "ModListOptions.OnOptionsClosed exists (saves the settings)");
            List<Dictionary<string, object>> options = ModListOptions.GetOptions().Cast<Dictionary<string, object>>().ToList();
            var types = new HashSet<string> { "bool", "int", "choice", "button", "heading" };
            var ids = new HashSet<string>();
            var problems = new List<string>();
            foreach (Dictionary<string, object> o in options)
            {
                string type = o.TryGetValue("type", out object t) ? t as string : null;
                string label = o.TryGetValue("label", out object l) ? l as string : null;
                string id = o.TryGetValue("id", out object i) ? i as string : null;
                string desc = o.TryGetValue("description", out object d) ? d as string : null;
                string name = id ?? label ?? "?";
                if (type == null || !types.Contains(type))
                {
                    problems.Add($"{name}: type '{type}'");
                }
                if (string.IsNullOrEmpty(label) || label.Length > 30)
                {
                    problems.Add($"{name}: label length {label?.Length ?? 0}");
                }
                // Mod List shows descriptions in one or two lines; the menu font lacks | ; and bullets.
                if (desc != null && desc.Length > 90)
                {
                    problems.Add($"{name}: description {desc.Length} characters");
                }
                if ((label + desc).IndexOfAny(new[] { '|', ';', '•' }) >= 0)
                {
                    problems.Add($"{name}: a character the menu font doesn't have");
                }
                if (type == "heading")
                {
                    continue;
                }
                if (id == null || !ids.Add(id))
                {
                    problems.Add($"{name}: missing or repeated id");
                }
                try
                {
                    RoundTrip(o, type, name, problems);
                }
                catch (Exception e)
                {
                    problems.Add($"{name}: {e.GetType().Name} {e.Message}");
                }
            }
            Log($"options: {string.Join(", ", options.Select(o => $"{(o.TryGetValue("id", out object i) ? i : "#")}({o["type"]})"))}");
            Check(problems.Count == 0 && ids.Count >= 9, $"every option is well formed and its value round-trips ({ids.Count} options{(problems.Count > 0 ? "; " + string.Join("; ", problems) : "")})");
        }

        /// <summary>Sets each value an option allows and reads it back, then puts the original back.</summary>
        private static void RoundTrip(Dictionary<string, object> o, string type, string name, List<string> problems)
        {
            switch (type)
            {
                case "bool":
                {
                    var get = (Func<bool>)o["get"];
                    var set = (Action<bool>)o["set"];
                    bool was = get();
                    set(!was);
                    bool flipped = get() == !was;
                    set(was);
                    if (!flipped || get() != was)
                    {
                        problems.Add($"{name}: bool doesn't round-trip");
                    }
                    break;
                }
                case "int":
                {
                    var get = (Func<int>)o["get"];
                    var set = (Action<int>)o["set"];
                    int min = (int)o["min"], max = (int)o["max"], was = get();
                    set(min);
                    bool lo = get() == min;
                    set(max);
                    bool hi = get() == max;
                    set(was);
                    if (!lo || !hi || get() != was || was < min || was > max)
                    {
                        problems.Add($"{name}: int doesn't round-trip ({min}..{max}, was {was})");
                    }
                    break;
                }
                case "choice":
                {
                    var get = (Func<string>)o["get"];
                    var set = (Action<string>)o["set"];
                    var choices = ((IEnumerable)o["choices"]).Cast<string>().ToList();
                    if (o.TryGetValue("labels", out object labels) && ((IEnumerable)labels).Cast<string>().Count() != choices.Count)
                    {
                        problems.Add($"{name}: labels and choices differ in number");
                    }
                    string was = get();
                    if (!choices.Contains(was))
                    {
                        problems.Add($"{name}: current value '{was}' isn't a choice");
                    }
                    foreach (string c in choices)
                    {
                        set(c);
                        if (get() != c)
                        {
                            problems.Add($"{name}: choice '{c}' doesn't round-trip");
                        }
                    }
                    set(was);
                    break;
                }
                case "button":
                    if (!(o.TryGetValue("action", out object a) && a is Action))
                    {
                        problems.Add($"{name}: button without an action");
                    }
                    break;
            }
            foreach (string key in new[] { "enabled", "visible" })
            {
                if (o.TryGetValue(key, out object f) && f is Func<bool> check)
                {
                    check();
                }
            }
        }

        // ---- the editor with the new settings

        private static LayoutBlueprint _uxBefore;
        private static int _uxRoom;
        private static LayoutPosition _uxGrowA, _uxGrowB;

        private void StepUxOpen()
        {
            _restoreSettings = Settings.TestSnapshot();
            Settings.SetApplyKeepsOpen(true);
            Settings.SetRememberTools(true);
            Settings.SetPalette(Settings.Palette.Standard);
            List<Entity> players = AllWith<CPlayer>();
            FloorPlanService service = World.GetExistingSystem<FloorPlanService>();
            if (players.Count == 0 || service == null)
            {
                Fail("no player or no FloorPlanService to open the editor with");
                return;
            }
            _uxBefore = ReadLayout();
            service.RequestOpen(players[0]);
        }

        private FloorPlanView ReadyEditor(string what)
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view != null && view.TestReady)
            {
                return view;
            }
            if (StepElapsed < 10f)
            {
                Retry(0.5f);
                return null;
            }
            Fail($"the editor didn't open within 10 s ({what})");
            return null;
        }

        private void StepUxReadouts()
        {
            FloorPlanView view = ReadyEditor("settings");
            if (view == null)
            {
                return;
            }
            view.TestSetVisible(true);

            // The box being drawn shows its size (and says when it's too big).
            view.TestSetMode(0);
            var a = new LayoutPosition(0, 0);
            view.TestDragBox(a, a + new LayoutPosition(4, 2));
            string status = view.TestStatus;
            Check(status.Contains("5 x 3"), $"drawing a box shows its size ('{status}')");
            view.TestDragBox(a, a + new LayoutPosition(FloorPlanView.MaxBoxSide, 0));
            status = view.TestStatus;
            Check(status.Contains("too big") && status.Contains($"{FloorPlanView.MaxBoxSide} x {FloorPlanView.MaxBoxSide}"), $"a box past the limit says so ('{status}')");
            view.TestEndDrag();
            Check(FloorPlanView.MaxBoxSide == 2 * LayoutEdits.MaxExtent + 1, $"a box can span the largest restaurant ({FloorPlanView.MaxBoxSide} tiles, was 40)");

            // The controller help names every wall brush (the front door was missing).
            Check(FloorPlanView.TestHelpFor(walls: true, mouse: false).Contains("front door"), "the controller help in wall mode lists the front door brush");
            bool hideWas = Settings.HideControls;
            Settings.SetHideControls(false);
            view.TestEndDrag(); // refreshes the HUD
            bool shown = view.TestHelp.Length > 0;
            Settings.SetHideControls(true);
            view.TestEndDrag();
            bool hidden = view.TestHelp.Length == 0;
            Settings.SetHideControls(hideWas);
            Check(shown && hidden, "\"Hide controls legend\" hides the editor's control hints, and they show without it");

            // Pan speed scales the editor's WASD / stick panning.
            Vector3 start = FloorPlanCamera.TestAnchor;
            int speedWas = Settings.PanSpeed;
            Settings.SetPanSpeed(100);
            FloorPlanCamera.Pan(Vector2.right, 0.1f);
            float normal = (FloorPlanCamera.TestAnchor - start).magnitude;
            Settings.SetPanSpeed(200);
            Vector3 mid = FloorPlanCamera.TestAnchor;
            FloorPlanCamera.Pan(Vector2.right, 0.1f);
            float doubled = (FloorPlanCamera.TestAnchor - mid).magnitude;
            FloorPlanCamera.Move(start - FloorPlanCamera.TestAnchor);
            Settings.SetPanSpeed(speedWas);
            Check(normal > 0.5f && Mathf.Abs(doubled - 2f * normal) < 0.05f, $"pan speed 200% pans twice as far ({doubled:0.00} vs {normal:0.00} tiles)");
        }

        /// <summary>A box the size the old 40-tile limit refused, painted in one go and undone.</summary>
        private void StepUxBigBox()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view == null || !view.TestReady)
            {
                Fail("editor not open");
                return;
            }
            LayoutBlueprint live = ReadLayout();
            Bounds b = CurrentBounds();
            Room room = live.Tiles.Values.Where(r => LayoutHelpers.IsInside(r) && r.Type == RoomType.Dining).OrderBy(r => r.ID).FirstOrDefault();
            if (room.Type != RoomType.Dining || !view.TestSelectRoom(room.ID))
            {
                Fail("no dining room brush for the big box");
                return;
            }
            // Out from the back and both sides of the restaurant (the street side is refused anyway).
            int x0 = Mathf.RoundToInt(b.min.x) - 20, x1 = Mathf.RoundToInt(b.max.x) + 20, y0 = Mathf.RoundToInt(b.min.z) + 1, y1 = Mathf.RoundToInt(b.max.z) + 25;
            x0 = Mathf.Max(x0, -LayoutEdits.MaxExtent);
            x1 = Mathf.Min(x1, LayoutEdits.MaxExtent);
            y1 = Mathf.Min(y1, LayoutEdits.MaxExtent);
            int before = view.TestDraft.Tiles.Count(t => LayoutHelpers.IsInside(t.Value));
            var watch = Stopwatch.StartNew();
            view.TestPaintBox(new LayoutPosition(x0, y0), new LayoutPosition(x1, y1));
            watch.Stop();
            int after = view.TestDraft.Tiles.Count(t => LayoutHelpers.IsInside(t.Value));
            Log($"big box {x1 - x0 + 1} x {y1 - y0 + 1}: painted {after - before} tiles in {watch.ElapsedMilliseconds} ms ({view.TestMessage}).");
            Check(x1 - x0 + 1 > 40 && after - before > 1000, $"a {x1 - x0 + 1} x {y1 - y0 + 1} box (over the old 40) paints ({after - before} new tiles)");
            Check(watch.ElapsedMilliseconds < 1500, $"painting it takes under 1.5 s ({watch.ElapsedMilliseconds} ms)");
            view.TestUndo();
            Check(SameLayout(live, view.TestDraft, out string diff), $"one undo takes the big box back{(diff == null ? "" : ": " + diff)}");
        }

        /// <summary>Grows a room by two tiles, and shows the draft in both palettes.</summary>
        private void StepUxPalette()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view == null || !view.TestReady)
            {
                Fail("editor not open");
                return;
            }
            if (StepElapsed < 0.1f)
            {
                LayoutBlueprint live = ReadLayout();
                Dictionary<LayoutPosition, OccupantFlags> occupants = TileOccupants.Take(EntityManager);
                LayoutPosition[] dirs = { new LayoutPosition(0, 1), new LayoutPosition(-1, 0), new LayoutPosition(1, 0) };
                _uxRoom = 0;
                foreach (LayoutPosition d in dirs)
                {
                    foreach (LayoutPosition s in live.Tiles.Where(t => LayoutHelpers.IsInside(t.Value) && t.Value.Type == RoomType.Dining && !live.Tiles.ContainsKey(t.Key + d))
                                 .Select(t => t.Key).OrderBy(p => Math.Abs(p.x) + Math.Abs(p.y)))
                    {
                        LayoutPosition ga = s + d, gb = ga + d;
                        if (BlocksEdit(ga) || BlocksEdit(gb) || live.Tiles.ContainsKey(gb) || LayoutEdits.PaintTile(new LayoutBlueprint(live), ga, live[s], occupants) != null)
                        {
                            continue;
                        }
                        (_uxRoom, _uxGrowA, _uxGrowB) = (live[s].ID, ga, gb);
                        break;
                    }
                    if (_uxRoom != 0)
                    {
                        break;
                    }
                }
                if (_uxRoom == 0 || !view.TestSelectRoom(_uxRoom))
                {
                    Fail("no dining room edge with two free tiles for the settings test");
                    return;
                }
                view.TestPaint(_uxGrowA);
                view.TestPaint(_uxGrowB);
                Check(view.TestDraft[_uxGrowA].ID == _uxRoom && view.TestDraft[_uxGrowB].ID == _uxRoom, $"draft: room {_uxRoom} grew onto {Fmt(_uxGrowA)} and {Fmt(_uxGrowB)}");
                FloorPlanCamera.Move(new Vector3(_uxGrowA.x, 0f, _uxGrowA.y) - FloorPlanCamera.TestAnchor);
                Color std = view.TestOverlayColourOf(_uxGrowA);
                Settings.SetPalette(Settings.Palette.Colourblind);
                Color cb = view.TestOverlayColourOf(_uxGrowA);
                Settings.SetPalette(Settings.Palette.Standard);
                Check(std != cb, $"the palettes colour a dining tile differently ({std} / {cb})");
                Retry(1f);
                return;
            }
            if (Settings.EditorPalette == Settings.Palette.Standard)
            {
                Shot("ux-palette-standard");
                Settings.SetPalette(Settings.Palette.Colourblind);
                Retry(1f);
                return;
            }
            Shot("ux-palette-colourblind");
        }

        private void StepUxApplyKeepOpen()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view == null || !view.TestReady || _uxRoom == 0)
            {
                Fail("editor not open, or nothing drafted");
                return;
            }
            view.TestApply();
            Log($"applied with the editor kept open ({view.TestMessage}).");
        }

        private void StepUxVerifyKeptOpen()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view != null && view.TestAwaiting && StepElapsed < 8f)
            {
                Retry(0.25f);
                return;
            }
            LayoutBlueprint live = ReadLayout();
            bool applied = live.Tiles.TryGetValue(_uxGrowA, out Room ra) && ra.ID == _uxRoom && live.Tiles.TryGetValue(_uxGrowB, out Room rb) && rb.ID == _uxRoom;
            if (!applied && StepElapsed < 8f)
            {
                Retry(0.25f);
                return;
            }
            Check(applied, "Apply committed the draft");
            Check(AllWith<CFloorPlanSession>().Count == 1 && view != null && view.TestReady, "with \"Apply keeps the editor open\", the editor is still open after applying");
            Check(view != null && view.TestMessage == "Changes applied.", $"the editor says the changes were applied ('{view?.TestMessage}')");
            Check(view != null && !view.TestHasUnapplied && SameLayout(live, view.TestDraft, out _), "the draft is now the applied layout (nothing left to apply)");
            Shot("ux-applied-kept-open");
        }

        private void StepUxRevert()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view == null || !view.TestReady)
            {
                Fail("editor not open");
                return;
            }
            Check(view.TestSelectBrush("Outdoors (remove floor)"), "erase brush available");
            view.TestPaint(_uxGrowB);
            view.TestPaint(_uxGrowA);
            view.TestApply();
        }

        private void StepUxVerifyReverted()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            bool same = SameLayout(_uxBefore, ReadLayout(), out string diff);
            if ((!same || (view != null && view.TestAwaiting)) && StepElapsed < 8f)
            {
                Retry(0.25f);
                return;
            }
            Check(same, $"a second Apply in the same session put the layout back{(same ? "" : ": " + diff)}");
            if (view == null || !view.TestReady)
            {
                Fail("the editor closed after the second Apply");
                return;
            }
            // Nothing to apply: with the setting on, Apply doesn't close the editor either.
            view.TestApply();
            Check(view.TestReady && view.TestMessage.StartsWith("Nothing to apply"), $"Apply with nothing to apply keeps the editor open ('{view.TestMessage}')");

            // Esc with changes asks first; the second press discards.
            Check(view.TestSelectRoom(_uxRoom), "brush for the room again");
            view.TestPaint(_uxGrowA);
            Check(view.TestHasUnapplied, "the draft has a change");
            view.TestSetMode(1);
            view.TestSetEdgeBrush(1); // hatch: remembered for the next session
            view.TestCancel();
            Check(view.TestReady && view.TestMessage.Contains("again to discard"), $"the first Esc with changes only warns ('{view.TestMessage}')");
            view.TestCancel();
        }

        private void StepUxVerifyDiscarded()
        {
            if (AllWith<CFloorPlanSession>().Count > 0 && StepElapsed < 8f)
            {
                Retry(0.25f);
                return;
            }
            Check(AllWith<CFloorPlanSession>().Count == 0, "the second Esc closes the editor");
            Check(SameLayout(_uxBefore, ReadLayout(), out string diff), $"discarding left the layout alone{(diff == null ? "" : ": " + diff)}");
            ReopenEditor();
        }

        private void ReopenEditor()
        {
            List<Entity> players = AllWith<CPlayer>();
            World.GetExistingSystem<FloorPlanService>()?.RequestOpen(players[0]);
        }

        private void StepUxVerifyRemembered()
        {
            FloorPlanView view = ReadyEditor("remembered tools");
            if (view == null)
            {
                return;
            }
            Check(view.TestMode == 1 && view.TestBrushLabel.StartsWith("Hatch"), $"with \"Remember mode and brush\", the editor opens in wall mode with the hatch brush (mode {view.TestMode}, brush '{view.TestBrushLabel}')");
            Settings.SetRememberTools(false);
            view.TestCancel(); // nothing changed: closes at once
        }

        private void StepUxReopenDefault()
        {
            if (AllWith<CFloorPlanSession>().Count > 0 && StepElapsed < 8f)
            {
                Retry(0.25f);
                return;
            }
            Check(AllWith<CFloorPlanSession>().Count == 0, "Esc without changes closes at once");
            ReopenEditor();
        }

        private void StepUxVerifyDefault()
        {
            FloorPlanView view = ReadyEditor("default tools");
            if (view == null)
            {
                return;
            }
            Check(view.TestMode == 0, $"without the setting, the editor opens painting floor (mode {view.TestMode}, brush '{view.TestBrushLabel}')");
            view.TestCancel();
            _restoreSettings?.Invoke();
            _restoreSettings = null;
            Log($"settings restored: {Settings.Describe()}.");
        }

        // ---- the restaurant at the size limit, through the game's own camera

        /// <summary>
        /// User report 2026-11-19: at the limit, the back of the restaurant wasn't drawn outside the
        /// editor (the camera's far clip of 60). StepLimitGround missed it: its survey camera is
        /// its own orthographic one. This checks the game's camera: every corner of the restaurant
        /// that's on screen must be nearer than the far clip.
        /// </summary>
        private void StepLimitFarClip()
        {
            if (!_haveLimit)
            {
                return;
            }
            Camera cam = Camera.main;
            if (cam == null)
            {
                Fail("no main camera");
                return;
            }
            LayoutBlueprint bp = ReadLayout();
            var inside = bp.Tiles.Where(t => LayoutHelpers.IsInside(t.Value)).Select(t => t.Key).ToList();
            float x0 = inside.Min(p => p.x) - 0.5f, x1 = inside.Max(p => p.x) + 0.5f, z0 = inside.Min(p => p.y) - 0.5f, z1 = inside.Max(p => p.y) + 0.5f;
            float deepest = 0f;
            foreach (Vector3 c in new[] { new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0), new Vector3(x0, 0f, z1), new Vector3(x1, 0f, z1) })
            {
                Vector3 v = cam.WorldToViewportPoint(c);
                if (v.z > 0f && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f)
                {
                    deepest = Mathf.Max(deepest, v.z);
                }
            }
            if (deepest >= cam.farClipPlane && StepElapsed < 5f)
            {
                Retry(0.5f);
                return;
            }
            Log($"at the limit: far clip {cam.farClipPlane:0} (game's {PlayCamera.TestVanillaFar:0}), deepest restaurant corner on screen {deepest:0}.");
            _heightUnpanned = cam.transform.position.y;
            Check(deepest < cam.farClipPlane, $"the game's camera draws all of a restaurant at the size limit (deepest corner on screen {deepest:0}, far clip {cam.farClipPlane:0})");
            Shot("06g-limit-game-camera");
        }

        // ---- panning across a restaurant at the size limit (pan range grows with the restaurant)

        /// <summary>The camera's height before panning: panning must keep it (user report 2026-11-19, 0.4.1: panning to the back zoomed out).</summary>
        private static float _heightUnpanned;

        private void CheckPanKeepsHeight(string where)
        {
            float h = Camera.main != null ? Camera.main.transform.position.y : 0f;
            Check(Mathf.Abs(h - _heightUnpanned) < 0.25f, $"panning {where} keeps the camera's height, so the zoom doesn't change (height {h:0.00}, {_heightUnpanned:0.00} unpanned)");
        }

        private bool LimitEdgeOnScreen(bool back, out float y)
        {
            LayoutBlueprint bp = ReadLayout();
            var inside = bp.Tiles.Where(t => LayoutHelpers.IsInside(t.Value)).Select(t => t.Key).ToList();
            float z = back ? inside.Max(p => p.y) : inside.Min(p => p.y);
            float x = (inside.Min(p => p.x) + inside.Max(p => p.x)) / 2f;
            Vector3 v = Camera.main != null ? Camera.main.WorldToViewportPoint(new Vector3(x, 0f, z)) : new Vector3(-1f, -1f, -1f);
            y = v.y;
            return v.z > 0f && v.y > 0.05f && v.y < 0.95f;
        }

        private void StepLimitPanBack()
        {
            if (!_haveLimit)
            {
                return;
            }
            // Pan towards the back a screen at a time, as holding the stick does.
            if (!LimitEdgeOnScreen(true, out float y) && StepElapsed < 10f)
            {
                PlayCamera.TestSet(1f, PlayCamera.Pan + new Vector2(0f, 0.5f));
                Retry(0.5f);
                return;
            }
            Check(LimitEdgeOnScreen(true, out y), $"at the size limit, panning reaches the back of the restaurant (back edge at viewport y {y:0.00}, pan {PlayCamera.Pan.y:0.0} screens)");
            CheckPanKeepsHeight("to the back");
            Shot("06h-limit-pan-back");
        }

        private void StepLimitPanFront()
        {
            if (!_haveLimit)
            {
                return;
            }
            if (!LimitEdgeOnScreen(false, out float y) && StepElapsed < 15f)
            {
                PlayCamera.TestSet(1f, PlayCamera.Pan - new Vector2(0f, 0.5f));
                Retry(0.5f);
                return;
            }
            Check(LimitEdgeOnScreen(false, out y), $"and the front (front edge at viewport y {y:0.00}, pan {PlayCamera.Pan.y:0.0} screens)");
            CheckPanKeepsHeight("to the front");
            Shot("06i-limit-pan-front");
            PlayCamera.TestSet(1f, Vector2.zero);
        }

        // ---- the pause menu's "Floor Planner" (the request it sends; the button itself needs the menu)

        private void StepPauseRequest()
        {
            if (AllWith<CFloorPlanSession>().Count > 0 && StepElapsed < 8f)
            {
                Retry(0.25f);
                return;
            }
            List<Entity> players = AllWith<CPlayer>();
            int id = EntityManager.GetComponentData<CPlayer>(players[0]).ID;
            Controllers.InputSourceIdentifier.DefaultInputSource.MakeRequest(id, FloorPlanPauseButton.OpenRequest);
            Log($"sent the pause menu's floor plan request for player {id}.");
        }

        private void StepVerifyPauseRequest()
        {
            FloorPlanView view = ReadyEditor("pause menu request");
            if (view == null)
            {
                return;
            }
            Check(AllWith<CFloorPlanSession>().Count == 1, "the pause menu's request opened the editor on the host");
            view.TestCancel();
        }

        // ---- host-only editing

        private void StepHostOnly()
        {
            if (StepElapsed < 0.1f)
            {
                if (AllWith<CFloorPlanSession>().Count > 0)
                {
                    Retry(0.25f);
                    return;
                }
                _restoreSettings = Settings.TestSnapshot();
                Settings.SetEditors(Settings.WhoCanEdit.HostOnly);
                Retry(1f);
                return;
            }
            List<Entity> players = AllWith<CPlayer>();
            CPlayer host = EntityManager.GetComponentData<CPlayer>(players[0]);
            CPlayer other = host;
            other.InputSource = host.InputSource + 1;
            Check(FloorPlanService.MayEdit(host) && !FloorPlanService.MayEdit(other), "host only: the host's players may edit, other machines' players may not");
            Check(FloorPlanTerminalLabelPatch.LastLabel == FloorPlanIds.LabelHostOnly, $"host only: the terminal tells the clients (label {FloorPlanTerminalLabelPatch.LastLabel}, expected {FloorPlanIds.LabelHostOnly})");
            var title = HarmonyLib.AccessTools.Field(typeof(RerollBlueprintView), "Title");
            bool floorPlan = UnityEngine.Object.FindObjectsOfType<RerollBlueprintView>()
                .Any(v => title?.GetValue(v) is TMPro.TextMeshPro tmp && tmp.text == "Floor Plan");
            Check(floorPlan, "host only: the host still sees \"Floor Plan\" (clients see \"Host only\")");
            Settings.SetEditors(Settings.WhoCanEdit.Everyone);
            Check(FloorPlanService.MayEdit(other), "everyone: other machines' players may edit");
            _restoreSettings?.Invoke();
            _restoreSettings = null;
        }

        // ---- the zoom-out limit (after the play camera steps)

        private static float _fovNormalOut;

        private void StepZoomOutNormal()
        {
            if (StepElapsed < 0.1f)
            {
                _restoreSettings = Settings.TestSnapshot();
                Settings.SetZoomOut(Settings.ZoomOutLimit.Normal);
                PlayCamera.TestSet(0.1f, Vector2.zero); // clamps to the limit
                Retry(2f);
                return;
            }
            _fovNormalOut = Camera.main != null ? Camera.main.fieldOfView : 0f;
            Check(Mathf.Approximately(PlayCamera.Zoom, 0.6f), $"the normal zoom-out limit holds at 0.6 (zoom {PlayCamera.Zoom:0.00}, FOV {_fovNormalOut:0.0})");
            Shot("ux-zoom-out-normal");
        }

        private void StepZoomOutFurthest()
        {
            if (StepElapsed < 0.1f)
            {
                Settings.SetZoomOut(Settings.ZoomOutLimit.Furthest);
                PlayCamera.TestSet(0.1f, Vector2.zero);
            }
            float fov = Camera.main != null ? Camera.main.fieldOfView : 0f;
            if (fov < _fovNormalOut * 1.2f && StepElapsed < 5f)
            {
                Retry(0.5f);
                return;
            }
            Check(Mathf.Approximately(PlayCamera.Zoom, 0.4f) && fov > _fovNormalOut * 1.2f, $"\"Furthest\" zooms out further (zoom {PlayCamera.Zoom:0.00}, FOV {fov:0.0}, normal {_fovNormalOut:0.0})");
            Shot("ux-zoom-out-furthest");
            Settings.SetZoomOut(Settings.ZoomOutLimit.Normal);
        }

        private void StepZoomOutClamped()
        {
            float fov = Camera.main != null ? Camera.main.fieldOfView : 0f;
            if (Mathf.Abs(fov - _fovNormalOut) > _fovNormalOut * 0.03f && StepElapsed < 5f)
            {
                Retry(0.5f);
                return;
            }
            Check(Mathf.Approximately(PlayCamera.Zoom, 0.6f) && Mathf.Abs(fov - _fovNormalOut) <= _fovNormalOut * 0.03f,
                $"turning the limit back to normal pulls the view back in (zoom {PlayCamera.Zoom:0.00}, FOV {fov:0.0})");
            PlayCamera.TestSet(1f, Vector2.zero);
            _restoreSettings?.Invoke();
            _restoreSettings = null;
        }

        // ---- weather everywhere (while the weather test's copies are showing)

        private static int _weatherStage, _copiesSeen;

        private void StepWeatherSetting()
        {
            if (_rain == null || _rainCopies.Count == 0)
            {
                Log("INFO no weather copies to switch off; \"Weather everywhere\" not exercised.");
                return;
            }
            int showing = _rainCopies.Count(c => c != null && c.gameObject.activeSelf);
            switch (_weatherStage++)
            {
                case 0:
                    _restoreSettings = Settings.TestSnapshot();
                    Settings.SetWeatherEverywhere(true);
                    WeatherTiler.TestShowAll = true; // every copy, wherever the camera looks
                    Retry(0.5f);
                    return;
                case 1:
                    _copiesSeen = showing;
                    Settings.SetWeatherEverywhere(false);
                    Retry(0.5f);
                    return;
                case 2:
                    Check(_copiesSeen > 0 && showing == 0, $"\"Weather everywhere\" off hides EFP's weather copies ({showing} showing, {_copiesSeen} before)");
                    Settings.SetWeatherEverywhere(true);
                    Retry(0.5f);
                    return;
                default:
                    Check(showing == _copiesSeen, $"switching it back on shows them again ({showing} of {_copiesSeen})");
                    WeatherTiler.TestShowAll = false;
                    _weatherStage = 0;
                    _restoreSettings?.Invoke();
                    _restoreSettings = null;
                    return;
            }
        }

        // ---- the profiler button (late: it then measures the night and the test's day)

        private void StepProfilerButton()
        {
            if (StepElapsed < 0.1f)
            {
                if (SystemProfiler.Active)
                {
                    Log("INFO the profiler is already on (profile.flag); the button isn't exercised.");
                    return;
                }
                SystemProfiler.Request();
                Check(SystemProfiler.Requested, "the options button asks for the profiler");
                Retry(0.5f);
                return;
            }
            Check(SystemProfiler.Active && !SystemProfiler.Requested, "the profiler started on the next frame, from outside the ECS systems, without freezing");
        }
    }
}
#endif
