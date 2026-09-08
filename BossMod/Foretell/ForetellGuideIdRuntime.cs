using System.Threading;
using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private readonly CancellationTokenSource _guideIdCancellation = new();
    private Task<GuideIdCatalog>? _guideIdCatalogTask;
    private Task<GuideIdPlan>? _guideIdPlanTask;
    private GuideDocument? _guideIdPlanDocument;
    private GuideIdPlan? _guideIdPlan;
    private string _guideIdError = "";

    private void UpdateGuideIdPlan(GuideDocument? document)
    {
        if (_isReplay || !_cfg.EnableGuides) return;
        _guideIdCatalogTask ??= Task.Run(() => GuideIdCatalog.LoadInstalled(_guideIdCancellation.Token));
        if (_guideIdCatalogTask.IsFaulted)
        {
            _guideIdError = _guideIdCatalogTask.Exception?.GetBaseException().Message ?? "Game data unavailable";
            return;
        }
        if (!_guideIdCatalogTask.IsCompletedSuccessfully || document == null) return;
        if (_guideIdPlanTask is { IsCompleted: false }) return;
        if (!ReferenceEquals(_guideIdPlanDocument, document))
        {
            _guideIdPlanDocument = document;
            _guideIdPlan = null;
            _guideIdError = "";
            var catalog = _guideIdCatalogTask.Result;
            _guideIdPlanTask = Task.Run(() => new GuideIdPlan(document, catalog), _guideIdCancellation.Token);
        }
        if (_guideIdPlanTask is { IsCompletedSuccessfully: true }) _guideIdPlan = _guideIdPlanTask.Result;
        else if (_guideIdPlanTask?.IsFaulted == true) _guideIdError = _guideIdPlanTask.Exception?.GetBaseException().Message ?? "Guide ID resolution failed";
    }

    private GuideIdPlan? GuideIDs(GuideDocument? document) => document != null && ReferenceEquals(_guideIdPlan?.Document, document) ? _guideIdPlan : null;

    private GuideIdResolutionInfo GuideIDInfo(GuideDocument? document) => GuideIDs(document)?.Info
        ?? new(_guideIdCatalogTask is { IsCompletedSuccessfully: true } ? _guideIdCatalogTask.Result.Fingerprint : "", "",
            _guideIdError.Length > 0 ? "Failed: " + _guideIdError[..Math.Min(512, _guideIdError.Length)]
                : _guideIdCatalogTask == null ? "NotStarted" : !_guideIdCatalogTask.IsCompleted ? "ReadingGameData" : document == null ? "WaitingForGuide" : "ResolvingGuideIDs", []);

    private void DrawGuideIDDiagnostics()
    {
        if (!ImGui.CollapsingHeader(GuideText("Game ID matching", "Correspondances avec le jeu", "Spiel-ID-Zuordnung", "ゲームID照合"))) return;
        var info = GuideIDInfo(PreparedGuide);
        ImGui.TextWrapped(info.State);
        if (_guideIdCatalogTask is { IsCompletedSuccessfully: true }) ImGui.TextUnformatted($"Game rows: {_guideIdCatalogTask.Result.Count:N0}");
        var triggers = info.Triggers.Where(trigger => trigger.Kind != "boss").ToArray();
        ImGui.TextUnformatted($"Triggers with candidate IDs: {triggers.Count(trigger => trigger.IDs.Length > 0)} / {triggers.Length}");
        ImGui.BeginDisabled(_guideIdCatalogTask is { IsCompleted: false } || _guideIdPlanTask is { IsCompleted: false });
        if (ImGui.Button(GuideText("Reload game data", "Relire les données du jeu", "Spieldaten neu laden", "ゲームデータ再読込")))
        {
            _guideIdCatalogTask = null; _guideIdPlanTask = null; _guideIdPlan = null; _guideIdPlanDocument = null; _guideIdError = "";
        }
        ImGui.EndDisabled();
        if (ImGui.TreeNode(GuideText("Unresolved names", "Noms non résolus", "Nicht zugeordnete Namen", "未解決の名前")))
        {
            foreach (var entry in info.Triggers.Where(entry => entry.IDs.Length == 0)) ImGui.TextWrapped($"{entry.Boss} · {entry.Name} ({entry.Kind})");
            ImGui.TreePop();
        }
    }
}
