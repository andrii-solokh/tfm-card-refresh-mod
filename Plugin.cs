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
        public const string PluginGuid = "com.experiment.tfm.cardrefresh";
        public const string PluginName = "TFM Card Playability Refresh";
        public const string PluginVersion = "1.0.0";

        private const float PollIntervalSeconds = 0.25f;

        private float _elapsed;
        private float _targetsElapsed;
        private KeyCode _repeatKey = KeyCode.None;
        private float _repeatTimer;
        private const float RepeatInitialDelay = 0.35f;
        private const float RepeatInterval = 0.06f;
        private int _lastPlayer = int.MinValue;
        private int _lastEvents = int.MinValue;
        private bool _prevIsMyTurn;
        private bool _reopenHandAfterPass;
        private bool _showOverlay;

        // ---- Config: rebindable keys (edit BepInEx/config/<guid>.cfg) ----
        internal static ConfigEntry<KeyCode> KeyProjects, KeyActions, KeyResources, KeyVictoryPoints,
            KeyEffects, KeyGreenery, KeyTemperature, KeySell, KeyBoard, KeyConfirm,
            KeyNavUp, KeyNavDown, KeyNavLeft, KeyNavRight, KeyOverlay;

        // ---- Config: feature toggles ----
        internal static ConfigEntry<bool> FeatCardRefresh, FeatHandReadable, FeatAutoOpenHand, FeatKeepHandOpen,
            FeatSuppressAnnouncements, FeatPlayabilityDim, FeatActionAvailability, FeatActionSort, FeatHotkeys,
            FeatAutoMaxPayment;

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
            KeyGreenery = Config.Bind("Keys", "ConvertGreenery", KeyCode.G, "Convert plants to a greenery");
            KeyTemperature = Config.Bind("Keys", "ConvertTemperature", KeyCode.T, "Convert heat to raise temperature");
            KeySell = Config.Bind("Keys", "Sell", KeyCode.S, "Sell cards (Sell Patents)");
            KeyBoard = Config.Bind("Keys", "BoardView", KeyCode.B, "Toggle View state / Return (inspect board)");
            KeyConfirm = Config.Bind("Keys", "Confirm", KeyCode.Space, "Confirm the default button of the open dialog/card");
            KeyNavUp = Config.Bind("Keys", "NavigateUp", KeyCode.UpArrow, "Move selection up in a choice list");
            KeyNavDown = Config.Bind("Keys", "NavigateDown", KeyCode.DownArrow, "Move selection down in a choice list");
            KeyNavLeft = Config.Bind("Keys", "NavigateLeft", KeyCode.LeftArrow, "Previous card page / left in a choice list");
            KeyNavRight = Config.Bind("Keys", "NavigateRight", KeyCode.RightArrow, "Next card page / right in a choice list");
            KeyOverlay = Config.Bind("Keys", "Overlay", KeyCode.H, "Toggle the on-screen hotkey overlay");

            FeatHotkeys = Config.Bind("Features", "Hotkeys", true, "Master switch for all keyboard shortcuts");
            FeatCardRefresh = Config.Bind("Features", "CardRefresh", true, "Re-check card playability when game state changes");
            FeatHandReadable = Config.Bind("Features", "HandReadableOffTurn", true, "Let you open your hand during opponents' turns");
            FeatAutoOpenHand = Config.Bind("Features", "AutoOpenHandAfterPlay", true, "Reopen hand/actions after you play or pass");
            FeatKeepHandOpen = Config.Bind("Features", "KeepHandOpenOffTurn", true, "Keep the hand open across passing (suppress auto-close)");
            FeatSuppressAnnouncements = Config.Bind("Features", "SuppressAnnouncements", true, "Hide turn/phase announcement banners");
            FeatPlayabilityDim = Config.Bind("Features", "DimUnplayableInHandView", true, "Dim requirement-locked cards in the off-turn hand view");
            FeatActionAvailability = Config.Bind("Features", "ShowActionAvailabilityOffTurn", true, "Show which card actions are usable during opponents' turns");
            FeatActionSort = Config.Bind("Features", "SortUsableActionsFirst", true, "Sort usable actions to the top of the actions popup");
            FeatAutoMaxPayment = Config.Bind("Features", "AutoMaxSteelTitaniumPayment", true, "When playing a card, pre-fill steel/titanium to cover the cost (not more)");
        }

        private void Awake()
        {
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded.");
            BindConfig();
            try
            {
                new Harmony(PluginGuid).PatchAll(typeof(TfmCardRefreshPlugin).Assembly);
                Logger.LogInfo("Harmony patches applied (keep hand readable during opponent turns).");
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("Harmony patch failed (hand-readable feature disabled): " + e.Message);
            }
        }

        private static GUIStyle s_numberStyle;

        private void OnGUI()
        {
            GUI.skin.label.richText = true;
            DrawNumberBadges();

            if (!_showOverlay)
            {
                return;
            }
            (ConfigEntry<KeyCode> key, KeyCode fallback, string label)[] rows =
            {
                (KeyProjects, KeyCode.P, "Projects (hand)"),
                (KeyActions, KeyCode.A, "Actions"),
                (KeyResources, KeyCode.R, "Resources"),
                (KeyVictoryPoints, KeyCode.V, "Victory points"),
                (KeyEffects, KeyCode.E, "Effects"),
                (KeyGreenery, KeyCode.G, "Plants → greenery"),
                (KeyTemperature, KeyCode.T, "Heat → temperature"),
                (KeySell, KeyCode.S, "Sell cards"),
                (KeyBoard, KeyCode.B, "View state / board"),
                (KeyConfirm, KeyCode.Space, "Confirm"),
                (KeyNavUp, KeyCode.UpArrow, "Navigate up"),
                (KeyNavDown, KeyCode.DownArrow, "Navigate down"),
            };
            float height = 52f + (rows.Length + 1) * 20f;
            GUILayout.BeginArea(new Rect(12f, 12f, 320f, height), GUI.skin.box);
            GUILayout.Label("<b>TFM mod — shortcuts</b>   (" + Key(KeyOverlay, KeyCode.H) + " to hide)");
            foreach ((ConfigEntry<KeyCode> key, KeyCode fallback, string label) in rows)
            {
                GUILayout.Label("  " + Key(key, fallback).ToString().PadRight(11) + label);
            }
            GUILayout.Label("  " + "1-9".PadRight(11) + "Use numbered card/action");
            GUILayout.Label("  " + "Cmd/Ctrl+1-4".PadRight(11) + "Focus player (1 = you)");
            GUILayout.EndArea();
        }

        // Draw a big white number at the LEFT edge of each current clickable item,
        // but only while Cmd/Ctrl is held (the key that also activates them). Uses
        // the item's world corners so it works regardless of pivot; Y is flipped for
        // IMGUI's top-left origin (screen-space-overlay canvas).
        private static readonly Vector3[] s_corners = new Vector3[4];

        private void DrawNumberBadges()
        {
            if (_targets.Count == 0 || !ModifierHeld())
            {
                return;
            }
            if (s_numberStyle == null)
            {
                s_numberStyle = new GUIStyle
                {
                    fontSize = 34,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleLeft,
                };
                s_numberStyle.normal.textColor = Color.white;
            }
            for (int i = 0; i < _targets.Count && i < 9; i++)
            {
                RectTransform rt = _targets[i].t as RectTransform;
                if (rt == null)
                {
                    continue;
                }
                rt.GetWorldCorners(s_corners); // 0=BL 1=TL 2=TR 3=BR, screen px for overlay
                float leftX = s_corners[0].x + 8f;
                float centerY = (s_corners[0].y + s_corners[1].y) * 0.5f;
                Rect rect = new Rect(leftX, (Screen.height - centerY) - 22f, 44f, 44f);
                string text = (i + 1).ToString();
                Color prev = GUI.color;
                GUI.color = Color.black;
                GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, s_numberStyle);
                GUI.color = Color.white;
                GUI.Label(rect, text, s_numberStyle);
                GUI.color = prev;
            }
        }

        private void Update()
        {
            // Only scan for numbered items while Cmd/Ctrl is held (the only time the
            // badges show and number-select is used). FindObjectsByType scans the
            // whole scene, so running it every frame lagged the game.
            if (ModifierHeld())
            {
                _targetsElapsed += Time.unscaledDeltaTime;
                if (_targetsElapsed >= 0.1f)
                {
                    _targetsElapsed = 0f;
                    RefreshNumberedTargets();
                }
            }
            else
            {
                _targetsElapsed = 0.1f; // next modifier press refreshes immediately
                if (_targets.Count > 0)
                {
                    _targets.Clear();
                }
            }
            HandleSpaceToConfirm();
            HandleHotkeys();

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
                bool isMyTurn = info.CurrentPlayerLocalId == info.GetMyPlayerLocalId();
                if (_prevIsMyTurn && !isMyTurn)
                {
                    _reopenHandAfterPass = true;
                }
                if (isMyTurn)
                {
                    _reopenHandAfterPass = false;
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
            // Overlay toggle works regardless of the master switch or text focus.
            if (Input.GetKeyDown(Key(KeyOverlay, KeyCode.H)))
            {
                _showOverlay = !_showOverlay;
                return;
            }
            if (!On(FeatHotkeys) || IsTextInputFocused())
            {
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
            if (Input.GetKeyDown(Key(KeyProjects, KeyCode.P)))
            {
                ToggleHand();
                return;
            }
            if (Input.GetKeyDown(Key(KeyGreenery, KeyCode.G)))
            {
                Convert(EResourceType.Plant);
                return;
            }
            if (Input.GetKeyDown(Key(KeyTemperature, KeyCode.T)))
            {
                Convert(EResourceType.Heat);
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
            for (int n = 0; n < 9; n++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + n) || Input.GetKeyDown(KeyCode.Keypad1 + n))
                {
                    // Bare number = focus player; Cmd/Ctrl+number = use the numbered card/action.
                    if (ModifierHeld())
                    {
                        ActivateNumberedTarget(n);
                    }
                    else
                    {
                        FocusPlayer(n);
                    }
                    return;
                }
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
                    UIManager.Instance.Pop(EPage.ViewPlayerCardsPage);
                    UserClosingHand = false;
                    return;
                }
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
                int myId = game.GameInfo.GetMyPlayerLocalId();
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
                int myId = game.GameInfo.GetMyPlayerLocalId();
                UIManager.Instance.Push(EPage.CardActionsPopup, keepPreviousPagesVisible: true, IntValuePageParameters.Create(myId));
            }
            catch (System.Exception)
            {
            }
        }

        // Reopen whichever panel you were last using: actions if your last play was a
        // card action, otherwise your projects/hand.
        internal static void ReopenPreferredPanel()
        {
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

        // Suppress the game's automatic close of the read-only hand while it is not
        // your turn, so it stays open (no close-then-reopen flicker) after you pass.
        // Your own closes (the page's close/back buttons, or the P hotkey) are still
        // allowed via UserClosingHand, and your-turn closes (needed for tile
        // placement, etc.) are never suppressed.
        internal static bool ShouldSuppressHandClose(EPage page)
        {
            if (page != EPage.ViewPlayerCardsPage || UserClosingHand || !On(FeatKeepHandOpen))
            {
                return false;
            }
            try
            {
                if (!Singleton<GameManager>.IsInstanced)
                {
                    return false;
                }
                TM_GameInfo info = Singleton<GameManager>.Instance.Game?.GameInfo;
                if (info == null)
                {
                    return false;
                }
                return info.CurrentPlayerLocalId != info.GetMyPlayerLocalId();
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
                    return;
                }
                if (resourceType == EResourceType.Plant)
                {
                    tray.OnPlantConversion();
                }
                else
                {
                    tray.OnHeatConversion();
                }
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
                int myId = game.GameInfo.GetMyPlayerLocalId();
                TM_PlayerBoardData board = game.GameData.GetPlayerBoardData(myId);
                ExpandPlayerCardsPage page = Object.FindFirstObjectByType<ExpandPlayerCardsPage>();
                int cardId = (page != null) ? Traverse.Create(page).Field("m_CurrentCardID").GetValue<int>() : -1;

                if (cardId > 0 && board != null && board.HandCards != null && board.HandCards.Contains(cardId))
                {
                    PlayerAction sellAction = board.PlayerActionBank.GetStandardProjectPlayerAction(EStandardProject.SellPatents);
                    Traverse.Create(human).Field("m_SellPatentAction").SetValue(sellAction);
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
                    return;
                }

                // No focused card: fall back to the game's sell menu.
                human.HandleSelectStandardProjectSelection(EStandardProject.SellPatents, 0);
            }
            catch (System.Exception)
            {
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
                int myId = game.GameInfo.GetMyPlayerLocalId();

                // Order: you first, then the other players by ascending local id.
                List<int> ids = new List<int>(tabs.PlayerTabs.Keys);
                ids.Sort();
                List<int> ordered = new List<int>();
                if (ids.Contains(myId))
                {
                    ordered.Add(myId);
                }
                foreach (int id in ids)
                {
                    if (id != myId)
                    {
                        ordered.Add(id);
                    }
                }

                if (index < 0 || index >= ordered.Count)
                {
                    return;
                }
                HUD_PlayerTab tab = tabs.GetHudPlayer(ordered[index]);
                if (tab != null)
                {
                    tab.OnClickIdentifierPannel();
                    RebindOpenStatPopup(ordered[index]);
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
        // the SELECT ONE choices, else the actions popup's actions. Number keys
        // activate them; OnGUI draws a number over each.
        private readonly List<(Transform t, System.Action act)> _targets = new List<(Transform, System.Action)>();

        private static bool ModifierHeld()
        {
            return Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand)
                || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        }

        private void RefreshNumberedTargets()
        {
            _targets.Clear();
            try
            {
                CardActionChoiceController choiceController = FindActiveChoiceController();
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
                            _targets.Add((tr, () => ct.Method("OnChoiceClicked", idx).GetValue()));
                        }
                    }
                    return;
                }

                // "Select any production/resource" popup: each player entry.
                StealResourcePage steal = Object.FindFirstObjectByType<StealResourcePage>();
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
                                _targets.Add((e.transform, () => e.OnClick()));
                            }
                        }
                    }
                    return;
                }

                CardActionsPopup actionsPopup = Object.FindFirstObjectByType<CardActionsPopup>();
                if (actionsPopup != null)
                {
                    foreach (CardActionElement element in actionsPopup.GetComponentsInChildren<CardActionElement>())
                    {
                        CardActionElement el = element;
                        _targets.Add((el.transform, () => el.OnActionButtonTriggered()));
                    }
                    return;
                }

                // Hand browse grid (ViewPlayerCardsPage): number opens the card (or
                // presses its Select button if it has one). Enumerate the page's own
                // card list so board/tray cards are never numbered.
                ViewPlayerCardsPage viewPage = Object.FindFirstObjectByType<ViewPlayerCardsPage>();
                if (viewPage != null)
                {
                    System.Collections.IEnumerable cardObjs =
                        Traverse.Create(viewPage).Field("m_CardObjects").GetValue() as System.Collections.IEnumerable;
                    List<CardPreview> grid = new List<CardPreview>();
                    if (cardObjs != null)
                    {
                        foreach (object o in cardObjs)
                        {
                            if (o is CardPreview cp && cp.isActiveAndEnabled)
                            {
                                grid.Add(cp);
                            }
                        }
                    }
                    SortByScreenPosition(grid);
                    foreach (CardPreview c in grid)
                    {
                        CardPreview card = c;
                        Button btn = CardButton(card);
                        _targets.Add((card.transform,
                            btn != null ? (System.Action)(() => btn.onClick.Invoke()) : (() => card.ExpandCardToView())));
                    }
                    if (_targets.Count > 0)
                    {
                        return;
                    }
                }

                // Any card with an active, interactable Select/Use/Buy button: the
                // hand carousel, buy/discard/keep selection, etc. Ordered top row
                // first, then left to right. Number presses that card's own button.
                List<BaseCardPreview> cards = new List<BaseCardPreview>();
                foreach (BaseCardPreview pv in Object.FindObjectsByType<BaseCardPreview>(FindObjectsSortMode.None))
                {
                    if (CardButton(pv) != null)
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
                    _targets.Add((card.transform, () => PressCardButton(card)));
                }
            }
            catch (System.Exception)
            {
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

        private void NavigateChoice(bool down)
        {
            // Up = more, Down = less on the amount panel, if one is open.
            if (AdjustConversion(down ? -1 : 1))
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
            // Right = more, Left = less on the amount panel, if one is open.
            if (AdjustConversion(right ? 1 : -1))
            {
                return;
            }
            try
            {
                ExpandPlayerCardsPage page = Object.FindFirstObjectByType<ExpandPlayerCardsPage>();
                if (page != null && page.ScrollSnapRect != null)
                {
                    int current = Traverse.Create(page.ScrollSnapRect).Field("_currentPage").GetValue<int>();
                    page.ScrollSnapRect.LerpToPage(current + (right ? 1 : -1));
                    return;
                }
                NavigateChoice(down: right);
            }
            catch (System.Exception)
            {
            }
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

        private void HandleSpaceToConfirm()
        {
            if (!On(FeatHotkeys) || !Input.GetKeyDown(Key(KeyConfirm, KeyCode.Space)) || IsTextInputFocused())
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
                    return;
                }

                // SELECT ONE action-choice popup: Space confirms the highlighted option.
                CardActionChoiceController choicePanel = FindActiveChoiceController();
                if (choicePanel != null)
                {
                    if (Traverse.Create(choicePanel).Field("SelectedAction").GetValue<int>() >= 0)
                    {
                        choicePanel.OnConfirm();
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
                    }
                    return;
                }

                // "Select any production / resource" target popup: Space confirms the
                // highlighted player (Confirm self-guards on a valid selection).
                StealResourcePage stealPage = Object.FindFirstObjectByType<StealResourcePage>();
                if (stealPage != null)
                {
                    stealPage.Confirm();
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
                    }
                    return;
                }

                // A confirmation dialog takes priority: Space is the default OK/Yes.
                GenericPopup popup = Object.FindFirstObjectByType<GenericPopup>();
                if (popup != null)
                {
                    PressDefaultPopupButton(popup);
                    return;
                }

                ExpandPlayerCardsPage page = Object.FindFirstObjectByType<ExpandPlayerCardsPage>();
                if (page == null)
                {
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
                    }
                    break;
                }
            }
            catch (System.Exception)
            {
                // Non-fatal: ignore the keypress if anything is unexpected.
            }
        }

        // Press the dialog's default confirm button (Yes, else Ok), whichever is
        // shown and interactable.
        private static void PressDefaultPopupButton(GenericPopup popup)
        {
            string[] fields = { "DefaultYesButton", "DefaultOkButton" };
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
                    return;
                }
            }
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

    // When you open your hand while it is not your turn (ViewOnly state),
    // CardPreview skips the playability evaluation, so every card shows at full
    // brightness even when its requirements are not met (e.g. a card needing
    // >= 4C temperature). Dim the cards in your own hand whose requirements are
    // not currently satisfied. Requirements (temperature, oxygen, oceans, tags,
    // resources on cards) are turn-independent, so this is accurate at any time.
    // Affordability is intentionally left out: cost can be paid with steel/
    // titanium, so a pure megacredit check would wrongly dim payable cards.
    [HarmonyPatch(typeof(CardPreview), "HandleState", new[] { typeof(ECardState) })]
    internal static class ShowHandPlayabilityInViewPatch
    {
        private static void Postfix(CardPreview __instance, ECardState aState)
        {
            if (aState != ECardState.ViewOnly
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
                TM_PlayerBoardData board = game.GameData.GetPlayerBoardData(playerId);
                if (board == null || board.HandCards == null || !board.HandCards.Contains(cardId))
                {
                    return; // only dim cards actually in this player's hand
                }
                TM_ProjectCardData project = __instance.CardData as TM_ProjectCardData;
                if (project == null || project.ValidateAllRequirements(game, playerId))
                {
                    return; // requirements met (or not a project card) -> leave bright
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
        private static void Prefix() { TfmCardRefreshPlugin.UserClosingHand = true; }
        private static void Postfix() { TfmCardRefreshPlugin.UserClosingHand = false; }
    }

    [HarmonyPatch(typeof(ViewPlayerCardsPage), nameof(ViewPlayerCardsPage.OnCloseButtonClick))]
    internal static class ManualHandCloseButtonPatch
    {
        private static void Prefix() { TfmCardRefreshPlugin.UserClosingHand = true; }
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
