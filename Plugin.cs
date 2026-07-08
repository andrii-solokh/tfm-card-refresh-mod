using System.Collections.Generic;
using Aube;
using BepInEx;
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
        private int _lastPlayer = int.MinValue;
        private int _lastEvents = int.MinValue;
        private bool _prevIsMyTurn;
        private bool _reopenHandAfterPass;

        private void Awake()
        {
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded.");
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

        private void Update()
        {
            // Space presses the focused card's own action button (Buy / Use /
            // Select, whichever the game has wired for the current context).
            // Checked every frame; the throttle below only gates the poll.
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

        // Tray stat popups opened via the game's own toggle path. ShowPopup ignores
        // the toggle button's disabled state, so these work during opponent turns.
        // (Projects/hand and Board are handled separately: the hand is not a tray
        // toggle, and Board is the "View state" inspection toggle.)
        private static readonly (KeyCode key, EPage page)[] s_hotkeys =
        {
            (KeyCode.A, EPage.CardActionsPopup),      // actions
            (KeyCode.R, EPage.CardResourcesPopup),    // resources
            (KeyCode.V, EPage.CardVictoryPointsPopup),// victory points
            (KeyCode.E, EPage.CardEffectsPopup),      // effects
        };

        private void HandleHotkeys()
        {
            if (IsTextInputFocused())
            {
                return;
            }
            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                NavigateChoice(down: false);
                return;
            }
            if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                NavigateChoice(down: true);
                return;
            }
            if (Input.GetKeyDown(KeyCode.P))
            {
                ToggleHand();
                return;
            }
            if (Input.GetKeyDown(KeyCode.G))
            {
                Convert(EResourceType.Plant);
                return;
            }
            if (Input.GetKeyDown(KeyCode.T))
            {
                Convert(EResourceType.Heat);
                return;
            }
            if (Input.GetKeyDown(KeyCode.S))
            {
                SellFromHand();
                return;
            }
            if (Input.GetKeyDown(KeyCode.B))
            {
                ToggleBoardView();
                return;
            }
            for (int n = 0; n < 4; n++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + n) || Input.GetKeyDown(KeyCode.Keypad1 + n))
                {
                    FocusPlayer(n);
                    return;
                }
            }
            for (int i = 0; i < s_hotkeys.Length; i++)
            {
                if (Input.GetKeyDown(s_hotkeys[i].key))
                {
                    TryTogglePopup(s_hotkeys[i].page);
                    return;
                }
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
                    UIManager.Instance.Pop(EPage.ViewPlayerCardsPage);
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
                HUD_PlayerTray tray = (game != null && game.HUD != null) ? game.HUD.PlayerTray : null;
                if (tray != null)
                {
                    tray.ShowPopup(EPage.CardActionsPopup);
                }
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
                if (current.TryGetAgentAsHuman(out TM_HumanAgent human))
                {
                    human.HandleSelectStandardProjectSelection(EStandardProject.SellPatents, 0);
                }
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
                }
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
        private void NavigateChoice(bool down)
        {
            try
            {
                CardActionChoiceController controller = FindActiveChoiceController();
                if (controller == null)
                {
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
            if (!Input.GetKeyDown(KeyCode.Space) || IsTextInputFocused())
            {
                return;
            }
            try
            {
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
            if (!aActive && __instance.GetPageType() == EPage.ViewPlayerCardsPage)
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
                if (!Singleton<GameManager>.IsInstanced)
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
            if (aState != ECardState.ViewOnly)
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
                if (game == null || game.GetRequestedPlayerToPlay().PlayerID == aPlayerId)
                {
                    return; // your turn: the game's own state is already correct
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
}
