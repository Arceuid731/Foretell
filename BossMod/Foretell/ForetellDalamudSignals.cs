using Dalamud.Game.DutyState;
using Dalamud.Game.Chat;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private void InitializeDalamudSignals()
    {
        Service.DutyState.DutyStarted += OnDutyStarted;
        _subscriptions.Add(new(() => Service.DutyState.DutyStarted -= OnDutyStarted));
        Service.DutyState.DutyWiped += OnDutyWiped;
        _subscriptions.Add(new(() => Service.DutyState.DutyWiped -= OnDutyWiped));
        Service.DutyState.DutyRecommenced += OnDutyRecommenced;
        _subscriptions.Add(new(() => Service.DutyState.DutyRecommenced -= OnDutyRecommenced));
        Service.DutyState.DutyCompleted += OnDutyCompleted;
        _subscriptions.Add(new(() => Service.DutyState.DutyCompleted -= OnDutyCompleted));
        Service.FlyTextGui.FlyTextCreated += OnFlyText;
        _subscriptions.Add(new(() => Service.FlyTextGui.FlyTextCreated -= OnFlyText));
        ClassifyNonGameplayDalamudSignals();
    }

    private void ClassifyNonGameplayDalamudSignals()
    {
        // UI notifications and plugin log messages are diagnostics, not game-state sensors. Subscribing to them
        // created feedback/flood paths while adding no encounter evidence; typed SystemLog remains available.
        RegisterCapability("dalamud.logMessage", typeof(ILogMessage), "LogMessage", false, true, "non-gameplay diagnostic stream");
        RegisterCapability("dalamud.toast.normal", typeof(ToastOptions), "Toast", false, true, "non-gameplay UI notification");
        RegisterCapability("dalamud.toast.quest", typeof(QuestToastOptions), "QuestToast", false, true, "non-gameplay UI notification");
        RegisterCapability("dalamud.toast.error", typeof(SeString), "ErrorToast", false, true, "non-gameplay UI notification");
    }

    private void OnDutyStarted(IDutyStateEventArgs args) => OnDutySignal(ObservationKind.DutyStarted, args);
    private void OnDutyWiped(IDutyStateEventArgs args) => OnDutySignal(ObservationKind.DutyWiped, args);
    private void OnDutyRecommenced(IDutyStateEventArgs args) => OnDutySignal(ObservationKind.DutyRecommenced, args);
    private void OnDutyCompleted(IDutyStateEventArgs args) => OnDutySignal(ObservationKind.DutyCompleted, args);

    private void OnDutySignal(ObservationKind kind, IDutyStateEventArgs args)
    {
        var obs = Observation(kind, primary: args.EventHandlerId, secondary: args.ContentFinderCondition.RowId,
            detail: args.TerritoryType.RowId.ToString());
        StoreNative(obs, "dalamud.duty.eventHandlerId", args.EventHandlerId);
        StoreNative(obs, "dalamud.duty.contentFinderCondition", args.ContentFinderCondition.RowId);
        StoreNative(obs, "dalamud.duty.territoryType", args.TerritoryType.RowId);
        ProcessRichObservation(obs, args);
    }

    private void OnFlyText(ref FlyTextKind kind, ref int val1, ref int val2, ref SeString text1, ref SeString text2,
        ref uint color, ref uint icon, ref uint damageTypeIcon, ref float yOffset, ref bool handled)
    {
        var obs = Observation(ObservationKind.FlyText, primary: (uint)kind, secondary: icon, value1: val1, value2: val2,
            flag: handled, detail: kind.ToString());
        StoreNative(obs, "dalamud.flyText.kind", kind);
        StoreNative(obs, "dalamud.flyText.val1", val1);
        StoreNative(obs, "dalamud.flyText.val2", val2);
        StoreNative(obs, "dalamud.flyText.color", color);
        StoreNative(obs, "dalamud.flyText.icon", icon);
        StoreNative(obs, "dalamud.flyText.damageTypeIcon", damageTypeIcon);
        StoreNative(obs, "dalamud.flyText.yOffset", yOffset);
        StoreNative(obs, "dalamud.flyText.handledAtCapture", handled);
        obs.Text["dalamud.flyText.text1"] = text1.TextValue;
        obs.Text["dalamud.flyText.text2"] = text2.TextValue;
        obs.Binary["dalamud.flyText.text1.raw"] = text1.Encode();
        obs.Binary["dalamud.flyText.text2.raw"] = text2.Encode();
        ProcessObservation(obs);
    }

    private void OnDalamudLogMessage(ILogMessage message)
    {
        // This is the structured native LogMessage stream, not player chat. The callback-owned parameter/entity
        // objects are flattened synchronously because Dalamud explicitly invalidates them after the callback.
        var obs = Observation(ObservationKind.DalamudLogMessage, primary: message.LogMessageId, flag: message.IsHandled,
            detail: "Dalamud structured LogMessage");
        StoreNative(obs, "dalamud.logMessage.id", message.LogMessageId);
        StoreNative(obs, "dalamud.logMessage.parameterCount", message.ParameterCount);
        StoreNative(obs, "dalamud.logMessage.handledAtCapture", message.IsHandled);
        ProcessRichObservation(obs, message);
    }

    private void OnNormalToast(ref SeString message, ref ToastOptions options, ref bool isHandled)
        => OnToast(ObservationKind.NormalToast, message, options, isHandled);

    private void OnQuestToast(ref SeString message, ref QuestToastOptions options, ref bool isHandled)
        => OnToast(ObservationKind.QuestToast, message, options, isHandled);

    private void OnErrorToast(ref SeString message, ref bool isHandled)
        => OnToast(ObservationKind.ErrorToast, message, null, isHandled);

    private void OnToast(ObservationKind kind, SeString message, object? options, bool isHandled)
    {
        var obs = Observation(kind, primary: StableHash(message.TextValue), flag: isHandled, detail: message.TextValue);
        obs.Text["dalamud.toast.message"] = message.TextValue;
        obs.Binary["dalamud.toast.message.raw"] = message.Encode();
        if (options != null)
            ProcessRichObservation(obs, options);
        else
            ProcessObservation(obs);
    }
}
