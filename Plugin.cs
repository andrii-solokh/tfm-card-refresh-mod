using System.Collections.Generic;
using Aube;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using TFM.GameData;
using UI;
using UI.Cards;
using UI.Cards.Actions;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TfmCardRefresh
{
    // Fixes stale card-playability display in Terraforming Mars.
    //
    // BigCardPreview computes button-enabled + missing-requirement highlight in
    // HandleState(), which only runs on open / state transition. When a card
    // preview is already open and a new authoritative game state lands (e.g. your
    // turn begins), nothing re-runs HandleState, so the preview keeps the state it
    // was opened with until you close and reopen it.
    //
    // ExpandPlayerCardsPage already exposes the exact refresh the game uses
    // internally: UpdateBasedOnTutorialSelectableCards() -> for each open preview
    // -> UpdateTutorialSelectableState() -> HandleState(m_CurrentState).
    //
    // This plugin polls a cheap committed-state signature (current player +
    // applied-event count) while the page is open and calls that refresh only when
    // the signature changes. Because it keys off committed game state, it does not
    // fire while you are locally configuring an unsubmitted pay/discount panel, so
    // it never stomps in-progress input. It is display-only: PlayCard() re-validates
    // at press time and the server is authoritative, so no move state is altered.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class TfmCardRefreshPlugin : BaseUnityPlugin
    {
        // GUID kept stable across the rename (it keys the BepInEx config file, so
        // changing it would orphan everyone's saved settings).
        public const string PluginGuid = "com.experiment.tfm.cardrefresh";
        public const string PluginName = "Terraforming Mars: Mission Control";
        public const string PluginVersion = "1.5.5";

        private const float PollIntervalSeconds = 0.25f;

        private float _elapsed;
        private float _targetsElapsed;
        private EPage _lastBadgePage = (EPage)(-1);
        private KeyCode _repeatKey = KeyCode.None;
        private float _repeatTimer;
        private const float RepeatInitialDelay = 0.35f;
        private const float RepeatInterval = 0.06f;
        private int _lastPlayer = int.MinValue;
        private int _lastEvents = int.MinValue;
        private bool _prevIsMyTurn;
        private bool _reopenHandAfterPass;
        private bool _showOverlay;
        private bool _showScoreboard;

        // Prepared-action queue: atomic actions the player lines up ahead of time; the
        // mod auto-fires them (in order, as far as actions/resources allow) the moment
        // it becomes their action turn. Always available; inert while the queue is empty.
        // Items are added from the fixed panel list (below) or from the on-screen element
        // itself with the add-key (standard projects / milestones / awards).
        private enum PreparedKind { Action, Conversion, Card, CardAction, Pass }
        private struct PreparedItem
        {
            public PreparedKind kind;
            public EActionType actionType; // Action: StandardProject / Milestone / Award
            public int arg;                // Action: (int) element type; Card: card id
            public EResourceType res;      // Conversion
            public string label;

            public bool IsPass { get { return kind == PreparedKind.Pass; } }
            public bool Same(PreparedItem o)
            {
                return kind == o.kind && actionType == o.actionType && arg == o.arg && res == o.res;
            }
            public static PreparedItem MakeAction(EActionType t, int a, string lbl)
            {
                return new PreparedItem { kind = PreparedKind.Action, actionType = t, arg = a, label = lbl };
            }
            public static PreparedItem MakeConversion(EResourceType r, string lbl)
            {
                return new PreparedItem { kind = PreparedKind.Conversion, res = r, label = lbl };
            }
            public static PreparedItem MakeCard(int cardId, string lbl)
            {
                return new PreparedItem { kind = PreparedKind.Card, arg = cardId, label = lbl };
            }
            public static PreparedItem MakeCardAction(int cardId, string lbl)
            {
                return new PreparedItem { kind = PreparedKind.CardAction, arg = cardId, label = lbl };
            }
            public static PreparedItem MakePass()
            {
                return new PreparedItem { kind = PreparedKind.Pass, label = "Pass / skip turn  (always last)" };
            }
        }
        private readonly List<PreparedItem> _prepareQueue = new List<PreparedItem>();
        private bool _showPrepare;
        private bool _fireAwaitConfirm;
        private float _fireCooldown;
        private float _fireConfirmElapsed;
        private float _cardNavElapsed;

        // ---- Config: rebindable keys (edit BepInEx/config/<guid>.cfg) ----
        internal static ConfigEntry<KeyCode> KeyProjects, KeyActions, KeyResources, KeyVictoryPoints,
            KeyEffects, KeyTags, KeyGreenery, KeyTemperature, KeySell, KeyBoard, KeyConfirm,
            KeyNavUp, KeyNavDown, KeyNavLeft, KeyNavRight, KeyOverlay,
            KeyMilestones, KeyStandardProjects, KeyAwards, KeyScoreboard, KeyPeek, KeyPrepare, KeyPrepareAdd,
            KeyPrepareSkip;

        // ---- Config: feature toggles ----
        internal static ConfigEntry<bool> FeatCardRefresh, FeatHandReadable, FeatAutoOpenHand, FeatKeepHandOpen,
            FeatSuppressAnnouncements, FeatPlayabilityDim, FeatActionAvailability, FeatActionSort, FeatHotkeys,
            FeatAutoMaxPayment, FeatIndicator, FeatTurnSound, FeatScoreboard, FeatStayFocused;

        // The sound played when your action turn begins (a game SFX id).
        internal static ConfigEntry<string> TurnStartSound;

        internal static bool On(ConfigEntry<bool> e)
        {
            return e == null || e.Value;
        }

        private static KeyCode Key(ConfigEntry<KeyCode> e, KeyCode fallback)
        {
            return e == null ? fallback : e.Value;
        }

        private void BindConfig()
        {
            KeyProjects = Config.Bind("Keys", "Projects", KeyCode.P, "Open/close your hand (projects)");
            KeyActions = Config.Bind("Keys", "Actions", KeyCode.A, "Open the card Actions popup");
            KeyResources = Config.Bind("Keys", "Resources", KeyCode.R, "Open the Card Resources popup");
            KeyVictoryPoints = Config.Bind("Keys", "VictoryPoints", KeyCode.V, "Open the Victory Points popup");
            KeyEffects = Config.Bind("Keys", "Effects", KeyCode.E, "Open the Effects popup");
            KeyTags = Config.Bind("Keys", "Tags", KeyCode.T, "Open the Card Tags popup");
            KeyGreenery = Config.Bind("Keys", "ConvertGreenery", KeyCode.G, "Convert plants to a greenery");
            KeyTemperature = Config.Bind("Keys", "ConvertTemperature", KeyCode.F, "Convert heat to raise temperature");
            KeySell = Config.Bind("Keys", "Sell", KeyCode.S, "Sell cards (Sell Patents)");
            KeyBoard = Config.Bind("Keys", "BoardView", KeyCode.B, "Toggle View state / Return (inspect board)");
            KeyConfirm = Config.Bind("Keys", "Confirm", KeyCode.Space, "Confirm the default button of the open dialog/card");
            KeyNavUp = Config.Bind("Keys", "NavigateUp", KeyCode.UpArrow, "Move selection up in a choice list");
            KeyNavDown = Config.Bind("Keys", "NavigateDown", KeyCode.DownArrow, "Move selection down in a choice list");
            KeyNavLeft = Config.Bind("Keys", "NavigateLeft", KeyCode.LeftArrow, "Previous card page / left in a choice list");
            KeyNavRight = Config.Bind("Keys", "NavigateRight", KeyCode.RightArrow, "Next card page / right in a choice list");
            KeyOverlay = Config.Bind("Keys", "Overlay", KeyCode.H, "Toggle the on-screen hotkey overlay");
            KeyMilestones = Config.Bind("Keys", "Milestones", KeyCode.M, "Open/close the Milestones tab");
            KeyStandardProjects = Config.Bind("Keys", "StandardProjects", KeyCode.K, "Open/close the Standard Projects tab");
            KeyAwards = Config.Bind("Keys", "Awards", KeyCode.W, "Open/close the Awards tab");
            KeyScoreboard = Config.Bind("Keys", "Scoreboard", KeyCode.Tab, "Toggle the live scoreboard (current VP from all sources)");
            KeyPeek = Config.Bind("Keys", "Highlight", KeyCode.LeftAlt, "Hold to reveal the badges/hints AND play the highlighted card/row (Alt+1-4/Q W E R). Left or Right variant both work; try LeftControl if you prefer");
            KeyPrepare = Config.Bind("Keys", "Prepare", KeyCode.Q, "Open/close the Prepare queue panel; while open, 1-9 add a fixed action, Backspace undo, Delete clear");
            KeyPrepareAdd = Config.Bind("Keys", "PrepareAdd", KeyCode.LeftShift, "Hold + a badge to ADD that standard project / milestone / award to the Prepare queue instead of doing it now (Left or Right Shift)");
            KeyPrepareSkip = Config.Bind("Keys", "PrepareSkip", KeyCode.Z, "Toggle Pass / skip in the Prepare queue (always fires last)");

            FeatHotkeys = Config.Bind("Features", "Hotkeys", true, "Master switch for all keyboard shortcuts");
            FeatCardRefresh = Config.Bind("Features", "CardRefresh", true, "Re-check card playability when game state changes");
            FeatHandReadable = Config.Bind("Features", "HandReadableOffTurn", true, "Let you open your hand during opponents' turns");
            FeatAutoOpenHand = Config.Bind("Features", "AutoOpenHandAfterPlay", true, "Reopen hand/actions after you play or pass");
            FeatKeepHandOpen = Config.Bind("Features", "KeepHandOpenOffTurn", true, "Keep the hand open across passing (suppress auto-close)");
            FeatSuppressAnnouncements = Config.Bind("Features", "SuppressAnnouncements", true, "Hide turn/phase announcement banners");
            FeatPlayabilityDim = Config.Bind("Features", "DimUnplayableInHandView", true, "Dim requirement-locked cards in the off-turn hand view and the buy/draft picker");
            FeatActionAvailability = Config.Bind("Features", "ShowActionAvailabilityOffTurn", true, "Show which card actions are usable during opponents' turns");
            FeatActionSort = Config.Bind("Features", "SortUsableActionsFirst", true, "Sort usable actions to the top of the actions popup");
            FeatAutoMaxPayment = Config.Bind("Features", "AutoMaxSteelTitaniumPayment", true, "When playing a card, pre-fill steel/titanium to cover the cost (not more)");
            FeatIndicator = Config.Bind("Features", "ShowRunningIndicator", true, "Show a small always-on 'mod running' marker in the corner");
            FeatTurnSound = Config.Bind("Features", "PlayTurnStartSound", true, "Play a sound when your action turn begins (announcements stay hidden)");
            FeatScoreboard = Config.Bind("Features", "Scoreboard", true, "Enable the live scoreboard panel (current VP breakdown for all players)");
            FeatStayFocused = Config.Bind("Features", "StayFocusedOnMyBoard", true, "Offline (vs bots): keep the view on your own board instead of following each bot's turn");
            TurnStartSound = Config.Bind("Features", "TurnStartSound", "SFX_OTHER_PLAYER_TURN", "Sound id for the turn-start ping (e.g. SFX_OTHER_PLAYER_TURN, SFX_MENU_CONFIRM, SFX_POPUP_OPEN)");
        }

        private void Awake()
        {
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded.");
            BindConfig();
            try
            {
                Harmony harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(TfmCardRefreshPlugin).Assembly);
                Logger.LogInfo("Harmony patches applied (keep hand readable during opponent turns).");
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("Harmony patch failed (hand-readable feature disabled): " + e.Message);
            }
        }

        private static GUIStyle s_numberStyle;
        private static GUIStyle s_indicatorStyle;
        private static GUIStyle s_keyHintStyle;
        private static GUIStyle s_prepareStyle;

        // Chip backing for the badges/hints: a dark translucent fill with a
        // mint border, so the letters read against busy card art.
        private static Texture2D s_chipFill;
        private static Texture2D s_chipBorder;

        private static Texture2D SolidTexture(Color c)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // Draw a filled, bordered chip in the given screen rect (IMGUI space).
        private static void DrawChip(Rect box)
        {
            if (s_chipFill == null)
            {
                s_chipFill = SolidTexture(new Color(0.03f, 0.09f, 0.11f, 0.86f));
                s_chipBorder = SolidTexture(new Color(0.05f, 0.95f, 0.58f, 0.95f)); // brand mint
            }
            const float b = 2f;
            GUI.DrawTexture(box, s_chipFill);
            GUI.DrawTexture(new Rect(box.x, box.y, box.width, b), s_chipBorder);
            GUI.DrawTexture(new Rect(box.x, box.yMax - b, box.width, b), s_chipBorder);
            GUI.DrawTexture(new Rect(box.x, box.y, b, box.height), s_chipBorder);
            GUI.DrawTexture(new Rect(box.xMax - b, box.y, b, box.height), s_chipBorder);
        }

        private void OnGUI()
        {
            GUI.skin.label.richText = true;
            DrawRunningIndicator();
            DrawNumberBadges();
            DrawKeyHints();
            DrawScoreboard();
            DrawPreparePanel();

            if (!_showOverlay)
            {
                return;
            }
            // Panels/actions are bare keys; hold the highlight key (Alt) to reveal the
            // badges and play the highlighted card/row.
            (ConfigEntry<KeyCode> key, KeyCode fallback, string label)[] rows =
            {
                (KeyProjects, KeyCode.P, "Projects (hand)"),
                (KeyActions, KeyCode.A, "Actions"),
                (KeyResources, KeyCode.R, "Resources"),
                (KeyVictoryPoints, KeyCode.V, "Victory points"),
                (KeyEffects, KeyCode.E, "Effects"),
                (KeyTags, KeyCode.T, "Tags"),
                (KeyMilestones, KeyCode.M, "Milestones"),
                (KeyStandardProjects, KeyCode.K, "Standard projects"),
                (KeyAwards, KeyCode.W, "Awards"),
                (KeySell, KeyCode.S, "Sell cards"),
                (KeyBoard, KeyCode.B, "View state / board"),
                (KeyOverlay, KeyCode.H, "This overlay"),
                (KeyGreenery, KeyCode.G, "Plants → greenery"),
                (KeyTemperature, KeyCode.F, "Heat → temperature"),
            };
            int lines = 11 + rows.Length; // title + fixed rows + per-key rows
            float height = 34f + lines * 20f;
            GUILayout.BeginArea(new Rect(12f, 12f, 360f, height), GUI.skin.box);
            string peek = Key(KeyPeek, KeyCode.LeftAlt).ToString();
            GUILayout.Label("<b>Mission Control — shortcuts</b>   (" + Key(KeyOverlay, KeyCode.H) + " to hide)");
            GUILayout.Label("  " + "Space".PadRight(12) + "Confirm (dialog / card)");
            GUILayout.Label("  " + "Esc".PadRight(12) + "Cancel / No / close window");
            GUILayout.Label("  " + Key(KeyPrepareSkip, KeyCode.Z).ToString().PadRight(12) + "Skip / pass turn");
            GUILayout.Label("  " + "↑ / ↓ / ← / →".PadRight(12) + "Navigate a list / pages");
            GUILayout.Label("  " + Key(KeyScoreboard, KeyCode.Tab).ToString().PadRight(12) + "Scoreboard");
            GUILayout.Label("  " + "1-5".PadRight(12) + "Focus player (1 = you)");
            GUILayout.Label("  " + Key(KeyPrepare, KeyCode.Q).ToString().PadRight(12) + "Prepare queue (auto-plays at your turn)");
            GUILayout.Label("  <b>hold " + peek + " (highlight) then:</b>");
            GUILayout.Label("  " + (peek + "+1-4 Q W E R").PadRight(16) + "Play / use item");
            GUILayout.Label("  " + (peek + "+5-9").PadRight(16) + "Sort tabs (in hand)");
            foreach ((ConfigEntry<KeyCode> key, KeyCode fallback, string label) in rows)
            {
                GUILayout.Label("  " + Key(key, fallback).ToString().PadRight(12) + label);
            }
            GUILayout.EndArea();
        }

        // Draw a big white number at the LEFT edge of each current clickable item,
        // but only while the highlight key is held (badges are display-only now). Uses
        // the item's world corners so it works regardless of pivot; Y is flipped for
        // IMGUI's top-left origin (screen-space-overlay canvas).
        private static readonly Vector3[] s_corners = new Vector3[4];

        // Tiny always-on marker so you can tell at a glance the mod is active. One
        // GUI.Label, no scene work, so it has no measurable FPS cost.
        private void DrawRunningIndicator()
        {
            if (!On(FeatIndicator))
            {
                return;
            }
            if (s_indicatorStyle == null)
            {
                s_indicatorStyle = new GUIStyle
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.LowerLeft,
                };
                s_indicatorStyle.normal.textColor = new Color(0.10f, 0.95f, 0.62f, 0.65f);
            }
            GUI.Label(new Rect(10f, Screen.height - 26f, 340f, 20f), "Mission Control v" + PluginVersion + " active", s_indicatorStyle);
        }

        private void DrawNumberBadges()
        {
            if ((_targets.Count == 0 && _sortTargets.Count == 0) || (!PeekHeld() && !AddHeld()))
            {
                return;
            }
            if (s_numberStyle == null)
            {
                s_numberStyle = new GUIStyle
                {
                    fontSize = 28,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
                s_numberStyle.normal.textColor = Color.white;
            }
            for (int i = 0; i < _targets.Count; i++)
            {
                RectTransform rt = _targets[i].t as RectTransform;
                if (rt == null)
                {
                    continue;
                }
                rt.GetWorldCorners(s_corners); // 0=BL 1=TL 2=TR 3=BR, screen px for overlay
                // Anchored near the card's upper-left, nudged up and left of the old
                // mid-left spot so the chip clears the card art.
                float leftX = s_corners[0].x - 6f;
                float centerY = (s_corners[0].y + s_corners[1].y) * 0.5f;
                Rect chip = new Rect(leftX, (Screen.height - centerY) - 34f, 38f, 38f);
                DrawChip(chip);
                GUI.Label(chip, _targets[i].label, s_numberStyle);
            }
            // Sort/filter tabs (5-9): centre a badge over each horizontal tab.
            for (int i = 0; i < _sortTargets.Count && i < s_sortKeys.Length; i++)
            {
                RectTransform rt = _sortTargets[i].t as RectTransform;
                if (rt == null)
                {
                    continue;
                }
                rt.GetWorldCorners(s_corners);
                float centerX = (s_corners[0].x + s_corners[2].x) * 0.5f;
                float centerY = (s_corners[0].y + s_corners[1].y) * 0.5f;
                Rect chip = new Rect(centerX - 17f, (Screen.height - centerY) - 17f, 34f, 34f);
                DrawChip(chip);
                GUI.Label(chip, s_sortKeyLabels[i], s_numberStyle);
            }
        }

        // Draw the key letter centred over each hinted button while the highlight key is held,
        // so the panel shortcuts (Milestones / Standard Projects / Awards) are
        // discoverable without memorising them.
        private void DrawKeyHints()
        {
            if (_keyHints.Count == 0 || (!PeekHeld() && !AddHeld()))
            {
                return;
            }
            if (s_keyHintStyle == null)
            {
                s_keyHintStyle = new GUIStyle
                {
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
                s_keyHintStyle.normal.textColor = new Color(0.05f, 0.95f, 0.58f, 1f); // brand mint
            }
            foreach ((Transform t, string label) in _keyHints)
            {
                RectTransform rt = t as RectTransform;
                if (rt == null)
                {
                    continue;
                }
                rt.GetWorldCorners(s_corners);
                float centerX = (s_corners[0].x + s_corners[2].x) * 0.5f;
                float centerY = (s_corners[0].y + s_corners[1].y) * 0.5f;
                Vector2 size = s_keyHintStyle.CalcSize(new GUIContent(label));
                float w = Mathf.Max(size.x + 16f, 30f);
                float h = Mathf.Max(size.y + 8f, 28f);
                Rect chip = new Rect(centerX - w * 0.5f, (Screen.height - centerY) - h * 0.5f, w, h);
                DrawChip(chip);
                GUI.Label(chip, label, s_keyHintStyle);
            }
        }

        // One player's live score, split by source (matches the game's own
        // scoreboard columns). Recomputed each frame the panel is open.
        private struct ScoreRow
        {
            public string name;
            public bool isMe;
            public int tr, awards, milestones, greenery, city, cardVp, total;
        }

        private static readonly string[] s_scoreHeaders =
            { "Player", "TR", "Award", "Mstn", "Grn", "City", "Card", "TOTAL" };
        private static readonly float[] s_scoreColW =
            { 176f, 58f, 68f, 64f, 56f, 56f, 60f, 78f };
        private static GUIStyle s_scoreTitleStyle;
        private static GUIStyle s_scoreCellStyle;

        // The live "if the game ended now" scoreboard. Reimplements the game's
        // ScoreManager.CalculatePlayerScore math (TR + greeneries + cities +
        // milestones + awards + card VP) WITHOUT its achievement/save-data side
        // effects, which fire when the real method runs on the human player.
        private void DrawScoreboard()
        {
            if (!_showScoreboard || !On(FeatScoreboard))
            {
                return;
            }
            List<ScoreRow> rows = ComputeScores();
            if (rows.Count == 0)
            {
                return;
            }
            if (s_scoreCellStyle == null)
            {
                s_scoreCellStyle = new GUIStyle { fontSize = 19, richText = true, alignment = TextAnchor.MiddleLeft };
                s_scoreCellStyle.normal.textColor = Color.white;
                s_scoreCellStyle.padding = new RectOffset(2, 2, 2, 2);
                s_scoreTitleStyle = new GUIStyle { fontSize = 22, fontStyle = FontStyle.Bold, richText = true };
                s_scoreTitleStyle.normal.textColor = new Color(0.05f, 0.95f, 0.58f, 1f);
            }
            float width = 28f;
            for (int i = 0; i < s_scoreColW.Length; i++)
            {
                width += s_scoreColW[i];
            }
            const float rowH = 28f;
            float height = 58f + (rows.Count + 1) * rowH;
            float x = Screen.width - width - 14f; // top-right
            GUILayout.BeginArea(new Rect(x, 12f, width, height), GUI.skin.box);
            GUILayout.Label("SCOREBOARD  (live, " + Key(KeyScoreboard, KeyCode.Tab) + " to hide)", s_scoreTitleStyle);
            GUILayout.Space(4f);
            DrawScoreCells(s_scoreHeaders, bold: true);
            foreach (ScoreRow r in rows)
            {
                string[] cells =
                {
                    (r.isMe ? "▸ " : "") + r.name,
                    r.tr.ToString(), r.awards.ToString(), r.milestones.ToString(),
                    r.greenery.ToString(), r.city.ToString(), r.cardVp.ToString(), r.total.ToString(),
                };
                DrawScoreCells(cells, bold: r.isMe);
            }
            GUILayout.EndArea();
        }

        private static void DrawScoreCells(string[] cells, bool bold)
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < cells.Length && i < s_scoreColW.Length; i++)
            {
                string text = bold ? "<b>" + cells[i] + "</b>" : cells[i];
                GUILayout.Label(text, s_scoreCellStyle, GUILayout.Width(s_scoreColW[i]));
            }
            GUILayout.EndHorizontal();
        }

        private List<ScoreRow> ComputeScores()
        {
            List<ScoreRow> rows = new List<ScoreRow>();
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return rows;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                TM_GameData data = (game != null) ? game.GameData : null;
                TM_GameInfo info = (game != null) ? game.GameInfo : null;
                if (data == null || info == null || data.PlayerBoardData == null)
                {
                    return rows;
                }
                TM_GameBoardData board = data.BoardData;
                bool solo = info.IsSoloModeGame();
                int myId = MyStableLocalId();
                List<int> playerIds = new List<int>(data.PlayerBoardData.Keys);
                Dictionary<int, int> awardTotals = solo ? null : ComputeAwardScores(game, data, playerIds);

                foreach (KeyValuePair<int, TM_PlayerBoardData> kv in data.PlayerBoardData)
                {
                    TM_PlayerBoardData pbd = kv.Value;
                    if (pbd == null)
                    {
                        continue;
                    }
                    int pid = kv.Key;
                    int tr = pbd.TerraformingRating;
                    int city = CitiesScore(board, pid);
                    int green = GreeneriesScore(board, pid);
                    int mile = MilestonesScore(data, pid);
                    int awards = (awardTotals != null && awardTotals.ContainsKey(pid)) ? awardTotals[pid] : 0;
                    int cardVp = 0;
                    try
                    {
                        cardVp = pbd.VictoryPointBank.CalculateVictoryPoints();
                    }
                    catch (System.Exception)
                    {
                    }
                    rows.Add(new ScoreRow
                    {
                        name = PlayerName(info, pid),
                        isMe = pid == myId,
                        tr = tr,
                        awards = awards,
                        milestones = mile,
                        greenery = green,
                        city = city,
                        cardVp = cardVp,
                        total = tr + awards + mile + green + city + cardVp,
                    });
                }
                rows.Sort((a, b) => b.total.CompareTo(a.total));
            }
            catch (System.Exception)
            {
            }
            return rows;
        }

        // City VP: each city tile scores 1 per adjacent greenery (any owner).
        private static int CitiesScore(TM_GameBoardData board, int pid)
        {
            if (board == null)
            {
                return 0;
            }
            int n = 0;
            var cities = board.GetPlacedTiles(pid, GridTileTypeHelper.CityTileTypes);
            if (cities == null)
            {
                return 0;
            }
            for (int i = 0; i < cities.Count; i++)
            {
                int tileId = cities[i].Infos.TileID;
                var greeneriesInRange = board.GetTileSlotsInRange(tileId, EGridTileType.Greenery);
                if (greeneriesInRange != null)
                {
                    n += greeneriesInRange.Count;
                }
            }
            return n;
        }

        // Greenery VP: 1 per greenery tile the player owns.
        private static int GreeneriesScore(TM_GameBoardData board, int pid)
        {
            if (board == null)
            {
                return 0;
            }
            var tiles = board.GetPlacedTiles(EGridTileType.Greenery, pid);
            return (tiles != null) ? tiles.Count : 0;
        }

        // Milestone VP: ScoreValue (5) per milestone this player has claimed.
        private static int MilestonesScore(TM_GameData data, int pid)
        {
            if (data == null || data.MilestonesData == null)
            {
                return 0;
            }
            var achievements = data.MilestonesData.GetAchievements();
            if (achievements == null)
            {
                return 0;
            }
            int n = 0;
            foreach (var a in achievements)
            {
                if (a != null && a.Item2 == pid && a.Item1 != EMilestoneType.None)
                {
                    n += data.MilestonesData.ScoreValue;
                }
            }
            return n;
        }

        // Award VP per player, ported from ScoreManager.CalculateAwardPlayerScores:
        // for each FUNDED award, the leader(s) get FirstScoreValue; in 3+ player
        // games with a unique leader, the sole runner-up gets SecondScoreValue.
        private static Dictionary<int, int> ComputeAwardScores(TM_Game game, TM_GameData data, List<int> playerIds)
        {
            Dictionary<int, int> totals = new Dictionary<int, int>();
            foreach (int pid in playerIds)
            {
                totals[pid] = 0;
            }
            var awardsData = data.AwardsData;
            if (awardsData == null)
            {
                return totals;
            }
            int mask = awardsData.PurchasedAchievements;
            int playerCount = playerIds.Count;
            foreach (object value in System.Enum.GetValues(typeof(EAwardType)))
            {
                EAwardType award = (EAwardType)value;
                if (award == EAwardType.None || (mask & (int)award) == 0)
                {
                    continue;
                }
                int max = int.MinValue;
                int second = int.MinValue;
                bool uniqueMax = true;
                List<KeyValuePair<int, int>> progress = new List<KeyValuePair<int, int>>(playerCount);
                foreach (int pid in playerIds)
                {
                    int v = AchievementHandler.GetPlayerAwardProgressValue(award, pid, game);
                    if (v > max)
                    {
                        uniqueMax = true;
                        second = max;
                        max = v;
                    }
                    else if (v == max)
                    {
                        uniqueMax = false;
                    }
                    else if (v > second)
                    {
                        second = v;
                    }
                    progress.Add(new KeyValuePair<int, int>(pid, v));
                }
                foreach (KeyValuePair<int, int> p in progress)
                {
                    if (p.Value == max)
                    {
                        totals[p.Key] += awardsData.FirstScoreValue;
                    }
                    else if (playerCount > 2 && uniqueMax && p.Value == second)
                    {
                        totals[p.Key] += awardsData.SecondScoreValue;
                    }
                }
            }
            return totals;
        }

        private static string PlayerName(TM_GameInfo info, int pid)
        {
            try
            {
                TM_Player p = info.GetPlayerWithLocalId(pid);
                if (p != null && !string.IsNullOrEmpty(p.Name))
                {
                    return p.Name;
                }
            }
            catch (System.Exception)
            {
            }
            return "Player " + pid;
        }

        private void Update()
        {
            if (UserClosingPanelFrames > 0)
            {
                UserClosingPanelFrames--;
            }
            // Keep the numbered/sort targets current so their bare keys (1-4 / Q W E R,
            // 5-9) can activate them without a modifier held. FindObjectsByType scans
            // the whole scene, so it runs only on a top-page change or a twice-a-second
            // safety re-scan; RefreshNumberedTargets itself does nothing but a few cheap
            // page lookups when no selectable page is open, so the idle cost is nil.
            _targetsElapsed += Time.unscaledDeltaTime;
            EPage topPage = TopPage();
            if (topPage != _lastBadgePage || _targetsElapsed >= 0.5f)
            {
                _targetsElapsed = 0f;
                _lastBadgePage = topPage;
                RefreshNumberedTargets();
                RefreshKeyHints();
            }
            HandleSpaceToConfirm();
            HandleHotkeys();
            FirePreparedActions();
            KeepMyBoardFocused();

            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed < PollIntervalSeconds)
            {
                return;
            }
            _elapsed = 0f;

            KeepHandOpenAfterPass();

            try
            {
                ExpandPlayerCardsPage page = Object.FindFirstObjectByType<ExpandPlayerCardsPage>();
                if (page == null)
                {
                    // Reset so reopening the hand always triggers one refresh.
                    _lastPlayer = int.MinValue;
                    _lastEvents = int.MinValue;
                    return;
                }

                if (!Singleton<GameManager>.IsInstanced)
                {
                    return;
                }

                TM_Game game = Singleton<GameManager>.Instance.Game;
                if (game == null)
                {
                    return;
                }

                TM_GameInfo info = game.GameInfo;
                if (info == null || info.GameState == null)
                {
                    return;
                }

                int player = info.CurrentPlayerLocalId;
                int events = info.GameState.EventsCount;
                if (player == _lastPlayer && events == _lastEvents)
                {
                    return;
                }

                _lastPlayer = player;
                _lastEvents = events;

                if (!On(FeatCardRefresh))
                {
                    return;
                }
                page.UpdateBasedOnTutorialSelectableCards();
                Logger.LogInfo("Refreshed open card previews (player=" + player + ", events=" + events + ").");
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("Refresh skipped: " + e.Message);
            }
        }

        // Keep your projects visible after you pass: when your turn ends, reopen the
        // read-only hand once during the opponent's turn (after the game has closed
        // it). Only once, so a later manual close stays closed. On your own turn the
        // action-prompt patch handles opening, so this stays out of the way.
        // Play the configured turn-start ping through the game's own audio system.
        private static void PlayTurnStartSound()
        {
            try
            {
                string id = (TurnStartSound != null) ? TurnStartSound.Value : "SFX_OTHER_PLAYER_TURN";
                if (!string.IsNullOrEmpty(id))
                {
                    LuckyHammers.Audio.AudioInterface.PlaySound(id);
                }
            }
            catch (System.Exception)
            {
            }
        }

        private void KeepHandOpenAfterPass()
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                TM_GameInfo info = game?.GameInfo;
                if (info == null)
                {
                    return;
                }
                bool isMyTurn = info.CurrentPlayerLocalId == MyStableLocalId(Singleton<GameManager>.Instance.Game, info);
                if (_prevIsMyTurn && !isMyTurn)
                {
                    _reopenHandAfterPass = true;
                }
                if (isMyTurn)
                {
                    _reopenHandAfterPass = false;
                }
                // Ping when your action turn begins (the transition into your turn),
                // so you notice it even though the turn banner is suppressed.
                if (!_prevIsMyTurn && isMyTurn
                    && game.GameFlow.CurrentPhase == EPhase.Action
                    && On(FeatTurnSound))
                {
                    PlayTurnStartSound();
                }
                _prevIsMyTurn = isMyTurn;

                if (_reopenHandAfterPass
                    && !isMyTurn
                    && game.GameFlow.CurrentPhase == EPhase.Action
                    && !UIManager.Instance.IsPageInStack(PreferredPage()))
                {
                    ReopenPreferredPanel();
                    _reopenHandAfterPass = false;
                }
            }
            catch (System.Exception)
            {
            }
        }

        // True on the initial press, then repeatedly while held (after a short delay).
        // Lets you hold an arrow to ramp an amount instead of tapping many times.
        private bool KeyDownOrRepeat(KeyCode key)
        {
            if (Input.GetKeyDown(key))
            {
                _repeatKey = key;
                _repeatTimer = RepeatInitialDelay;
                return true;
            }
            if (_repeatKey == key)
            {
                if (!Input.GetKey(key))
                {
                    _repeatKey = KeyCode.None;
                    return false;
                }
                _repeatTimer -= Time.unscaledDeltaTime;
                if (_repeatTimer <= 0f)
                {
                    _repeatTimer = RepeatInterval;
                    return true;
                }
            }
            return false;
        }

        private void HandleHotkeys()
        {
            // All shortcuts are bare keys, so any of them can land in the chat / search
            // field. Suppress them while a text field is focused, except when a mod-
            // driven dialog is open (the game auto-focuses chat after a keyboard confirm,
            // so Space / arrows / Esc must keep working there).
            bool typing = IsTextInputFocused() && !ModDialogOpen();
            // Overlay help toggle works even when the master switch is off (but not while
            // typing), so you can always bring up the shortcut list.
            if (!typing && Input.GetKeyDown(Key(KeyOverlay, KeyCode.H)))
            {
                _showOverlay = !_showOverlay;
                return;
            }
            if (!On(FeatHotkeys))
            {
                return;
            }
            // Esc cancels: press No / Close on an open confirm dialog, else dismiss the
            // mod's own panels. Before the typing guard: Esc is never text input.
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                HandleEscape();
                return;
            }
            if (typing)
            {
                return;
            }
            // Prepare queue: bare Q toggles the panel; while it is open the number keys
            // toggle an action in/out and Backspace clears (handled here so they don't
            // fall through to focus-player / card keys). Gated on !PeekHeld so Alt+Q
            // still plays card slot 5.
            if (!PeekHeld() && !AddHeld() && Input.GetKeyDown(Key(KeyPrepare, KeyCode.Q)))
            {
                _showPrepare = !_showPrepare;
                return;
            }
            // Z toggles Pass/skip in the queue (fires last), from anywhere.
            if (!PeekHeld() && !AddHeld() && Input.GetKeyDown(Key(KeyPrepareSkip, KeyCode.Z)))
            {
                int at = _prepareQueue.FindIndex(x => x.IsPass);
                if (at >= 0)
                {
                    _prepareQueue.RemoveAt(at);
                }
                else
                {
                    _prepareQueue.Add(PreparedItem.MakePass());
                    _showPrepare = true;
                }
                return;
            }
            if (_showPrepare && !PeekHeld() && !AddHeld() && HandlePreparePanelInput())
            {
                return;
            }
            if (On(FeatScoreboard) && Input.GetKeyDown(Key(KeyScoreboard, KeyCode.Tab)))
            {
                _showScoreboard = !_showScoreboard;
                return;
            }
            // Arrows repeat while held (hold to ramp an amount up/down quickly).
            if (KeyDownOrRepeat(Key(KeyNavUp, KeyCode.UpArrow)))
            {
                NavigateChoice(down: false);
                return;
            }
            if (KeyDownOrRepeat(Key(KeyNavDown, KeyCode.DownArrow)))
            {
                NavigateChoice(down: true);
                return;
            }
            if (KeyDownOrRepeat(Key(KeyNavLeft, KeyCode.LeftArrow)))
            {
                NavigateHorizontal(right: false);
                return;
            }
            if (KeyDownOrRepeat(Key(KeyNavRight, KeyCode.RightArrow)))
            {
                NavigateHorizontal(right: true);
                return;
            }
            // Play / use the highlighted item: only while the Alt (highlight) layer is
            // held, so the bare letters below stay free for the panels (E/R/W) even when
            // cards fill those slots. A key that no target fills falls through to its
            // panel / focus-player binding, so Alt+E with no 7th card still opens Effects.
            // Cache mode: on opponents' turns a badge press queues the item (prepare
            // during downtime); on your own turn it plays/activates normally so you keep
            // full control of resource choices. Hold the add-key to force-cache on your
            // turn. Never cache in a selection / choice / play-view context (buy, sell,
            // steal, resource pick, card choice, carousel) - there a badge must select.
            bool addMode = (AddHeld() || !IsMyActionTurn()) && !IsSelectionContext();
            if (PeekHeld() || AddHeld())
            {
                // Cards / actions / standard-project / milestone / award rows. Each target
                // carries its own key (a grid maps row 1 to 1-4, row 2 to Q W E R). In
                // cache mode a queueable target goes to the Prepare queue instead of
                // being played now.
                for (int i = 0; i < _targets.Count; i++)
                {
                    if (TargetKeyDown(_targets[i].key))
                    {
                        if (addMode)
                        {
                            PreparedItem? d = DescribeTarget(_targets[i].t);
                            if (d.HasValue)
                            {
                                EnqueuePrepared(d.Value);
                                _showPrepare = true; // surface the panel so the add is visible
                                Logger.LogInfo("Prepare: queued " + d.Value.label + " (from source)");
                            }
                            return;
                        }
                        ActivateNumberedTarget(i);
                        return;
                    }
                }
                // Secondary group: the hand's sort/filter tabs, badged 5-9 (not queueable).
                if (!addMode)
                {
                    for (int i = 0; i < s_sortKeys.Length; i++)
                    {
                        if ((Input.GetKeyDown(s_sortKeys[i]) || Input.GetKeyDown(KeyCode.Keypad5 + i))
                            && i < _sortTargets.Count)
                        {
                            ActivateSortTarget(i);
                            return;
                        }
                    }
                }
            }
            // Conversions (cache them too while in cache mode).
            if (Input.GetKeyDown(Key(KeyGreenery, KeyCode.G)))
            {
                if (addMode)
                {
                    EnqueuePrepared(PreparedItem.MakeConversion(EResourceType.Plant, "Greenery (place)"));
                    _showPrepare = true;
                }
                else
                {
                    Convert(EResourceType.Plant);
                }
                return;
            }
            if (Input.GetKeyDown(Key(KeyTemperature, KeyCode.F)))
            {
                if (addMode)
                {
                    EnqueuePrepared(PreparedItem.MakeConversion(EResourceType.Heat, "Heat → Temperature"));
                    _showPrepare = true;
                }
                else
                {
                    Convert(EResourceType.Heat);
                }
                return;
            }
            // Panels / actions.
            if (Input.GetKeyDown(Key(KeyProjects, KeyCode.P)))
            {
                ToggleHand();
                return;
            }
            if (Input.GetKeyDown(Key(KeyActions, KeyCode.A)))
            {
                TryTogglePopup(EPage.CardActionsPopup);
                return;
            }
            if (Input.GetKeyDown(Key(KeyResources, KeyCode.R)))
            {
                TryTogglePopup(EPage.CardResourcesPopup);
                return;
            }
            if (Input.GetKeyDown(Key(KeyVictoryPoints, KeyCode.V)))
            {
                TryTogglePopup(EPage.CardVictoryPointsPopup);
                return;
            }
            if (Input.GetKeyDown(Key(KeyEffects, KeyCode.E)))
            {
                TryTogglePopup(EPage.CardEffectsPopup);
                return;
            }
            if (Input.GetKeyDown(Key(KeyTags, KeyCode.T)))
            {
                TryTogglePopup(EPage.CardTagsPopup);
                return;
            }
            if (Input.GetKeyDown(Key(KeyMilestones, KeyCode.M)))
            {
                ToggleActionTab(EActionType.Milestone);
                return;
            }
            if (Input.GetKeyDown(Key(KeyStandardProjects, KeyCode.K)))
            {
                ToggleActionTab(EActionType.StandardProject);
                return;
            }
            if (Input.GetKeyDown(Key(KeyAwards, KeyCode.W)))
            {
                ToggleActionTab(EActionType.Award);
                return;
            }
            if (Input.GetKeyDown(Key(KeySell, KeyCode.S)))
            {
                SellFromHand();
                return;
            }
            if (Input.GetKeyDown(Key(KeyBoard, KeyCode.B)))
            {
                ToggleBoardView();
                return;
            }
            // Bare 1-5 focus a player's board when no numbered target claimed the key.
            for (int n = 0; n < 5; n++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + n) || Input.GetKeyDown(KeyCode.Keypad1 + n))
                {
                    FocusPlayer(n);
                    return;
                }
            }
        }

        // Open (or switch to) one of the top tabs shown as MILESTONES / STANDARD
        // PROJECTS / AWARDS; press the same key again to close it. Uses the game's own
        // top-button handler so the tab animation and selected state stay correct.
        private void ToggleActionTab(EActionType action)
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                HUD_TopActionButtons tabs = (game != null && game.HUD != null) ? game.HUD.TopActionButtons : null;
                if (tabs == null)
                {
                    return;
                }
                if (UIManager.Instance.IsPageInStack(EPage.ActionPopup))
                {
                    ActionPopup popup = UIManager.Instance.GetPage<ActionPopup>(EPage.ActionPopup);
                    if (popup != null && popup.GetActionType() == action)
                    {
                        popup.OnCloseButtonClick(); // same tab already open -> close it
                        return;
                    }
                }
                tabs.OnClick(action); // open, or switch to this tab
            }
            catch (System.Exception)
            {
            }
        }

        // The hand (ViewPlayerCardsPage) is not a tray toggle popup; it opens from
        // the card-hand button (m_CardHand) via a gated handler. Toggle: close if
        // open, else open via that button.
        private void ToggleHand()
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return;
                }
                if (UIManager.Instance.IsPageInStack(EPage.ViewPlayerCardsPage))
                {
                    UserClosingHand = true;
                    UserClosedPanel = true;
                    UIManager.Instance.Pop(EPage.ViewPlayerCardsPage);
                    UserClosingHand = false;
                    return;
                }
                UserClosedPanel = false;
                OpenHand();
            }
            catch (System.Exception)
            {
            }
        }

        // Open the read-only hand view directly, the same way the game does when the
        // card-hand button is tapped (HUD_PlayerTray pushes ViewPlayerCardsPage with a
        // ViewHandCards init param). No-op if already open or the hand is empty.
        // Static so both the P hotkey and the reopen-after-play patch share the one
        // correct path (the hand is NOT a tray toggle popup, so the old
        // HUD_TogglePopupButton lookup never found it).
        internal static void OpenHand()
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced
                    || UIManager.Instance.IsPageInStack(EPage.ViewPlayerCardsPage))
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                if (game == null)
                {
                    return;
                }
                int myId = MyStableLocalId(game, game.GameInfo);
                TM_PlayerBoardData board = game.GameData.GetPlayerBoardData(myId);
                if (board == null || board.HandCards == null || board.HandCards.Count == 0)
                {
                    return;
                }
                UIManager.Instance.Push(
                    EPage.ViewPlayerCardsPage,
                    keepPreviousPagesVisible: true,
                    ViewPlayerCardsPage.InitParam.Create(
                        myId,
                        ViewPlayerCardsPage.EViewCardsPageMode.ViewHandCards,
                        board.HandCards));
            }
            catch (System.Exception)
            {
            }
        }

        // Whether your most recent play was a card action (blue card) rather than a
        // project card. Drives which panel the auto-open reopens.
        internal static bool LastPlayWasAction;

        // Open the card-actions popup (your blue-card actions). No-op if already open.
        internal static void OpenActions()
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced
                    || UIManager.Instance.IsPageInStack(EPage.CardActionsPopup))
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                if (game == null || game.GameInfo == null)
                {
                    return;
                }
                // Open MY actions explicitly. ShowPopup binds to whichever player the
                // HUD is currently showing, which after viewing an opponent would open
                // the opponent's actions.
                int myId = MyStableLocalId(game, game.GameInfo);
                UIManager.Instance.Push(EPage.CardActionsPopup, keepPreviousPagesVisible: true, IntValuePageParameters.Create(myId));
            }
            catch (System.Exception)
            {
            }
        }

        // Reopen whichever panel you were last using: actions if your last play was a
        // card action, otherwise your projects/hand.
        // Set true when you deliberately close the hand/actions, so we stop
        // auto-reopening it. Cleared only when you open a panel yourself (P/A).
        internal static bool UserClosedPanel;

        internal static void ReopenPreferredPanel()
        {
            if (UserClosedPanel)
            {
                return; // you closed it on purpose; don't force it back open
            }
            if (LastPlayWasAction)
            {
                OpenActions();
            }
            else
            {
                OpenHand();
            }
        }

        // The page the preferred panel corresponds to (for "is it already open?").
        internal static EPage PreferredPage()
        {
            return LastPlayWasAction ? EPage.CardActionsPopup : EPage.ViewPlayerCardsPage;
        }

        // Set true only while YOU are closing the hand (a manual close path), so those
        // pops are allowed through while automatic ones are suppressed.
        internal static bool UserClosingHand;

        // Frames remaining in which a user-initiated popup close is allowed through the
        // suppression below. Set by the mod's own close paths (toggle keys, Esc) and
        // counted down in Update; covers the async close-animation callback.
        internal static int UserClosingPanelFrames;

        // Tray popups (besides the hand) kept open during opponents' turns so you can
        // browse / prepare without them snapping shut when an opponent acts.
        private static readonly EPage[] s_keepOpenOffTurn =
        {
            EPage.ViewPlayerCardsPage, EPage.CardActionsPopup, EPage.CardResourcesPopup,
            EPage.CardVictoryPointsPopup, EPage.CardEffectsPopup, EPage.CardTagsPopup,
        };

        // Suppress the game's automatic close of the hand / tray popups while it is not
        // your turn, so they stay open (no close-then-reopen flicker, no snapping shut
        // when an opponent's action resolves). Your own closes are allowed via the
        // UserClosingHand flag (hand) or the UserClosingPanelFrames grace window (other
        // popups); your-turn closes (needed for tile placement, etc.) are never touched.
        internal static bool ShouldSuppressHandClose(EPage page)
        {
            if (!On(FeatKeepHandOpen) || UserClosingHand || UserClosingPanelFrames > 0)
            {
                return false;
            }
            bool keep = false;
            for (int i = 0; i < s_keepOpenOffTurn.Length; i++)
            {
                if (s_keepOpenOffTurn[i] == page) { keep = true; break; }
            }
            if (!keep)
            {
                return false;
            }
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return false;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                TM_GameInfo info = game != null ? game.GameInfo : null;
                if (info == null)
                {
                    return false;
                }
                return info.UserAllowedToPlayLocalID != MyStableLocalId(game, info);
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // "View state" / "Return": flip the board-inspection toggle so you can look
        // at the board while a front-end page is up, and flip back to return.
        private void ToggleBoardView()
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                HUD_CheckGameStateToggle toggle =
                    (game != null && game.HUD != null) ? game.HUD.CheckGameStateToggle : null;
                if (toggle == null || !toggle.IsCheckGameStateToggleActive() || !toggle.IsCheckGameStateInteractable())
                {
                    return;
                }
                Toggle stateToggle = Traverse.Create(toggle).Field("checkGameStateToggle").GetValue<Toggle>();
                if (stateToggle != null)
                {
                    stateToggle.isOn = !stateToggle.isOn;
                }
            }
            catch (System.Exception)
            {
            }
        }

        // Standard conversions from the tray: Plant -> greenery, Heat -> temperature.
        // Only fires when the conversion button is interactable (enough resources and
        // it's your turn), so it can't attempt an illegal conversion.
        private void Convert(EResourceType resourceType)
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                HUD_PlayerTray tray = (game != null && game.HUD != null) ? game.HUD.PlayerTray : null;
                if (tray == null)
                {
                    return;
                }
                string field = (resourceType == EResourceType.Plant) ? "m_PlantConversion" : "m_HeatConversion";
                CanvasGroup button = Traverse.Create(tray).Field(field).GetValue<CanvasGroup>();
                if (button == null || !button.interactable)
                {
                    return; // not enough resources, or conversion unavailable right now
                }
                // Submit the conversion directly, like the tray handler does, but skip
                // its 1-second cosmetic lock (WaitForSeconds(1f)) so conversions chain.
                TM_GameInfo info = game.GameInfo;
                TM_Player me = info.GetMyPlayer();
                if (me == null || !info.CurrentPlayer.IsMe() || me.NbActionsLeft <= 0)
                {
                    return;
                }
                info.CurrentPlayer.HandleSelectActionSelection(EActionType.Conversion, resourceType);
            }
            catch (System.Exception)
            {
            }
        }

        // Sell cards from hand: open the game's Sell Patents UI (your hand, pick
        // cards, Sell). Same call the Standard Projects > Sell Patents button makes,
        // so it only works during your action turn. Space then confirms the sale.
        // Card the direct-sell wants to sell, injected into the sell action's event
        // so the Sell Patents popup is never needed. -1 = no direct sell pending.
        internal static int PendingSellCardId = -1;

        private void SellFromHand()
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                TM_Player current = game?.GameInfo?.CurrentPlayer;
                if (current == null
                    || current.NbActionsLeft <= 0
                    || game.GameFlow.CurrentPhase != EPhase.Action
                    || UIManager.Instance.IsPageInStack(EPage.SellPatentPopupPage))
                {
                    return;
                }
                if (!current.TryGetAgentAsHuman(out TM_HumanAgent human))
                {
                    return;
                }

                // If a hand card is focused, sell exactly that card with no popup.
                int myId = MyStableLocalId(game, game.GameInfo);
                TM_PlayerBoardData board = game.GameData.GetPlayerBoardData(myId);
                ExpandPlayerCardsPage page = Object.FindFirstObjectByType<ExpandPlayerCardsPage>();
                int cardId = (page != null) ? Traverse.Create(page).Field("m_CurrentCardID").GetValue<int>() : -1;

                if (cardId > 0 && board != null && board.HandCards != null && board.HandCards.Contains(cardId))
                {
                    PlayerAction sellAction = board.PlayerActionBank.GetStandardProjectPlayerAction(EStandardProject.SellPatents);
                    Traverse.Create(human).Field("m_SellPatentAction").SetValue(sellAction);
                    // Selling is not a blue-card action, so the panel to reopen after
                    // it is your hand, not the actions popup. Clear the flag or the
                    // SelectAction re-prompt would reopen actions over the cards.
                    LastPlayWasAction = false;
                    PendingSellCardId = cardId;
                    try
                    {
                        human.InvokeSelectActionCallbackForSellPatentAction();
                    }
                    finally
                    {
                        // If the action didn't consume it synchronously, clear so it
                        // never leaks into a later, unrelated sell.
                        PendingSellCardId = -1;
                    }
                    StartCoroutine(ReopenHandAfterSell(cardId));
                    return;
                }

                // No focused card: fall back to the game's sell menu.
                human.HandleSelectStandardProjectSelection(EStandardProject.SellPatents, 0);
            }
            catch (System.Exception)
            {
            }
        }

        // Selling a card via the direct-sell path leaves the hand carousel closed.
        // Wait until the server confirms the card is gone from the hand (or a short
        // timeout), then reopen the hand grid so you land back on the remaining cards.
        private System.Collections.IEnumerator ReopenHandAfterSell(int soldCardId)
        {
            float deadline = Time.unscaledTime + 3f;
            while (Time.unscaledTime < deadline && HandContains(soldCardId))
            {
                yield return null;
            }
            yield return new WaitForSeconds(0.15f);
            if (UIManager.Instance.IsPageInStack(EPage.ExpandPlayerCardsPage))
            {
                UIManager.Instance.Pop(EPage.ExpandPlayerCardsPage);
            }
            UserClosedPanel = false;
            OpenHand();
        }

        private static bool HandContains(int cardId)
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return false;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                if (game == null)
                {
                    return false;
                }
                int myId = MyStableLocalId(game, game.GameInfo);
                TM_PlayerBoardData board = game.GameData.GetPlayerBoardData(myId);
                return board != null && board.HandCards != null && board.HandCards.Contains(cardId);
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // Focus a player's board (1 = you, then the others). Invokes the same tab
        // click the game uses, which switches the HUD to that player's tray/board.
        private void FocusPlayer(int index)
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                HUD_PlayerTabs tabs = (game != null && game.HUD != null) ? game.HUD.PlayerTabs : null;
                if (tabs == null || tabs.PlayerTabs == null)
                {
                    return;
                }
                // Order by the tabs' current on-screen position (the live turn order),
                // top to bottom then left to right, so 1/2/3 track the displayed list.
                List<KeyValuePair<int, HUD_PlayerTab>> pairs = new List<KeyValuePair<int, HUD_PlayerTab>>();
                foreach (KeyValuePair<int, HUD_PlayerTab> kv in tabs.PlayerTabs)
                {
                    if (kv.Value != null && kv.Value.isActiveAndEnabled)
                    {
                        pairs.Add(kv);
                    }
                }
                pairs.Sort((a, b) =>
                {
                    int cy = b.Value.transform.position.y.CompareTo(a.Value.transform.position.y);
                    return cy != 0 ? cy : a.Value.transform.position.x.CompareTo(b.Value.transform.position.x);
                });

                if (index < 0 || index >= pairs.Count)
                {
                    return;
                }
                pairs[index].Value.OnClickIdentifierPannel();
                RebindOpenStatPopup(pairs[index].Key);
            }
            catch (System.Exception)
            {
            }
        }

        // "Keep my board focused" state: offline the game focuses whichever bot is
        // taking its turn; we snap the view back to our own board shortly after (once
        // per turn change, after a short delay so we win the game's own focus).
        private int _lastCurrentPlayerSeen = int.MinValue;
        private float _refocusDelay = -1f;

        private void KeepMyBoardFocused()
        {
            if (!On(FeatStayFocused))
            {
                return;
            }
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                TM_GameInfo info = game != null ? game.GameInfo : null;
                if (info == null || info.IsOnline)
                {
                    return; // online the HUD already stays on your own seat
                }
                int myId = MyStableLocalId(game, info);
                int cur = info.CurrentPlayerLocalId;
                if (cur != _lastCurrentPlayerSeen)
                {
                    _lastCurrentPlayerSeen = cur;
                    _refocusDelay = (cur != myId) ? 0.35f : -1f; // a bot took over -> refocus me
                }
                if (_refocusDelay >= 0f)
                {
                    _refocusDelay -= Time.unscaledDeltaTime;
                    if (_refocusDelay < 0f)
                    {
                        FocusMyBoard(game, myId);
                    }
                }
            }
            catch (System.Exception)
            {
            }
        }

        private static void FocusMyBoard(TM_Game game, int myId)
        {
            try
            {
                HUD_PlayerTabs tabs = game.HUD != null ? game.HUD.PlayerTabs : null;
                if (tabs == null || tabs.PlayerTabs == null)
                {
                    return;
                }
                foreach (KeyValuePair<int, HUD_PlayerTab> kv in tabs.PlayerTabs)
                {
                    if (kv.Key == myId && kv.Value != null)
                    {
                        kv.Value.OnClickIdentifierPannel();
                        RebindOpenStatPopup(myId);
                        return;
                    }
                }
            }
            catch (System.Exception)
            {
            }
        }

        // The Card* stat popups are bound to the player they were opened for. After
        // switching the focused player, reopen an open one for the new player so it
        // shows the new player's values instead of the original's.
        private static readonly EPage[] s_statPopups =
        {
            EPage.CardActionsPopup, EPage.CardEffectsPopup, EPage.CardResourcesPopup,
            EPage.CardVictoryPointsPopup, EPage.CardTagsPopup,
        };

        private static void RebindOpenStatPopup(int playerLocalId)
        {
            foreach (EPage page in s_statPopups)
            {
                if (UIManager.Instance.IsPageInStack(page))
                {
                    UIManager.Instance.Pop(page, aAddToHistory: false);
                    UIManager.Instance.Push(page, keepPreviousPagesVisible: true, IntValuePageParameters.Create(playerLocalId));
                    return;
                }
            }
        }

        // Numbered, clickable items for the current context (recomputed each frame):
        // the SELECT ONE choices, else the actions popup's actions. Each carries the key
        // that fires it and the label OnGUI paints over it, so a card grid can map the
        // top row to numbers and the second row to Q W E R (see AddRowBasedTargets),
        // while list-style contexts just take the keys in flat order (see AddTarget).
        private readonly List<(Transform t, System.Action act, KeyCode key, string label)> _targets =
            new List<(Transform, System.Action, KeyCode, string)>();

        // Keys that fire the numbered targets, in on-screen order (Alt held; badged on
        // peek). Cards 1-4 use the number row; cards 5-8 use the q/w/e/r row instead of
        // reaching for 5-8. More than eight on a page: change page and reuse these.
        private static readonly KeyCode[] s_targetKeys =
        {
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
            KeyCode.Q, KeyCode.W, KeyCode.E, KeyCode.R,
        };
        private static readonly string[] s_targetKeyLabels = { "1", "2", "3", "4", "Q", "W", "E", "R" };

        // A target's key went down this frame; number keys also accept their keypad twin.
        private static bool TargetKeyDown(KeyCode key)
        {
            if (Input.GetKeyDown(key))
            {
                return true;
            }
            if (key >= KeyCode.Alpha1 && key <= KeyCode.Alpha9)
            {
                return Input.GetKeyDown(KeyCode.Keypad1 + (key - KeyCode.Alpha1));
            }
            return false;
        }

        // Append a target taking the next key in flat order (1 2 3 4 Q W E R). Used by
        // the list/popup contexts where there is no meaningful row/column layout.
        private void AddTarget(Transform t, System.Action act)
        {
            int i = _targets.Count;
            if (i >= s_targetKeys.Length)
            {
                return; // out of keys; leave the rest un-numbered
            }
            _targets.Add((t, act, s_targetKeys[i], s_targetKeyLabels[i]));
        }

        // Append card-grid targets so the top visual row maps to the number keys and the
        // second row to Q W E R, column-aligned (matching the keyboard layout) regardless
        // of how many cards sit in each row. Rows past the second are left un-numbered.
        private void AddRowBasedTargets(List<(Transform t, System.Action act)> items)
        {
            items.Sort((a, b) =>
            {
                float ay = Mathf.Round(a.t.position.y / 40f);
                float by = Mathf.Round(b.t.position.y / 40f);
                return ay != by ? by.CompareTo(ay) : a.t.position.x.CompareTo(b.t.position.x);
            });
            int row = 0;
            int col = 0;
            float prevBucket = float.NaN;
            foreach ((Transform t, System.Action act) in items)
            {
                float bucket = Mathf.Round(t.position.y / 40f);
                if (!float.IsNaN(prevBucket) && bucket != prevBucket)
                {
                    row++;
                    col = 0;
                }
                prevBucket = bucket;
                int keyIndex = (row == 0 && col < 4) ? col
                    : (row == 1 && col < 4) ? 4 + col
                    : -1;
                if (keyIndex >= 0)
                {
                    _targets.Add((t, act, s_targetKeys[keyIndex], s_targetKeyLabels[keyIndex]));
                }
                col++;
            }
        }

        // Secondary numbered group for the hand's sort/filter tabs (COST, PLAYABILITY,
        // CARD TYPE, TAGS, CHRONOLOGICAL). Kept off the card keys so both can show at
        // once: cards on 1-4/QWER, filters on 5-9. Bare keys, same as the cards.
        private readonly List<(Transform t, System.Action act)> _sortTargets = new List<(Transform, System.Action)>();
        private static readonly KeyCode[] s_sortKeys =
        {
            KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9,
        };
        private static readonly string[] s_sortKeyLabels = { "5", "6", "7", "8", "9" };

        // Key-letter hints drawn over on-screen buttons while the highlight key is held, so the
        // panel keys are discoverable. Refreshed on the same cadence as _targets.
        private readonly List<(Transform t, string label)> _keyHints = new List<(Transform, string)>();

        // Every HUD button whose key we hint (top tabs, tray stat popups, hand,
        // conversions, board toggle), cached once since they live for the whole game,
        // so the hint refresh doesn't scan the scene every time.
        private readonly List<(Transform t, string label)> _hintTargets =
            new List<(Transform, string)>();

        // True while the "highlight" key is held. This is the card-play layer: panels
        // and actions are bare keys, and holding this both reveals the badges/hints and
        // arms the numbered keys (Alt+1-4 / Q W E R play a card, Alt+5-9 pick a sort
        // tab). Defaults to Alt/Option, which has no OS conflict on either platform
        // (unlike Cmd, whose Q/W/M/H are macOS-reserved) and no Ctrl+click=right-click
        // side effect. Accepts the matching right-hand key so either Alt works.
        private static bool ModKeyHeld(KeyCode k)
        {
            if (Input.GetKey(k))
            {
                return true;
            }
            switch (k)
            {
                case KeyCode.LeftShift: return Input.GetKey(KeyCode.RightShift);
                case KeyCode.LeftControl: return Input.GetKey(KeyCode.RightControl);
                case KeyCode.LeftAlt: return Input.GetKey(KeyCode.RightAlt);
                case KeyCode.LeftCommand: return Input.GetKey(KeyCode.RightCommand);
                default: return false;
            }
        }

        // Alt: reveal badges + play now. Add-key (Shift): reveal badges + queue instead.
        private static bool PeekHeld() { return ModKeyHeld(Key(KeyPeek, KeyCode.LeftAlt)); }
        private static bool AddHeld() { return ModKeyHeld(Key(KeyPrepareAdd, KeyCode.LeftShift)); }

        // If the target under this transform is a queueable atomic action (a placement-
        // free standard project, a milestone, or an award), return how to replay it;
        // else null. Standard projects that need a tile (Aquifer/Greenery/City) and Sell
        // are rejected. Uses the element's own action/element type so replay is identical.
        private static PreparedItem? DescribeTarget(Transform t)
        {
            try
            {
                if (t == null)
                {
                    return null;
                }
                StandardProjectElement sp = t.GetComponent<StandardProjectElement>();
                if (sp != null)
                {
                    int et = Traverse.Create(sp).Method("GetElementType").GetValue<int>();
                    if (et == (int)EStandardProject.PowerPlant)
                    {
                        return PreparedItem.MakeAction(EActionType.StandardProject, et, "Power Plant");
                    }
                    if (et == (int)EStandardProject.Asteroid)
                    {
                        return PreparedItem.MakeAction(EActionType.StandardProject, et, "Asteroid");
                    }
                    return null; // needs a tile / card selection -> can't auto-fire
                }
                MilestoneElement ms = t.GetComponent<MilestoneElement>();
                if (ms != null)
                {
                    int et = Traverse.Create(ms).Method("GetElementType").GetValue<int>();
                    return PreparedItem.MakeAction(EActionType.Milestone, et, "Milestone: " + ms.MilestoneType);
                }
                AwardElement aw = t.GetComponent<AwardElement>();
                if (aw != null)
                {
                    int et = Traverse.Create(aw).Method("GetElementType").GetValue<int>();
                    return PreparedItem.MakeAction(EActionType.Award, et, "Award #" + et);
                }
                // A blue card's action (in the Card Actions popup): queue its card id; at
                // your turn the mod opens the popup and clicks it (atomic ones run; ones
                // that ask for a choice hand off to you).
                CardActionElement ca = t.GetComponent<CardActionElement>();
                if (ca != null)
                {
                    return PreparedItem.MakeCardAction(ca.CardId, "Action: " + CardNameById(ca.CardId));
                }
                // A card in hand: queue its id; at your turn the mod opens its play view
                // so you finish payment / placement (it can't do those for you).
                CardPreview cp = t.GetComponent<CardPreview>();
                if (cp != null)
                {
                    return PreparedItem.MakeCard(cp.CardId, "Card: " + CardName(cp));
                }
            }
            catch (System.Exception)
            {
            }
            return null;
        }

        // Localized card name for a card id (falls back to the id). Reflection keeps this
        // resilient to dataset shape differences.
        private static string CardNameById(int id)
        {
            try
            {
                object ds = Singleton<GameManager>.Instance.DataSet;
                object data = Traverse.Create(ds).Method("GetCardById", new object[] { id }).GetValue();
                if (data != null)
                {
                    string n = Traverse.Create(data).Method("GetLocalizedName", new object[] { false }).GetValue<string>();
                    if (!string.IsNullOrEmpty(n))
                    {
                        return n;
                    }
                }
            }
            catch (System.Exception)
            {
            }
            return "#" + id;
        }

        // Best-effort display name for a hand card (falls back to the id).
        private static string CardName(CardPreview cp)
        {
            try
            {
                object tmp = Traverse.Create(cp).Property("CardTextName").GetValue()
                    ?? Traverse.Create(cp).Field("CardTextName").GetValue();
                if (tmp != null)
                {
                    string s = Traverse.Create(tmp).Property("text").GetValue<string>();
                    if (!string.IsNullOrEmpty(s))
                    {
                        return s;
                    }
                }
            }
            catch (System.Exception)
            {
            }
            return "#" + cp.CardId;
        }

        // The top page of the UI stack, used as a cheap change signal for the badge
        // scan (a full re-scan is only needed when this changes).
        private static EPage TopPage()
        {
            try
            {
                return UIManager.Instance.GetLastPageInStack();
            }
            catch (System.Exception)
            {
                return (EPage)(-1);
            }
        }

        // Cheap check (a list lookup) for whether a page is open, used to skip the
        // expensive whole-scene FindObjectsByType scans when that page isn't up.
        private static bool PageOpen(EPage page)
        {
            try
            {
                return UIManager.Instance.IsPageInStack(page);
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // True when the mod is driving an interactive dialog (card play, card/discard
        // selection, steal target, a choice/conversion panel, or a confirm popup). In
        // that state keyboard hotkeys must win over text-field focus: the game auto-
        // focuses its chat field after a keyboard confirm, which would otherwise block
        // Space/arrows until you click. Chat still suppresses hotkeys during free play.
        private static bool ModDialogOpen()
        {
            try
            {
                // Only the transient action dialogs, not the long-lived hand / actions
                // panels, so chat still works during normal play on your board.
                switch (TopPage())
                {
                    case EPage.ExpandPlayerCardsPage:
                    case EPage.CardSelectionPage:
                    case EPage.SellPatentPopupPage:
                    case EPage.StealResourcePage:
                    case EPage.PlayerCardResourcePage:
                        return true;
                }
                return Object.FindFirstObjectByType<GenericPopup>() != null
                    || FindActiveConversionController() != null
                    || FindActiveChoiceController() != null;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // Collect the on-screen buttons whose key we can show as a hint. Currently
        // the three top tabs (Milestones / Standard Projects / Awards); each button
        // reports its own EActionType, which maps to the configured key.
        private void RefreshKeyHints()
        {
            _keyHints.Clear();
            try
            {
                EnsureHintTargetsCached();
                // Draw the hint for every target currently on screen. activeInHierarchy
                // (visible) rather than isActiveAndEnabled: a greyed-but-openable button
                // still shows its key.
                foreach ((Transform t, string label) in _hintTargets)
                {
                    if (t != null && t.gameObject.activeInHierarchy)
                    {
                        _keyHints.Add((t, label));
                    }
                }
            }
            catch (System.Exception)
            {
            }
        }

        // Find every hinted HUD button once and cache it. Re-find only if the cache is
        // empty or a cached target was destroyed (e.g. leaving the game). Waits for the
        // tray to exist so the whole set is cached together, not a partial set.
        private void EnsureHintTargetsCached()
        {
            bool valid = _hintTargets.Count > 0;
            for (int i = 0; i < _hintTargets.Count; i++)
            {
                if (_hintTargets[i].t == null)
                {
                    valid = false;
                    break;
                }
            }
            if (valid || !Singleton<GameManager>.IsInstanced)
            {
                return;
            }
            TM_Game game = Singleton<GameManager>.Instance.Game;
            if (game == null || game.HUD == null || game.HUD.PlayerTray == null)
            {
                return; // HUD not fully built yet; try again next refresh
            }
            _hintTargets.Clear();
            try
            {
                // Top tabs: Milestones / Standard Projects / Awards.
                foreach (HUD_TopActionButton b in
                    Object.FindObjectsByType<HUD_TopActionButton>(FindObjectsSortMode.None))
                {
                    string label = (b != null) ? KeyForActionType(b.GetActionType()) : null;
                    if (label != null)
                    {
                        _hintTargets.Add((b.transform, label));
                    }
                }
                // Tray stat popups: Actions / Resources / Victory points / Effects.
                foreach (HUD_TogglePopupButton b in
                    Object.FindObjectsByType<HUD_TogglePopupButton>(FindObjectsSortMode.None))
                {
                    string label = (b != null) ? KeyForPage(b.GetPageType()) : null;
                    if (label != null)
                    {
                        _hintTargets.Add((b.transform, label));
                    }
                }
                // Tray hand + conversion buttons.
                HUD_PlayerTray tray = game.HUD.PlayerTray;
                AddFieldTarget(tray, "m_CardHand", Key(KeyProjects, KeyCode.P).ToString());
                AddFieldTarget(tray, "m_PlantConversion", Key(KeyGreenery, KeyCode.G).ToString());
                AddFieldTarget(tray, "m_HeatConversion", Key(KeyTemperature, KeyCode.F).ToString());
                // Board-view toggle.
                HUD_CheckGameStateToggle boardToggle = game.HUD.CheckGameStateToggle;
                if (boardToggle != null)
                {
                    _hintTargets.Add((boardToggle.transform, Key(KeyBoard, KeyCode.B).ToString()));
                }
            }
            catch (System.Exception)
            {
            }
        }

        // Add a hint anchored on a private tray field (a GameObject or a Component),
        // via reflection, so we don't hard-depend on its exact type.
        private void AddFieldTarget(object owner, string field, string label)
        {
            try
            {
                object value = Traverse.Create(owner).Field(field).GetValue();
                Transform t = (value as Component)?.transform ?? (value as GameObject)?.transform;
                if (t != null)
                {
                    _hintTargets.Add((t, label));
                }
            }
            catch (System.Exception)
            {
            }
        }

        // The configured key for a tray stat-popup page, or null if that popup has no
        // hotkey.
        private static string KeyForPage(EPage page)
        {
            switch (page)
            {
                case EPage.CardActionsPopup:
                    return Key(KeyActions, KeyCode.A).ToString();
                case EPage.CardResourcesPopup:
                    return Key(KeyResources, KeyCode.R).ToString();
                case EPage.CardVictoryPointsPopup:
                    return Key(KeyVictoryPoints, KeyCode.V).ToString();
                case EPage.CardEffectsPopup:
                    return Key(KeyEffects, KeyCode.E).ToString();
                case EPage.CardTagsPopup:
                    return Key(KeyTags, KeyCode.T).ToString();
                default:
                    return null;
            }
        }

        private static string KeyForActionType(EActionType action)
        {
            switch (action)
            {
                case EActionType.Milestone:
                    return Key(KeyMilestones, KeyCode.M).ToString();
                case EActionType.StandardProject:
                    return Key(KeyStandardProjects, KeyCode.K).ToString();
                case EActionType.Award:
                    return Key(KeyAwards, KeyCode.W).ToString();
                default:
                    return null;
            }
        }

        private void RefreshNumberedTargets()
        {
            _targets.Clear();
            _sortTargets.Clear();
            s_activeViewport = ActiveScrollViewport();
            try
            {
                CardActionChoiceController choiceController =
                    PageOpen(EPage.ChooseCardActionCardPage) ? FindActiveChoiceController() : null;
                if (choiceController != null)
                {
                    Traverse ct = Traverse.Create(choiceController);
                    System.Collections.IList choices = ct.Field("m_CardActionChoices").GetValue() as System.Collections.IList;
                    if (choices != null)
                    {
                        foreach (object choice in choices)
                        {
                            int idx = Traverse.Create(choice).Property("Index").GetValue<int>();
                            Transform tr = Traverse.Create(choice).Property("transform").GetValue<Transform>();
                            AddTarget(tr, () => ct.Method("OnChoiceClicked", idx).GetValue());
                        }
                    }
                    return;
                }

                // Corporation selection: number each corp card. The badge selects
                // (highlights) that corp, exactly like clicking it; Space then confirms.
                CorporationSelectionPopup corpPopup =
                    PageOpen(EPage.CorporationSelectionPopup) ? Object.FindFirstObjectByType<CorporationSelectionPopup>() : null;
                if (corpPopup != null)
                {
                    List<CorporationCard> corps = corpPopup.CorporationCards;
                    if (corps != null)
                    {
                        List<CorporationCard> shown = new List<CorporationCard>();
                        foreach (CorporationCard c in corps)
                        {
                            if (c != null && c.isActiveAndEnabled && IsOnScreen(c.transform))
                            {
                                shown.Add(c);
                            }
                        }
                        SortByScreenPosition(shown);
                        foreach (CorporationCard c in shown)
                        {
                            CorporationCard corp = c;
                            AddTarget(corp.transform, () => corp.SelectCorporation());
                        }
                    }
                    return;
                }

                // "Select any production/resource" popup: each player entry.
                StealResourcePage steal =
                    PageOpen(EPage.StealResourcePage) ? Object.FindFirstObjectByType<StealResourcePage>() : null;
                if (steal != null)
                {
                    System.Collections.IEnumerable els =
                        Traverse.Create(steal).Field("playerResourceElements").GetValue() as System.Collections.IEnumerable;
                    if (els != null)
                    {
                        foreach (object o in els)
                        {
                            PlayerResourceElement pre = o as PlayerResourceElement;
                            if (pre != null)
                            {
                                PlayerResourceElement e = pre;
                                AddTarget(e.transform, () => e.OnClick());
                            }
                        }
                    }
                    return;
                }

                CardActionsPopup actionsPopup =
                    PageOpen(EPage.CardActionsPopup) ? Object.FindFirstObjectByType<CardActionsPopup>() : null;
                if (actionsPopup != null)
                {
                    foreach (CardActionElement element in actionsPopup.GetComponentsInChildren<CardActionElement>())
                    {
                        CardActionElement el = element;
                        AddTarget(el.transform, () => el.OnActionButtonTriggered());
                    }
                    return;
                }

                // Standard Projects / Milestones / Awards tab (ActionPopup): number each
                // currently-usable row (its Use button is shown) top-to-bottom, and the
                // number presses that row's Use button.
                ActionPopup actionPopup =
                    PageOpen(EPage.ActionPopup) ? Object.FindFirstObjectByType<ActionPopup>() : null;
                if (actionPopup != null)
                {
                    List<ActionElementBase> els = new List<ActionElementBase>();
                    foreach (ActionElementBase element in actionPopup.GetComponentsInChildren<ActionElementBase>())
                    {
                        if (element != null && element.isActiveAndEnabled
                            && IsOnScreen(element.transform) && ActionElementUsable(element))
                        {
                            els.Add(element);
                        }
                    }
                    els.Sort((a, b) => b.transform.position.y.CompareTo(a.transform.position.y));
                    foreach (ActionElementBase element in els)
                    {
                        ActionElementBase el = element;
                        AddTarget(el.transform, () => el.OnUseButtonPressed());
                    }
                    if (_targets.Count > 0)
                    {
                        return;
                    }
                }

                // Buy/keep selection (CardSelectionPage, "BUY UP TO N CARDS"): number
                // toggles that card's selection. Enumerate the page's own card list.
                CardSelectionPage selPage =
                    PageOpen(EPage.CardSelectionPage) ? Object.FindFirstObjectByType<CardSelectionPage>() : null;
                if (selPage != null && AddSelectableCardPreviews(selPage, "m_AllCardPreviews"))
                {
                    return;
                }

                // Sell / discard patents (SellPatentPopupPage, "SELECT CARD TO
                // DISCARD" / "SELL PATENTS"): number toggles that card's selection,
                // then the page's confirm button (Space) commits.
                SellPatentPopupPage sellPage =
                    PageOpen(EPage.SellPatentPopupPage) ? Object.FindFirstObjectByType<SellPatentPopupPage>() : null;
                if (sellPage != null && AddSelectableCardPreviews(sellPage, "m_AllCardPreviews"))
                {
                    return;
                }

                // "Select any resource" card-resource list: number selects that row.
                PlayerCardResourcePage resPage =
                    PageOpen(EPage.PlayerCardResourcePage) ? Object.FindFirstObjectByType<PlayerCardResourcePage>() : null;
                if (resPage != null)
                {
                    foreach (CardResourceElement row in CardResourceRows(resPage))
                    {
                        CardResourceElement r = row;
                        Button b = r.GetComponent<Button>();
                        if (b != null)
                        {
                            AddTarget(r.transform, () => b.onClick.Invoke());
                        }
                    }
                    if (_targets.Count > 0)
                    {
                        return;
                    }
                }

                // Hand browse grid (ViewPlayerCardsPage): number opens the card (or
                // presses its Select button if it has one). Enumerate the page's own
                // card list so board/tray cards are never numbered.
                ViewPlayerCardsPage viewPage =
                    PageOpen(EPage.ViewPlayerCardsPage) ? Object.FindFirstObjectByType<ViewPlayerCardsPage>() : null;
                if (viewPage != null)
                {
                    System.Collections.IEnumerable cardObjs =
                        Traverse.Create(viewPage).Field("m_CardObjects").GetValue() as System.Collections.IEnumerable;
                    List<CardPreview> grid = new List<CardPreview>();
                    if (cardObjs != null)
                    {
                        foreach (object o in cardObjs)
                        {
                            if (o is CardPreview cp && cp.isActiveAndEnabled && IsOnScreen(cp.transform))
                            {
                                grid.Add(cp);
                            }
                        }
                    }
                    List<(Transform t, System.Action act)> gridItems = new List<(Transform, System.Action)>();
                    foreach (CardPreview c in grid)
                    {
                        CardPreview card = c;
                        Button btn = CardButton(card);
                        gridItems.Add((card.transform,
                            btn != null ? (System.Action)(() => btn.onClick.Invoke()) : (() => card.ExpandCardToView())));
                    }
                    // Top row -> 1-4, second row -> Q W E R, column-aligned.
                    AddRowBasedTargets(gridItems);
                    // Also badge the sort/filter tabs (5-9) shown along the bottom.
                    PopulateSortTargets(viewPage);
                    if (_targets.Count > 0)
                    {
                        return;
                    }
                }

                // Card carousel (ExpandPlayerCardsPage): number any card that has an
                // active Use/Buy button. Scope the search to the carousel's own
                // children so this never scans the whole scene (the other selectable
                // contexts all have their own branch above).
                ExpandPlayerCardsPage carousel =
                    PageOpen(EPage.ExpandPlayerCardsPage) ? Object.FindFirstObjectByType<ExpandPlayerCardsPage>() : null;
                if (carousel == null)
                {
                    return;
                }
                List<BaseCardPreview> cards = new List<BaseCardPreview>();
                foreach (BaseCardPreview pv in carousel.GetComponentsInChildren<BaseCardPreview>())
                {
                    if (CardButton(pv) != null && IsOnScreen(pv.transform))
                    {
                        cards.Add(pv);
                    }
                }
                cards.Sort((a, b) =>
                {
                    float ay = Mathf.Round(a.transform.position.y / 40f);
                    float by = Mathf.Round(b.transform.position.y / 40f);
                    return ay != by
                        ? by.CompareTo(ay)
                        : a.transform.position.x.CompareTo(b.transform.position.x);
                });
                foreach (BaseCardPreview c in cards)
                {
                    BaseCardPreview card = c;
                    AddTarget(card.transform, () => PressCardButton(card));
                }
            }
            catch (System.Exception)
            {
            }
        }

        // Number every on-screen CardPreview in a page's private card list, where a
        // number activates that card's select toggle. Shared by the buy/keep page and
        // the sell/discard page, which both expose a List<CardPreview> field of the
        // same shape. Returns true if it numbered at least one card.
        private bool AddSelectableCardPreviews(object page, string listField)
        {
            System.Collections.IEnumerable objs =
                Traverse.Create(page).Field(listField).GetValue() as System.Collections.IEnumerable;
            List<CardPreview> cards = new List<CardPreview>();
            if (objs != null)
            {
                foreach (object o in objs)
                {
                    if (o is CardPreview cp && cp.isActiveAndEnabled && IsOnScreen(cp.transform))
                    {
                        cards.Add(cp);
                    }
                }
            }
            SortByScreenPosition(cards);
            foreach (CardPreview c in cards)
            {
                CardPreview card = c;
                AddTarget(card.transform, () => card.OnSelectCard());
            }
            return _targets.Count > 0;
        }

        // The selectable rows of a card-resource page ("SELECT ANY RESOURCE"),
        // top to bottom, filtered to the ones you can actually pick.
        private static List<CardResourceElement> CardResourceRows(PlayerCardResourcePage page)
        {
            List<CardResourceElement> rows = new List<CardResourceElement>();
            System.Collections.IEnumerable els =
                Traverse.Create(page).Field("m_CardResourceElements").GetValue() as System.Collections.IEnumerable;
            if (els != null)
            {
                foreach (object o in els)
                {
                    if (o is CardResourceElement e && e.isActiveAndEnabled && IsOnScreen(e.transform))
                    {
                        Button b = e.GetComponent<Button>();
                        if (b != null && b.interactable)
                        {
                            rows.Add(e);
                        }
                    }
                }
            }
            SortByScreenPosition(rows);
            return rows;
        }

        // Up/Down move the highlighted row on a card-resource page. Returns true if
        // such a page is open (so the arrows don't fall through to other handlers).
        private static bool NavigateCardResource(bool down)
        {
            try
            {
                PlayerCardResourcePage page = Object.FindFirstObjectByType<PlayerCardResourcePage>();
                if (page == null)
                {
                    return false;
                }
                List<CardResourceElement> rows = CardResourceRows(page);
                if (rows.Count > 0)
                {
                    int selected = Traverse.Create(page).Field("m_SelectedCardID").GetValue<int>();
                    int idx = rows.FindIndex(r => r.CardID == selected);
                    if (idx < 0)
                    {
                        idx = 0;
                    }
                    int next = Mathf.Clamp(idx + (down ? 1 : -1), 0, rows.Count - 1);
                    if (next != idx)
                    {
                        rows[next].GetComponent<Button>()?.onClick.Invoke();
                    }
                }
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // The card's own Select/Use/Buy button, only if it is currently clickable.
        private static Button CardButton(BaseCardPreview card)
        {
            try
            {
                Button btn = Traverse.Create(card).Field("m_Btn").GetValue<Button>();
                if (btn != null && btn.interactable && btn.gameObject.activeInHierarchy)
                {
                    return btn;
                }
            }
            catch (System.Exception)
            {
            }
            return null;
        }

        private static void PressCardButton(BaseCardPreview card)
        {
            CardButton(card)?.onClick.Invoke();
        }

        // The scroll viewport (mask) of the card page being numbered, set at the top
        // of RefreshNumberedTargets. Cards on adjacent pages peek past this mask but
        // stay within the screen, so a screen-bounds test alone numbers them; clipping
        // to the mask keeps numbering to the cards on the current page.
        private static RectTransform s_activeViewport;
        private static readonly Vector3[] s_vpCorners = new Vector3[4];

        // The mask RectTransform of whichever card scroller is open, or null if none.
        private static RectTransform ActiveScrollViewport()
        {
            ScrollSnapRect snap = FindActiveCardScroll();
            if (snap == null)
            {
                return null;
            }
            ScrollRect sr = snap.GetComponent<ScrollRect>();
            if (sr != null && sr.viewport != null)
            {
                return sr.viewport;
            }
            return snap.transform as RectTransform;
        }

        // True if the item is visible on the current page. Vertically it must sit
        // within the screen; horizontally, when a scroller is open, the majority of it
        // must fall inside the scroll mask so a card peeking in from the next page
        // (only an edge showing) is not numbered.
        private static bool IsOnScreen(Transform tr)
        {
            RectTransform rt = tr as RectTransform;
            if (rt == null)
            {
                return false;
            }
            rt.GetWorldCorners(s_corners);
            const float m = 8f; // tolerance for cards clipped slightly by the panel frame
            float left = s_corners[0].x;
            float right = s_corners[2].x;
            float bottom = s_corners[0].y;
            float top = s_corners[1].y;
            if (bottom < -m || top > Screen.height + m)
            {
                return false;
            }
            if (s_activeViewport != null)
            {
                s_activeViewport.GetWorldCorners(s_vpCorners);
                float width = right - left;
                if (width <= 0f)
                {
                    return false;
                }
                float visible = Mathf.Min(right, s_vpCorners[2].x) - Mathf.Max(left, s_vpCorners[0].x);
                return visible >= width * 0.5f;
            }
            return left >= -m && right <= Screen.width + m;
        }

        // Order UI items top row first, then left to right (screen coords).
        private static void SortByScreenPosition<T>(List<T> items) where T : Component
        {
            items.Sort((a, b) =>
            {
                float ay = Mathf.Round(a.transform.position.y / 40f);
                float by = Mathf.Round(b.transform.position.y / 40f);
                return ay != by
                    ? by.CompareTo(ay)
                    : a.transform.position.x.CompareTo(b.transform.position.x);
            });
        }

        private void ActivateNumberedTarget(int zeroBasedIndex)
        {
            if (zeroBasedIndex < 0 || zeroBasedIndex >= _targets.Count)
            {
                return;
            }
            try
            {
                _targets[zeroBasedIndex].act();
            }
            catch (System.Exception)
            {
            }
        }

        private void ActivateSortTarget(int zeroBasedIndex)
        {
            if (zeroBasedIndex < 0 || zeroBasedIndex >= _sortTargets.Count)
            {
                return;
            }
            try
            {
                _sortTargets[zeroBasedIndex].act();
            }
            catch (System.Exception)
            {
            }
        }

        // Fill _sortTargets with the hand view's sort/filter tabs (COST, PLAYABILITY,
        // CARD TYPE, TAGS, CHRONOLOGICAL), ordered left-to-right, so 5-9 press them.
        private void PopulateSortTargets(ViewPlayerCardsPage viewPage)
        {
            try
            {
                GameObject container =
                    Traverse.Create(viewPage).Field("m_FilterContainer").GetValue<GameObject>();
                if (container == null || !container.activeInHierarchy)
                {
                    return;
                }
                List<Button> buttons = new List<Button>();
                foreach (Button b in container.GetComponentsInChildren<Button>())
                {
                    if (b != null && b.isActiveAndEnabled && IsOnScreen(b.transform))
                    {
                        buttons.Add(b);
                    }
                }
                buttons.Sort((a, b) => a.transform.position.x.CompareTo(b.transform.position.x));
                for (int i = 0; i < buttons.Count && i < s_sortKeys.Length; i++)
                {
                    Button btn = buttons[i];
                    _sortTargets.Add((btn.transform, () => btn.onClick.Invoke()));
                }
            }
            catch (System.Exception)
            {
            }
        }

        // True when an action row (standard project / milestone / award) is currently
        // usable, i.e. its Use button is shown and interactable rather than the
        // greyed "unavailable" button.
        private static bool ActionElementUsable(ActionElementBase element)
        {
            try
            {
                Button use = Traverse.Create(element).Field("UseButton").GetValue<Button>();
                return use != null && use.gameObject.activeInHierarchy && use.interactable;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private void TryTogglePopup(EPage page)
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                if (game != null && game.HUD != null && game.HUD.PlayerTray != null)
                {
                    // Toggling a popup closed counts as closing a panel (stop auto-reopen);
                    // toggling one open clears that so it can auto-reopen again.
                    bool wasOpen = UIManager.Instance.IsPageInStack(page);
                    UserClosedPanel = wasOpen;
                    if (wasOpen)
                    {
                        // A deliberate close: let it through the off-turn suppression
                        // (covers the async close-animation callback for ~0.5s).
                        UserClosingPanelFrames = 30;
                    }
                    game.HUD.PlayerTray.ShowPopup(page);
                }
            }
            catch (System.Exception)
            {
            }
        }

        // The "SELECT ONE" action-choice popup (e.g. Regolith Eaters' or-action).
        // Returns its controller when one is shown with choices, else null.
        internal static CardActionChoiceController FindActiveChoiceController()
        {
            try
            {
                CardActionChoiceController[] controllers =
                    Object.FindObjectsByType<CardActionChoiceController>(FindObjectsSortMode.None);
                foreach (CardActionChoiceController controller in controllers)
                {
                    if (!controller.isActiveAndEnabled)
                    {
                        continue;
                    }
                    System.Collections.IList choices =
                        Traverse.Create(controller).Field("m_CardActionChoices").GetValue() as System.Collections.IList;
                    if (choices != null && choices.Count > 0)
                    {
                        return controller;
                    }
                }
            }
            catch (System.Exception)
            {
            }
            return null;
        }

        // Up/Down move the highlighted option in the SELECT ONE popup, driving the
        // controller's own OnChoiceClicked so its highlight + cost panel stay correct.
        // The "spend X energy -> gain X MC" style amount panel (ResourceConversionController).
        internal static ResourceConversionController FindActiveConversionController()
        {
            try
            {
                foreach (ResourceConversionController c in
                    Object.FindObjectsByType<ResourceConversionController>(FindObjectsSortMode.None))
                {
                    if (c.isActiveAndEnabled)
                    {
                        return c;
                    }
                }
            }
            catch (System.Exception)
            {
            }
            return null;
        }

        // Nudge the amount on the active conversion panel; returns true if one exists.
        private static bool AdjustConversion(int delta)
        {
            try
            {
                ResourceConversionController ctrl = FindActiveConversionController();
                if (ctrl == null)
                {
                    return false;
                }
                ResourceConversionPanel panel =
                    Traverse.Create(ctrl).Field("resourceConversionPanel").GetValue<ResourceConversionPanel>();
                if (panel == null)
                {
                    return false;
                }
                panel.SetResourceAmount(panel.CurrentAmount + delta);
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // Nudge the steel/titanium amount on an open card's DECREASE COST panel;
        // returns true if such a panel is open (so arrows adjust payment instead of
        // paging cards).
        private static bool AdjustDecreaseCost(int delta)
        {
            try
            {
                // The card carousel keeps every hand card in a play state at once, so
                // several DECREASE COST controllers are live simultaneously. Only the
                // centred card's panel is on screen, so require the panel to be visible
                // (an off-screen adjacent card's panel adjusts nothing the user sees).
                // Match the game's own criterion: a panel is a candidate when its
                // component is enabled (the resource applies) with room to move.
                foreach (CardCostDecreaseController c in
                    Object.FindObjectsByType<CardCostDecreaseController>(FindObjectsSortMode.None))
                {
                    if (c == null || !c.isActiveAndEnabled || c.ResourceConversionPanels == null)
                    {
                        continue;
                    }
                    foreach (ResourceConversionPanel panel in c.ResourceConversionPanels)
                    {
                        if (panel != null && panel.enabled
                            && panel.MaxResourceAmount > panel.MinResourceAmount
                            && IsCentreOnScreen(panel.transform))
                        {
                            panel.SetResourceAmount(panel.CurrentAmount + delta);
                            return true;
                        }
                    }
                }
            }
            catch (System.Exception)
            {
            }
            return false;
        }

        // True if a UI element's centre sits within the screen. Used to pick the
        // visible one of several identical panels the carousel keeps live at once.
        private static bool IsCentreOnScreen(Transform tr)
        {
            RectTransform rt = tr as RectTransform;
            if (rt == null)
            {
                return true; // not a rect; don't exclude it
            }
            rt.GetWorldCorners(s_corners);
            float cx = (s_corners[0].x + s_corners[2].x) * 0.5f;
            float cy = (s_corners[0].y + s_corners[1].y) * 0.5f;
            return cx >= 0f && cx <= Screen.width && cy >= 0f && cy <= Screen.height;
        }

        private void NavigateChoice(bool down)
        {
            // Up = more, Down = less on any open amount / payment panel.
            if (AdjustConversion(down ? -1 : 1) || AdjustDecreaseCost(down ? -1 : 1))
            {
                return;
            }
            // Up/Down move the highlighted row on the "select any resource" list.
            if (NavigateCardResource(down))
            {
                return;
            }
            try
            {
                CardActionChoiceController controller = FindActiveChoiceController();
                if (controller == null)
                {
                    NavigateStealResource(down);
                    return;
                }
                Traverse t = Traverse.Create(controller);
                System.Collections.IList choices =
                    t.Field("m_CardActionChoices").GetValue() as System.Collections.IList;
                if (choices == null || choices.Count == 0)
                {
                    return;
                }
                List<int> indices = new List<int>();
                foreach (object choice in choices)
                {
                    indices.Add(Traverse.Create(choice).Property("Index").GetValue<int>());
                }
                int selected = t.Field("SelectedAction").GetValue<int>();
                int position = indices.IndexOf(selected);
                int newPosition;
                if (position < 0)
                {
                    newPosition = down ? 0 : indices.Count - 1;
                }
                else
                {
                    newPosition = down
                        ? System.Math.Min(position + 1, indices.Count - 1)
                        : System.Math.Max(position - 1, 0);
                }
                t.Method("OnChoiceClicked", indices[newPosition]).GetValue();
            }
            catch (System.Exception)
            {
            }
        }

        // Up/Down through the "Select any production/resource" entries.
        private void NavigateStealResource(bool down)
        {
            try
            {
                StealResourcePage steal = Object.FindFirstObjectByType<StealResourcePage>();
                if (steal == null)
                {
                    return;
                }
                Traverse st = Traverse.Create(steal);
                System.Collections.IEnumerable els =
                    st.Field("playerResourceElements").GetValue() as System.Collections.IEnumerable;
                if (els == null)
                {
                    return;
                }
                List<PlayerResourceElement> list = new List<PlayerResourceElement>();
                foreach (object o in els)
                {
                    if (o is PlayerResourceElement pre)
                    {
                        list.Add(pre);
                    }
                }
                if (list.Count == 0)
                {
                    return;
                }
                int selectedId = st.Field("m_SelectedPlayerID").GetValue<int>();
                int position = list.FindIndex(e => e.PlayerID == selectedId);
                int newPosition;
                if (position < 0)
                {
                    newPosition = down ? 0 : list.Count - 1;
                }
                else
                {
                    newPosition = down
                        ? System.Math.Min(position + 1, list.Count - 1)
                        : System.Math.Max(position - 1, 0);
                }
                list[newPosition].OnClick();
            }
            catch (System.Exception)
            {
            }
        }

        // Left/Right: page the open card carousel (ExpandPlayerCardsPage); if none
        // is open, fall back to moving the SELECT ONE choice highlight.
        private void NavigateHorizontal(bool right)
        {
            // Right = more, Left = less on an open amount / card-payment panel. This
            // takes priority over paging, so with a card open the arrows change the
            // steel/titanium payment instead of flipping to another card.
            if (AdjustConversion(right ? 1 : -1) || AdjustDecreaseCost(right ? 1 : -1))
            {
                return;
            }
            try
            {
                ScrollSnapRect snap = FindActiveCardScroll();
                if (snap != null)
                {
                    int current = Traverse.Create(snap).Field("_currentPage").GetValue<int>();
                    snap.LerpToPage(current + (right ? 1 : -1));
                    return;
                }
                NavigateChoice(down: right);
            }
            catch (System.Exception)
            {
            }
        }

        // The paging scroller of whichever card page is open (carousel, hand browse
        // grid, or buy/keep selection).
        private static ScrollSnapRect FindActiveCardScroll()
        {
            // Each scan is gated on its page being open so this is cheap when no card
            // scroller is up (the common case while the highlight key is held on the board).
            if (PageOpen(EPage.ExpandPlayerCardsPage))
            {
                ExpandPlayerCardsPage carousel = Object.FindFirstObjectByType<ExpandPlayerCardsPage>();
                if (carousel != null && carousel.ScrollSnapRect != null)
                {
                    return carousel.ScrollSnapRect;
                }
            }
            if (PageOpen(EPage.ViewPlayerCardsPage))
            {
                ViewPlayerCardsPage grid = Object.FindFirstObjectByType<ViewPlayerCardsPage>();
                ScrollSnapRect s = (grid != null) ? grid.GetComponentInChildren<ScrollSnapRect>() : null;
                if (s != null)
                {
                    return s;
                }
            }
            if (PageOpen(EPage.CardSelectionPage))
            {
                CardSelectionPage select = Object.FindFirstObjectByType<CardSelectionPage>();
                ScrollSnapRect s = (select != null) ? select.GetComponentInChildren<ScrollSnapRect>() : null;
                if (s != null)
                {
                    return s;
                }
            }
            if (PageOpen(EPage.SellPatentPopupPage))
            {
                SellPatentPopupPage sell = Object.FindFirstObjectByType<SellPatentPopupPage>();
                ScrollSnapRect s = (sell != null) ? sell.GetComponentInChildren<ScrollSnapRect>() : null;
                if (s != null)
                {
                    return s;
                }
            }
            return null;
        }

        // True only while a text field is ACTIVELY focused (accepting keystrokes),
        // so letter/space hotkeys don't fire mid-typing. A field that is merely the
        // selected object but not being edited (e.g. chat auto-selected on turn
        // start) must NOT block hotkeys, otherwise they appear dead until you click
        // elsewhere to deselect it.
        private static bool IsTextInputFocused()
        {
            try
            {
                EventSystem es = EventSystem.current;
                GameObject selected = (es != null) ? es.currentSelectedGameObject : null;
                if (selected == null)
                {
                    return false;
                }
                foreach (MonoBehaviour component in selected.GetComponents<MonoBehaviour>())
                {
                    if (component == null || !component.GetType().Name.Contains("InputField"))
                    {
                        continue;
                    }
                    // Only block while you are actually composing: the field must be
                    // focused AND already contain text. A chat field that is merely
                    // selected/focused but empty (a common stray state the game leaves
                    // after a turn) must NOT block, or the hotkeys look dead until you
                    // click elsewhere to deselect it.
                    Traverse focused = Traverse.Create(component).Property("isFocused");
                    if (focused.PropertyExists() && !focused.GetValue<bool>())
                    {
                        return false;
                    }
                    Traverse text = Traverse.Create(component).Property("text");
                    string value = text.PropertyExists() ? text.GetValue<string>() : null;
                    return !string.IsNullOrEmpty(value);
                }
            }
            catch (System.Exception)
            {
            }
            return false;
        }

        // After a keyboard confirm (the game's own Enter, or the mod's Space), Unity
        // leaves the pressed button selected, so the game keeps routing the next
        // Submit/Move (Space, Enter, arrows) to that stale element until you click
        // elsewhere. Deselect it so keyboard control returns without a click. Text
        // fields are left selected so chat typing still works.
        private static void ClearUiFocus()
        {
            try
            {
                EventSystem es = EventSystem.current;
                GameObject selected = (es != null) ? es.currentSelectedGameObject : null;
                if (selected == null)
                {
                    return;
                }
                foreach (MonoBehaviour component in selected.GetComponents<MonoBehaviour>())
                {
                    if (component != null && component.GetType().Name.Contains("InputField"))
                    {
                        return; // a text field is focused; leave it for typing
                    }
                }
                es.SetSelectedGameObject(null);
            }
            catch (System.Exception)
            {
            }
        }

        // Max non-Pass items the queue holds (a turn is two actions). Pass is excluded
        // from the cap and always fires last.
        private const int PrepareMaxItems = 2;

        // Add an action to the queue, capped at PrepareMaxItems: if it is already full,
        // the most recently added action is pushed out to make room for the new one.
        private void EnqueuePrepared(PreparedItem item)
        {
            if (item.IsPass)
            {
                return; // Pass is toggled elsewhere, not enqueued here
            }
            int nonPass = 0;
            for (int i = 0; i < _prepareQueue.Count; i++)
            {
                if (!_prepareQueue[i].IsPass)
                {
                    nonPass++;
                }
            }
            if (nonPass >= PrepareMaxItems)
            {
                for (int i = _prepareQueue.Count - 1; i >= 0; i--)
                {
                    if (!_prepareQueue[i].IsPass)
                    {
                        _prepareQueue.RemoveAt(i); // push out the last-added action
                        break;
                    }
                }
            }
            _prepareQueue.Add(item);
        }

        // Edit the queue while the Prepare panel is open. Items are added from source
        // (add-key + badge) and Pass via its own key; here only Backspace (undo the last
        // add) and Delete (clear) apply. Returns true if it consumed the key.
        private bool HandlePreparePanelInput()
        {
            if (Input.GetKeyDown(KeyCode.Delete))
            {
                _prepareQueue.Clear();
                return true;
            }
            if (Input.GetKeyDown(KeyCode.Backspace))
            {
                if (_prepareQueue.Count > 0)
                {
                    _prepareQueue.RemoveAt(_prepareQueue.Count - 1); // undo the last add
                }
                return true;
            }
            return false;
        }

        // My fixed seat's local id. GetMyPlayerLocalId() only works online; offline it
        // returns the CURRENT player, so "is it my turn" would always be true and the
        // queue would fire on every player's turn. Offline we instead find the human-
        // controlled seat (unique in solo-vs-AI), which is stable across turns.
        internal static int MyStableLocalId(TM_Game game, TM_GameInfo info)
        {
            try
            {
                if (info.IsOnline)
                {
                    return info.GetMyPlayerLocalId();
                }
                if (game.GameData != null && game.GameData.PlayerBoardData != null)
                {
                    foreach (KeyValuePair<int, TM_PlayerBoardData> kv in game.GameData.PlayerBoardData)
                    {
                        TM_Player p = info.GetPlayerWithLocalId(kv.Key);
                        if (p != null && p.TryGetAgentAsHuman(out TM_HumanAgent _))
                        {
                            return kv.Key;
                        }
                    }
                }
            }
            catch (System.Exception)
            {
            }
            return info.GetMyPlayerLocalId(); // fallback
        }

        // Convenience: my stable seat id from the live game, or -1 if unavailable.
        internal static int MyStableLocalId()
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return -1;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                TM_GameInfo info = game != null ? game.GameInfo : null;
                return info != null ? MyStableLocalId(game, info) : -1;
            }
            catch (System.Exception)
            {
                return -1;
            }
        }

        // The current event is a GameActionEvent (the game is waiting for an action).
        // Reflection avoids referencing the game's event namespace. Combined with the
        // my-turn check, this is the exact gate HandleSelectActionSelection uses.
        private static bool CurrentEventIsGameAction(TM_Game game)
        {
            try
            {
                object ep = Traverse.Create(game).Method("GetEventProvider").GetValue();
                if (ep == null)
                {
                    return false;
                }
                object ev = Traverse.Create(ep).Property("CurrentEvent").GetValue();
                return ev != null && ev.GetType().Name == "GameActionEvent";
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private void DrawPreparePanel()
        {
            if (!_showPrepare)
            {
                return;
            }
            if (s_prepareStyle == null)
            {
                s_prepareStyle = new GUIStyle
                {
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                    richText = true,
                    alignment = TextAnchor.MiddleLeft,
                };
                s_prepareStyle.normal.textColor = Color.white;
            }
            // Ordered rows: the actions in play order, then Pass (always last).
            List<string> rows = new List<string>();
            int n = 0;
            foreach (PreparedItem it in _prepareQueue)
            {
                if (!it.IsPass)
                {
                    rows.Add((++n) + ".  " + it.label);
                }
            }
            foreach (PreparedItem it in _prepareQueue)
            {
                if (it.IsPass)
                {
                    rows.Add("+   Pass");
                    break;
                }
            }
            float rowH = 30f;
            float height = 16f + (rows.Count + 1) * rowH;
            GUILayout.BeginArea(new Rect(12f, Screen.height - height - 40f, 460f, height), GUI.skin.box);
            GUILayout.Label("<color=#33f2a0>QUEUE:</color>", s_prepareStyle);
            if (rows.Count == 0)
            {
                GUILayout.Label("   <color=#888888>(empty)</color>", s_prepareStyle);
            }
            else
            {
                foreach (string r in rows)
                {
                    GUILayout.Label("   " + r, s_prepareStyle);
                }
            }
            GUILayout.EndArea();
        }

        // Auto-fire the prepared queue once it is the player's action turn. Runs one
        // step per call: submit an action, then on later frames press Yes on the game's
        // own confirm dialog (or nothing if the player has confirmations off), pause a
        // beat for the event to resolve, then move on. Stops when actions run out (any
        // unfired entries stay queued for the next turn) or nothing is affordable.
        private void FirePreparedActions()
        {
            // Stay active while a confirm from the last fired action is still pending,
            // even though the queue is already empty - otherwise the final action's
            // Yes/No never gets pressed and it reverts.
            if (_prepareQueue.Count == 0 && !_fireAwaitConfirm)
            {
                return;
            }
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                TM_GameInfo info = game != null ? game.GameInfo : null;
                if (info == null)
                {
                    return;
                }
                TM_Player me = info.GetMyPlayer();
                int myId = MyStableLocalId(game, info);
                bool myTurn = info.UserAllowedToPlayLocalID == myId;

                if (me == null || !myTurn)
                {
                    _fireAwaitConfirm = false; // drop stale confirm state; it isn't my turn
                    return;
                }

                // Confirm the game's Yes/No that our previous submit raised. Poll every
                // frame (not gated by the cooldown) and press Yes the moment the button
                // is actually interactable, since the dialog fades in a beat after submit.
                // If no dialog ever appears within the window, the player has confirmations
                // off (the action already ran), so just clear the flag.
                if (_fireAwaitConfirm)
                {
                    _fireConfirmElapsed += Time.unscaledDeltaTime;
                    GenericPopup popup = Object.FindFirstObjectByType<GenericPopup>();
                    if (popup != null)
                    {
                        if (PressDefaultPopupButton(popup))
                        {
                            Logger.LogInfo("Prepare: confirmed (Yes pressed) after " + _fireConfirmElapsed.ToString("0.00") + "s");
                            _fireAwaitConfirm = false;
                            _fireConfirmElapsed = 0f;
                            _fireCooldown = 0.5f; // let the action resolve before the next
                        }
                        else if (_fireConfirmElapsed >= 3f)
                        {
                            Logger.LogWarning("Prepare: gave up - a confirm dialog is open but its Yes/Ok button was never pressable");
                            _fireAwaitConfirm = false;
                            _fireConfirmElapsed = 0f;
                        }
                        return; // popup up but Yes not pressable yet -> keep polling
                    }
                    if (_fireConfirmElapsed >= 1.25f)
                    {
                        Logger.LogInfo("Prepare: no confirm dialog appeared (confirmations off?) - assuming it ran");
                        _fireAwaitConfirm = false;
                        _fireConfirmElapsed = 0f;
                        _fireCooldown = 0.3f;
                    }
                    return;
                }

                _fireCooldown -= Time.unscaledDeltaTime;
                if (_fireCooldown > 0f)
                {
                    return;
                }

                if (me.NbActionsLeft <= 0)
                {
                    return; // out of actions this turn; keep the rest queued
                }

                // If the next non-Pass item is a Card, drive it here - before the guard
                // below, because opening the card's play view itself trips ModDialogOpen.
                // Insta-plays it: navigate to the card and press its play button. Waits
                // during a real hand-off (target / placement / choice / dialog); once the
                // player finishes that, the queue resumes.
                int frontIdx = -1;
                for (int i = 0; i < _prepareQueue.Count; i++)
                {
                    if (!_prepareQueue[i].IsPass) { frontIdx = i; break; }
                }
                if (frontIdx >= 0 && _prepareQueue[frontIdx].kind == PreparedKind.Card)
                {
                    if (!CurrentEventIsGameAction(game) || HandOffInProgress())
                    {
                        return; // not action-ready, or the player is mid hand-off
                    }
                    PreparedItem card = _prepareQueue[frontIdx];
                    if (NavigateToCard(card.arg))
                    {
                        Logger.LogInfo("Prepare: played " + card.label);
                        _prepareQueue.RemoveAt(frontIdx);
                        _cardNavElapsed = 0f;
                        _fireAwaitConfirm = true; // auto-press the "Play this card?" payment confirm
                        _fireConfirmElapsed = 0f;
                        _fireCooldown = 0.5f;
                    }
                    else
                    {
                        _cardNavElapsed += Time.unscaledDeltaTime;
                        if (_cardNavElapsed >= 3f)
                        {
                            Logger.LogWarning("Prepare: card not playable (unaffordable / not in hand), skipping " + card.label);
                            _prepareQueue.RemoveAt(frontIdx);
                            _cardNavElapsed = 0f;
                        }
                        _fireCooldown = 0.2f;
                    }
                    return;
                }

                // Only submit when the game is actually waiting for an action (my turn is
                // already ensured above), the HUD is idle and interactable (goes false
                // while an action resolves), and no unrelated dialog is already open.
                // Also hold off while you are still adding from source (add-key held) or
                // the standard-projects/milestones/awards panel is open, so on-turn adds
                // don't fire mid-add and we submit with that panel closed.
                if (!CurrentEventIsGameAction(game)
                    || game.HUD == null || !game.HUD.HasFunctionalitiesEnabled
                    || AddHeld() || PageOpen(EPage.ActionPopup) || ModDialogOpen()
                    || Object.FindFirstObjectByType<GenericPopup>() != null)
                {
                    return;
                }

                // Fire the next affordable action first. Pass is never fired here; it
                // waits until it is the only thing left in the queue (so it always goes
                // last, after every other action has played).
                for (int i = 0; i < _prepareQueue.Count; i++)
                {
                    PreparedItem item = _prepareQueue[i];
                    if (item.IsPass)
                    {
                        continue;
                    }
                    // Blue card action: open the popup and click it. Atomic ones run (and
                    // any Yes/No is auto-confirmed); ones that raise a choice hand off to
                    // the user (the ModDialogOpen guard pauses us until they finish).
                    if (item.kind == PreparedKind.CardAction)
                    {
                        if (NavigateToCardAction(item.arg))
                        {
                            Logger.LogInfo("Prepare: triggered " + item.label);
                            _prepareQueue.RemoveAt(i);
                            _cardNavElapsed = 0f;
                            _fireAwaitConfirm = true; // catch a simple confirm if one appears
                            _fireConfirmElapsed = 0f;
                            _fireCooldown = 0.35f;
                        }
                        else
                        {
                            _cardNavElapsed += Time.unscaledDeltaTime;
                            if (_cardNavElapsed >= 3f)
                            {
                                Logger.LogWarning("Prepare: card action not available, skipping " + item.label);
                                _prepareQueue.RemoveAt(i);
                                _cardNavElapsed = 0f;
                            }
                            _fireCooldown = 0.2f;
                        }
                        return;
                    }
                    if (!CanFire(game, myId, item))
                    {
                        continue;
                    }
                    Logger.LogInfo("Prepare: firing " + item.label + " (NbActionsLeft=" + me.NbActionsLeft + ")");
                    SubmitPrepared(info, item);
                    _prepareQueue.RemoveAt(i);
                    _fireAwaitConfirm = true;
                    _fireConfirmElapsed = 0f;
                    _fireCooldown = 0.35f;
                    return;
                }
                // Everything but Pass is done: end the turn as the final step.
                bool onlyPassLeft = true;
                bool hasPass = false;
                foreach (PreparedItem item in _prepareQueue)
                {
                    if (item.IsPass)
                    {
                        hasPass = true;
                    }
                    else
                    {
                        onlyPassLeft = false; // a real action is stuck (e.g. can't afford)
                    }
                }
                if (onlyPassLeft && hasPass)
                {
                    Logger.LogInfo("Prepare: passing / ending turn (final queued step)");
                    _prepareQueue.RemoveAll(x => x.IsPass);
                    TryEndTurn();
                    _fireAwaitConfirm = true; // confirm the "do you want to pass?" dialog
                    _fireConfirmElapsed = 0f;
                    _fireCooldown = 0.35f;
                    return;
                }
            }
            catch (System.Exception)
            {
            }
        }

        // Bring a queued card to its play view so the player can finish it. One step per
        // call: open the hand if closed, else find the card in the grid and open it.
        // Returns true once the card has been opened (or the play view is already up),
        // false while still working. Cards on another grid page aren't reachable here.
        private bool NavigateToCard(int cardId)
        {
            try
            {
                if (PageOpen(EPage.ExpandPlayerCardsPage))
                {
                    return PressOpenCardPlayButton(); // insta-confirm the play
                }
                if (PageOpen(EPage.ViewPlayerCardsPage))
                {
                    ViewPlayerCardsPage vp = Object.FindFirstObjectByType<ViewPlayerCardsPage>();
                    System.Collections.IEnumerable cardObjs = vp != null
                        ? Traverse.Create(vp).Field("m_CardObjects").GetValue() as System.Collections.IEnumerable
                        : null;
                    if (cardObjs != null)
                    {
                        foreach (object o in cardObjs)
                        {
                            if (o is CardPreview cp && cp.CardId == cardId && cp.isActiveAndEnabled)
                            {
                                Button btn = CardButton(cp);
                                if (btn != null)
                                {
                                    btn.onClick.Invoke();
                                }
                                else
                                {
                                    cp.ExpandCardToView();
                                }
                                return false; // only opened the play view; press Play next cycle
                            }
                        }
                    }
                    return false; // not on the visible grid page
                }
                OpenHand();
                return false; // opening the hand; retry next cycle
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // Press the play/use button on the card currently shown in the play view. Returns
        // true once clicked; false if the view/button isn't ready (e.g. unaffordable).
        private static bool PressOpenCardPlayButton()
        {
            try
            {
                ExpandPlayerCardsPage page = Object.FindFirstObjectByType<ExpandPlayerCardsPage>();
                if (page == null)
                {
                    return false;
                }
                int currentCardId = Traverse.Create(page).Field("m_CurrentCardID").GetValue<int>();
                foreach (BigCardPreview preview in Object.FindObjectsByType<BigCardPreview>(FindObjectsSortMode.None))
                {
                    if (preview.CardId != currentCardId)
                    {
                        continue;
                    }
                    Button button = Traverse.Create(preview).Field("m_Btn").GetValue<Button>();
                    if (button != null && button.interactable && button.gameObject.activeInHierarchy)
                    {
                        button.onClick.Invoke();
                        return true;
                    }
                    return false; // found the card but its play button isn't pressable yet
                }
            }
            catch (System.Exception)
            {
            }
            return false;
        }

        // True when the game is waiting for MY action input (my action turn). Used to
        // decide auto-cache: off-turn caches, on-turn plays.
        private static bool IsMyActionTurn()
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return false;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                TM_GameInfo info = game != null ? game.GameInfo : null;
                if (info == null)
                {
                    return false;
                }
                return info.UserAllowedToPlayLocalID == MyStableLocalId(game, info);
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // A context where a badge press must SELECT/activate, never cache: the buy/keep
        // picker, sell, steal, resource pick, card-action choice, or the card play view.
        // In these, cache mode is suppressed so you can still pick cards normally.
        private static bool IsSelectionContext()
        {
            return PageOpen(EPage.CardSelectionPage) || PageOpen(EPage.SellPatentPopupPage)
                || PageOpen(EPage.StealResourcePage) || PageOpen(EPage.PlayerCardResourcePage)
                || PageOpen(EPage.ChooseCardActionCardPage) || PageOpen(EPage.ExpandPlayerCardsPage);
        }

        // A user-facing selection is in progress (target / placement / choice / dialog)
        // that the mod must not drive through - it hands off to the player. Excludes our
        // own hand-browse and card play-view navigation.
        private static bool HandOffInProgress()
        {
            try
            {
                switch (TopPage())
                {
                    case EPage.CardSelectionPage:
                    case EPage.SellPatentPopupPage:
                    case EPage.StealResourcePage:
                    case EPage.PlayerCardResourcePage:
                        return true;
                }
                return Object.FindFirstObjectByType<GenericPopup>() != null
                    || FindActiveConversionController() != null
                    || FindActiveChoiceController() != null;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // Bring a queued blue-card action to the fore and trigger it. One step per call:
        // open the Card Actions popup if closed, else find the action by its card id and
        // click it. Returns true once clicked, false while still working.
        private bool NavigateToCardAction(int cardId)
        {
            try
            {
                if (!PageOpen(EPage.CardActionsPopup))
                {
                    TM_Game game = Singleton<GameManager>.Instance.Game;
                    if (game != null && game.HUD != null && game.HUD.PlayerTray != null)
                    {
                        game.HUD.PlayerTray.ShowPopup(EPage.CardActionsPopup);
                    }
                    return false; // opening; retry next cycle
                }
                CardActionsPopup popup = Object.FindFirstObjectByType<CardActionsPopup>();
                if (popup != null)
                {
                    foreach (CardActionElement el in popup.GetComponentsInChildren<CardActionElement>())
                    {
                        if (el == null || !el.isActiveAndEnabled)
                        {
                            continue;
                        }
                        if (el.CardId == cardId)
                        {
                            el.OnActionButtonTriggered();
                            return true;
                        }
                    }
                }
                return false; // action not present / already used
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // Conservative affordability guard so we never submit an action the player
        // can't pay for (which would raise a confirm we'd wrongly accept). Uses base
        // costs; awards vary so they are not pre-checked (the game still validates).
        private static bool CanFire(TM_Game game, int myId, PreparedItem item)
        {
            try
            {
                TM_PlayerBoardData board = game.GameData.GetPlayerBoardData(myId);
                if (board == null)
                {
                    return false;
                }
                int mc = board.ResourceBank[EResourceType.MegaCredit].Quantity;
                if (item.kind == PreparedKind.Conversion)
                {
                    // Greenery needs 8 plants; temperature needs 8 heat. Check the right one.
                    return board.ResourceBank[item.res].Quantity >= 8;
                }
                if (item.kind == PreparedKind.Action)
                {
                    if (item.actionType == EActionType.StandardProject)
                    {
                        if (item.arg == (int)EStandardProject.PowerPlant) return mc >= 11;
                        if (item.arg == (int)EStandardProject.Asteroid) return mc >= 14;
                        return false;
                    }
                    if (item.actionType == EActionType.Milestone) return mc >= 8;
                    if (item.actionType == EActionType.Award) return true; // cost varies
                }
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private static void SubmitPrepared(TM_GameInfo info, PreparedItem item)
        {
            switch (item.kind)
            {
                case PreparedKind.Conversion:
                    info.CurrentPlayer.HandleSelectActionSelection(EActionType.Conversion, item.res);
                    break;
                case PreparedKind.Action:
                    // Standard projects want a boxed EStandardProject; milestones/awards
                    // want a boxed int index. array[1] is the heat-to-spend (0).
                    object sel = item.actionType == EActionType.StandardProject
                        ? (object)new object[] { (EStandardProject)item.arg, 0 }
                        : new object[] { item.arg, 0 };
                    info.CurrentPlayer.HandleSelectActionSelection(item.actionType, sel);
                    break;
            }
        }

        private void HandleSpaceToConfirm()
        {
            if (!On(FeatHotkeys) || !Input.GetKeyDown(Key(KeyConfirm, KeyCode.Space))
                || (IsTextInputFocused() && !ModDialogOpen()))
            {
                return;
            }
            try
            {
                // "Spend X energy" style amount panel: Space confirms the amount.
                ResourceConversionController conversion = FindActiveConversionController();
                if (conversion != null)
                {
                    conversion.OnConfirm();
                    ClearUiFocus();
                    return;
                }

                // SELECT ONE action-choice popup: Space confirms the highlighted option.
                CardActionChoiceController choicePanel = FindActiveChoiceController();
                if (choicePanel != null)
                {
                    if (Traverse.Create(choicePanel).Field("SelectedAction").GetValue<int>() >= 0)
                    {
                        choicePanel.OnConfirm();
                        ClearUiFocus();
                    }
                    return;
                }

                // Card selection (SELECT CARD TO DISCARD / buy): Space presses the
                // Done/Discard button, but only when it is interactable (the required
                // number of cards is selected) so it can't confirm an invalid choice.
                CardSelectionPage selectPage = Object.FindFirstObjectByType<CardSelectionPage>();
                if (selectPage != null)
                {
                    Button doneButton = Traverse.Create(selectPage).Field("DoneButton").GetValue<Button>();
                    if (doneButton != null && doneButton.interactable && doneButton.gameObject.activeInHierarchy)
                    {
                        doneButton.onClick.Invoke();
                        ClearUiFocus();
                    }
                    return;
                }

                // "Select any production / resource" target popup: Space confirms the
                // highlighted player (Confirm self-guards on a valid selection).
                StealResourcePage stealPage = Object.FindFirstObjectByType<StealResourcePage>();
                if (stealPage != null)
                {
                    stealPage.Confirm();
                    ClearUiFocus();
                    return;
                }

                // "Select any resource" card-resource page: Space confirms the row.
                PlayerCardResourcePage resPage = Object.FindFirstObjectByType<PlayerCardResourcePage>();
                if (resPage != null)
                {
                    resPage.Confirm();
                    ClearUiFocus();
                    return;
                }

                // Sell Patents popup: Space presses Sell when at least the minimum
                // number of cards is selected (SellButton is interactable).
                SellPatentPopupPage sellPage = Object.FindFirstObjectByType<SellPatentPopupPage>();
                if (sellPage != null)
                {
                    Button sellButton = Traverse.Create(sellPage).Field("SellButton").GetValue<Button>();
                    if (sellButton != null && sellButton.interactable && sellButton.gameObject.activeInHierarchy)
                    {
                        sellButton.onClick.Invoke();
                        ClearUiFocus();
                    }
                    return;
                }

                // A confirmation dialog takes priority: Space is the default OK/Yes.
                // (Also the Beginner-corp "are you sure?" dialog, which is a GenericPopup
                // on top of the corporation popup, so this must precede the corp branch.)
                GenericPopup popup = Object.FindFirstObjectByType<GenericPopup>();
                if (popup != null)
                {
                    PressDefaultPopupButton(popup);
                    ClearUiFocus();
                    return;
                }

                // Corporation selection: Space confirms the highlighted corp (Next /
                // Select). SelectCorporationHandler self-guards and may raise the
                // Beginner-confirm dialog, handled by the GenericPopup branch above.
                CorporationSelectionPopup corpPopup =
                    PageOpen(EPage.CorporationSelectionPopup) ? Object.FindFirstObjectByType<CorporationSelectionPopup>() : null;
                if (corpPopup != null)
                {
                    corpPopup.SelectCorporationHandler();
                    ClearUiFocus();
                    return;
                }

                ExpandPlayerCardsPage page = Object.FindFirstObjectByType<ExpandPlayerCardsPage>();
                if (page == null)
                {
                    // Nothing else is open: Space does nothing. Skip / pass / end-turn is
                    // only on the Z key now (queue Pass; it fires on your turn).
                    return;
                }
                int currentCardId = Traverse.Create(page).Field("m_CurrentCardID").GetValue<int>();
                BigCardPreview[] previews =
                    Object.FindObjectsByType<BigCardPreview>(FindObjectsSortMode.None);
                foreach (BigCardPreview preview in previews)
                {
                    if (preview.CardId != currentCardId)
                    {
                        continue;
                    }
                    Button button = Traverse.Create(preview).Field("m_Btn").GetValue<Button>();
                    if (button != null && button.interactable && button.gameObject.activeInHierarchy)
                    {
                        button.onClick.Invoke();
                        ClearUiFocus();
                    }
                    break;
                }
            }
            catch (System.Exception)
            {
                // Non-fatal: ignore the keypress if anything is unexpected.
            }
        }

        // Press the board's end-turn button (labelled Pass / Skip / End Turn by the
        // game depending on actions left), only when it is actually shown.
        private static void TryEndTurn()
        {
            try
            {
                HUD_EndTurnPanel panel = Object.FindFirstObjectByType<HUD_EndTurnPanel>();
                if (panel != null && panel.IsEnabled)
                {
                    panel.OnEndTurnSelected();
                }
            }
            catch (System.Exception)
            {
            }
        }

        // Press the dialog's default confirm button (Yes, else Ok), whichever is
        // shown and interactable.
        private static bool PressDefaultPopupButton(GenericPopup popup)
        {
            return PressFirstActivePopupButton(popup, "DefaultYesButton", "DefaultOkButton");
        }

        // ESC: cancel an open confirm dialog by pressing its No / Close button, else
        // dismiss whichever mod panel is showing (scoreboard, then help overlay).
        private void HandleEscape()
        {
            try
            {
                GenericPopup popup = Object.FindFirstObjectByType<GenericPopup>();
                if (popup != null)
                {
                    PressFirstActivePopupButton(popup, "DefaultNoButton", "DefaultCloseButton");
                    ClearUiFocus();
                    return;
                }
            }
            catch (System.Exception)
            {
            }
            // Close whatever game window is on top (hand, tags/actions/resources/VP/
            // effects popups, the milestones/projects/awards popup, the card view).
            if (CloseActiveWindow())
            {
                return;
            }
            if (_showScoreboard)
            {
                _showScoreboard = false;
                return;
            }
            if (_showPrepare)
            {
                _showPrepare = false;
                return;
            }
            if (_showOverlay)
            {
                _showOverlay = false;
            }
        }

        // Close the top-most game window on Esc. Returns true if it closed one. Uses the
        // proper close path per window so tray state stays correct, and bypasses the
        // off-turn keep-open suppression (this is a deliberate close).
        private bool CloseActiveWindow()
        {
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return false;
                }
                EPage top = TopPage();
                switch (top)
                {
                    case EPage.ViewPlayerCardsPage:
                        ToggleHand();
                        return true;
                    case EPage.CardActionsPopup:
                    case EPage.CardResourcesPopup:
                    case EPage.CardVictoryPointsPopup:
                    case EPage.CardEffectsPopup:
                    case EPage.CardTagsPopup:
                        TryTogglePopup(top);
                        return true;
                    case EPage.ActionPopup:
                    {
                        ActionPopup ap = UIManager.Instance.GetPage<ActionPopup>(EPage.ActionPopup);
                        if (ap != null)
                        {
                            ap.OnCloseButtonClick();
                        }
                        return true;
                    }
                    case EPage.ExpandPlayerCardsPage:
                    {
                        UserClosingHand = true;
                        UserClosingPanelFrames = 30;
                        UIManager.Instance.Pop(EPage.ExpandPlayerCardsPage);
                        UserClosingHand = false;
                        return true;
                    }
                }
            }
            catch (System.Exception)
            {
            }
            return false;
        }

        // Press the first of the named popup button fields that is shown and
        // interactable. Shared by Space (Yes/Ok) and ESC (No/Close).
        private static bool PressFirstActivePopupButton(GenericPopup popup, params string[] fields)
        {
            foreach (string field in fields)
            {
                GameObject buttonObject = Traverse.Create(popup).Field(field).GetValue<GameObject>();
                if (buttonObject == null || !buttonObject.activeInHierarchy)
                {
                    continue;
                }
                Button button = buttonObject.GetComponent<Button>();
                if (button != null && button.interactable)
                {
                    button.onClick.Invoke();
                    return true;
                }
            }
            return false;
        }
    }

    // The tray's toggle buttons are blanket-disabled whenever HUD functionalities
    // are off (i.e. whenever it is not your turn / during opponent actions), via
    // HUD_PlayerTray.EnableAllButtons -> HUD_TogglePopupButton.EnableButton(false).
    // That also locks the read-only hand browser (EPage.ViewPlayerCardsPage), so
    // you cannot look at your own hand while an opponent acts.
    //
    // Keep only that one toggle interactable. ViewPlayerCardsPage opens the hand in
    // ViewHandCards mode, which has no play button, so this grants viewing only and
    // cannot submit an action.
    [HarmonyPatch(typeof(HUD_TogglePopupButton), nameof(HUD_TogglePopupButton.EnableButton))]
    internal static class KeepHandReadablePatch
    {
        private static void Prefix(HUD_TogglePopupButton __instance, ref bool aActive)
        {
            if (TfmCardRefreshPlugin.On(TfmCardRefreshPlugin.FeatHandReadable)
                && !aActive && __instance.GetPageType() == EPage.ViewPlayerCardsPage)
            {
                aActive = true;
            }
        }
    }

    // SelectAction fires whenever the game prompts you to pick an action: at the
    // start of your turn, and again after each action if you have one left. Open
    // the hand there so your projects are shown on turn start, and reopened after a
    // play instead of collapsing. This is the one safe window: any board interaction
    // from a previous action has already resolved, so opening can't strand the hand
    // over the board. Guarded by NbActionsLeft > 0 and no-op if already open.
    [HarmonyPatch(typeof(TM_HumanAgent), nameof(TM_HumanAgent.SelectAction))]
    internal static class OpenHandOnActionPromptPatch
    {
        private static void Postfix()
        {
            try
            {
                if (!TfmCardRefreshPlugin.On(TfmCardRefreshPlugin.FeatAutoOpenHand)
                    || !Singleton<GameManager>.IsInstanced)
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                TM_Player current = game?.GameInfo?.CurrentPlayer;
                if (current == null || current.NbActionsLeft <= 0)
                {
                    return;
                }
                TfmCardRefreshPlugin.ReopenPreferredPanel();
            }
            catch (System.Exception)
            {
                // Non-fatal: leave the panel closed if anything is unexpected.
            }
        }
    }

    // Remember what you last played so the auto-open reopens the matching panel:
    // projects for a card, actions for a blue-card action.
    [HarmonyPatch(typeof(TM_HumanAgent), nameof(TM_HumanAgent.HandlePlayCard))]
    internal static class MarkPlayedCardPatch
    {
        private static void Postfix()
        {
            TfmCardRefreshPlugin.LastPlayWasAction = false;
        }
    }

    [HarmonyPatch(typeof(TM_HumanAgent), nameof(TM_HumanAgent.HandlePlayBlueCardAction))]
    internal static class MarkPlayedActionPatch
    {
        private static void Postfix()
        {
            TfmCardRefreshPlugin.LastPlayWasAction = true;
        }
    }

    // Suppress all HUD callouts (the "<player>'s turn" bar, phase announcements,
    // etc.). HUD_Callout.Show already has a fast-path for reloading a game that
    // just fires the completion callback and skips the banner + its timed display;
    // we apply that same behavior always. Firing the callback keeps any game-flow
    // step that waits on the callout moving, so nothing stalls, and dropping the
    // timed display also removes the turn-start delay it caused.
    [HarmonyPatch(typeof(HUD_Callout), nameof(HUD_Callout.Show))]
    internal static class SuppressAnnouncementsPatch
    {
        private static bool Prefix(System.Action aCallback)
        {
            if (!TfmCardRefreshPlugin.On(TfmCardRefreshPlugin.FeatSuppressAnnouncements))
            {
                return true; // feature off: show the announcement normally
            }
            aCallback?.Invoke();
            return false;
        }
    }

    // CardPreview skips the playability evaluation in the read-only hand view
    // (ViewOnly) and in the buy / draft picker (BuyCard / SelectCard), so every
    // card shows at full brightness even when it cannot be played. Dim the ones
    // that are not currently playable so playable and unplayable read apart there.
    // In the hand view "not playable" means unmet requirements (temperature,
    // oxygen, oceans, tags, resources) OR unaffordable after steel/titanium/heat.
    // In the buy / draft picker only requirements are used: you are paying to
    // acquire the card, not to play it now, so its play cost is not the gate.
    [HarmonyPatch(typeof(CardPreview), "HandleState", new[] { typeof(ECardState) })]
    internal static class ShowHandPlayabilityInViewPatch
    {
        private static void Postfix(CardPreview __instance, ECardState aState)
        {
            bool handView = aState == ECardState.ViewOnly;
            bool pickView = aState == ECardState.BuyCard || aState == ECardState.SelectCard;
            if ((!handView && !pickView)
                || !TfmCardRefreshPlugin.On(TfmCardRefreshPlugin.FeatPlayabilityDim))
            {
                return;
            }
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return;
                }
                TM_Game game = Singleton<GameManager>.Instance.Game;
                if (game == null)
                {
                    return;
                }
                int playerId = __instance.PlayerID;
                int cardId = __instance.CardId;
                // In the hand view, only dim cards actually in this player's hand.
                // In the buy / draft picker the shown cards are the candidates, so
                // dim by requirements directly.
                if (handView)
                {
                    TM_PlayerBoardData board = game.GameData.GetPlayerBoardData(playerId);
                    if (board == null || board.HandCards == null || !board.HandCards.Contains(cardId))
                    {
                        return;
                    }
                }
                TM_ProjectCardData project = __instance.CardData as TM_ProjectCardData;
                if (project == null)
                {
                    return; // not a project card -> leave bright
                }
                bool unmetRequirements = !project.ValidateAllRequirements(game, playerId);
                bool unaffordable = handView && !CanAffordCard(game, playerId, project);
                if (!unmetRequirements && !unaffordable)
                {
                    return; // playable (and, in hand, affordable) -> leave bright
                }
                CanvasGroup canvasGroup =
                    Traverse.Create(__instance).Field("m_CardPreviewCanvasGroup").GetValue<CanvasGroup>();
                float dimAlpha = Traverse.Create(__instance).Field("m_AlphaValue").GetValue<float>();
                if (canvasGroup != null)
                {
                    canvasGroup.alpha = dimAlpha;
                }
            }
            catch (System.Exception)
            {
            }
        }

        // Whether the player can pay this card's cost with megacredits plus the
        // resources that actually apply to it: steel on Building cards, titanium on
        // Space cards, and heat when the player may pay with heat. Mirrors the game's
        // own cost math (ActionCostEvaluator), so a card payable with steel/titanium
        // is not wrongly dimmed. On any doubt returns true (do not dim).
        private static bool CanAffordCard(TM_Game game, int playerId, TM_ProjectCardData card)
        {
            try
            {
                TM_PlayerBoardData board = game.GameData.GetPlayerBoardData(playerId);
                if (board == null)
                {
                    return true;
                }
                int steel = (card.Tags != null && card.Tags.Contains(ETagType.Building))
                    ? board.ResourceBank[EResourceType.Steel].Quantity : 0;
                int titanium = (card.Tags != null && card.Tags.Contains(ETagType.Space))
                    ? board.ResourceBank[EResourceType.Titanium].Quantity : 0;
                int heat = board.PassiveValues != null && board.PassiveValues.CanPayWithHeat
                    ? board.ResourceBank[EResourceType.Heat].Quantity : 0;
                int remaining = ActionCostEvaluator.GetCardCost(
                    game, playerId, card.CardId, steel, titanium, heat, 0, 0, 0);
                return remaining <= board.ResourceBank[EResourceType.MegaCredit].Quantity;
            }
            catch (System.Exception)
            {
                return true;
            }
        }
    }

    // CardActionElement.SetActionButtonState shows an action as usable only when
    // IsButtonEnabled is true, which folds together "not used this generation +
    // requirements met" (CanPlayAction) AND turn gates (it's your turn, actions
    // left). So during an opponent's turn every action shows disabled and you
    // cannot tell an unused/available action from one you already played.
    //
    // When it is NOT your turn, re-show as available any action that is actually
    // playable (CanPlayAction, turn-independent); used ones stay disabled. This is
    // display-only: OnActionButtonTriggered returns immediately when the requested
    // player is not you, so the shown-active button does nothing off-turn.
    [HarmonyPatch(typeof(CardActionElement), nameof(CardActionElement.SetActionButtonState))]
    internal static class ShowActionAvailabilityPatch
    {
        private static void Postfix(CardActionElement __instance, TM_Game game, int aPlayerId)
        {
            try
            {
                if (!TfmCardRefreshPlugin.On(TfmCardRefreshPlugin.FeatActionAvailability)
                    || game == null || game.GetRequestedPlayerToPlay().PlayerID == aPlayerId)
                {
                    return; // your turn (or feature off): the game's own state is already correct
                }
                BlueCardPlayerAction action =
                    Traverse.Create(__instance).Field("m_BlueCardPlayerAction").GetValue<BlueCardPlayerAction>();
                if (action != null && action.CanPlayAction(true))
                {
                    __instance.ForceActionButtonEnabled();
                }
            }
            catch (System.Exception)
            {
            }
        }
    }

    // Sort the actions popup so usable (not-yet-used) actions float to the top and
    // spent ones sink to the bottom. Re-applied when the popup opens and refreshes.
    internal static class ActionSorter
    {
        public static void Sort(CardActionsPopup popup)
        {
            try
            {
                if (!TfmCardRefreshPlugin.On(TfmCardRefreshPlugin.FeatActionSort))
                {
                    return;
                }
                CardActionElement[] elements = popup.GetComponentsInChildren<CardActionElement>();
                if (elements == null || elements.Length < 2)
                {
                    return;
                }
                List<Transform> available = new List<Transform>();
                List<Transform> spent = new List<Transform>();
                foreach (CardActionElement element in elements)
                {
                    BlueCardPlayerAction action =
                        Traverse.Create(element).Field("m_BlueCardPlayerAction").GetValue<BlueCardPlayerAction>();
                    bool usable = action != null && action.CanPlayAction(true);
                    (usable ? available : spent).Add(element.transform);
                }
                int index = 0;
                foreach (Transform t in available)
                {
                    t.SetSiblingIndex(index++);
                }
                foreach (Transform t in spent)
                {
                    t.SetSiblingIndex(index++);
                }
            }
            catch (System.Exception)
            {
            }
        }
    }

    [HarmonyPatch(typeof(CardActionsPopup), "AfterEnterStack")]
    internal static class SortActionsOnOpenPatch
    {
        private static void Postfix(CardActionsPopup __instance)
        {
            ActionSorter.Sort(__instance);
        }
    }

    [HarmonyPatch(typeof(CardActionsPopup), "EnableActions")]
    internal static class SortActionsOnRefreshPatch
    {
        private static void Postfix(CardActionsPopup __instance)
        {
            ActionSorter.Sort(__instance);
        }
    }

    // Suppress the automatic close of the hand while it's not your turn, so it stays
    // open after you pass instead of closing and reopening. Both Pop overloads are
    // patched; returning false skips the pop.
    [HarmonyPatch(typeof(UIManager), nameof(UIManager.Pop), new[] { typeof(EPage) })]
    internal static class SuppressHandAutoClosePatch
    {
        private static bool Prefix(EPage aPage)
        {
            return !TfmCardRefreshPlugin.ShouldSuppressHandClose(aPage);
        }
    }

    [HarmonyPatch(typeof(UIManager), nameof(UIManager.Pop), new[] { typeof(EPage), typeof(bool) })]
    internal static class SuppressHandAutoClose2Patch
    {
        private static bool Prefix(EPage aPage)
        {
            return !TfmCardRefreshPlugin.ShouldSuppressHandClose(aPage);
        }
    }

    // Mark the window around a manual hand close (page close/back button) so the
    // suppression above lets it through.
    [HarmonyPatch(typeof(ViewPlayerCardsPage), nameof(ViewPlayerCardsPage.OnCloseAnimationComplete))]
    internal static class ManualHandCloseAnimPatch
    {
        private static void Prefix() { TfmCardRefreshPlugin.UserClosingHand = true; }
        private static void Postfix() { TfmCardRefreshPlugin.UserClosingHand = false; }
    }

    [HarmonyPatch(typeof(ViewPlayerCardsPage), nameof(ViewPlayerCardsPage.OnBackButtonClick))]
    internal static class ManualHandBackPatch
    {
        private static void Prefix() { TfmCardRefreshPlugin.UserClosingHand = true; TfmCardRefreshPlugin.UserClosedPanel = true; }
        private static void Postfix() { TfmCardRefreshPlugin.UserClosingHand = false; }
    }

    [HarmonyPatch(typeof(ViewPlayerCardsPage), nameof(ViewPlayerCardsPage.OnCloseButtonClick))]
    internal static class ManualHandCloseButtonPatch
    {
        private static void Prefix() { TfmCardRefreshPlugin.UserClosingHand = true; TfmCardRefreshPlugin.UserClosedPanel = true; }
        private static void Postfix() { TfmCardRefreshPlugin.UserClosingHand = false; }
    }

    // Direct sell: when SellFromHand set a pending card, inject it into the sell
    // action's event. With the event already carrying a card, the action's
    // RequireInputToCompleteAction returns false, so the Sell Patents popup is
    // never shown and exactly this one card is sold. If this postfix never fires,
    // the event stays empty and the game just opens the popup as usual (no wrong
    // card can be sold).
    // When a card preview opens its "decrease cost" panel, pre-fill steel/titanium
    // to cover the megacredit cost, but no more than needed (greedy per panel, each
    // capped at ceil(remaining cost / rate) and the panel's max). You can still nudge
    // it afterwards. Toggle with Features/AutoMaxSteelTitaniumPayment.
    [HarmonyPatch(typeof(BigCardPreview), "ConfigureDecreaseCostPanel")]
    internal static class AutoMaxPaymentPatch
    {
        private static void Postfix(BigCardPreview __instance)
        {
            if (!TfmCardRefreshPlugin.On(TfmCardRefreshPlugin.FeatAutoMaxPayment))
            {
                return;
            }
            try
            {
                CardCostDecreaseController ctrl =
                    Traverse.Create(__instance).Field("m_DecreaseCostController").GetValue<CardCostDecreaseController>();
                if (ctrl == null || !ctrl.enabled || ctrl.ResourceConversionPanels == null)
                {
                    return;
                }
                foreach (ResourceConversionPanel panel in ctrl.ResourceConversionPanels)
                {
                    if (panel == null || !panel.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    int remaining = ctrl.CurrentCost;
                    int rate = panel.ConversionRate;
                    if (remaining <= 0 || rate <= 0)
                    {
                        panel.SetResourceAmount(panel.MinResourceAmount);
                        continue;
                    }
                    int needed = Mathf.CeilToInt((float)remaining / rate);
                    panel.SetResourceAmount(Mathf.Clamp(needed, panel.MinResourceAmount, panel.MaxResourceAmount));
                }
            }
            catch (System.Exception)
            {
            }
        }
    }

    [HarmonyPatch(typeof(SellCardsPlayerAction), nameof(SellCardsPlayerAction.CreateActionEventSpecific))]
    internal static class InjectDirectSellPatch
    {
        private static void Postfix(object __result)
        {
            if (TfmCardRefreshPlugin.PendingSellCardId < 0 || __result == null)
            {
                return;
            }
            Traverse trav = Traverse.Create(__result);
            System.Collections.IList cards = trav.Field("Cards").GetValue<System.Collections.IList>()
                ?? trav.Property("Cards").GetValue<System.Collections.IList>();
            if (cards != null)
            {
                cards.Add(TfmCardRefreshPlugin.PendingSellCardId);
                TfmCardRefreshPlugin.PendingSellCardId = -1;
            }
        }
    }
}
