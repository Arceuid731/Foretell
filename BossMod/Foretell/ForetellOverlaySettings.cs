using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private void DrawOverlaySettings()
    {
        var changed = false;
        if (ImGui.CollapsingHeader(GuideText("Central alerts", "Alertes centrales", "Zentrale Warnungen", "中央警告"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.Checkbox(GuideText("Guide alerts", "Alertes du guide", "Guide-Warnungen", "攻略の警告"), ref _cfg.GuideCentralAlerts);
            changed |= ImGui.Checkbox(GuideText("Detected mechanic alerts", "Alertes des mécaniques détectées", "Erkannte Mechaniken", "検出したギミックの警告"), ref _cfg.TextHints);
            changed |= ImGui.Checkbox(GuideText("Unlock central text", "Déverrouiller le texte central", "Zentralen Text entsperren", "中央テキストのロック解除"), ref _cfg.TextHintsUnlocked);
            changed |= ImGui.SliderFloat(GuideText("Text size##central", "Taille du texte##central", "Textgröße##central", "文字サイズ##central"), ref _cfg.GuideAlertScale, .7f, 3, "%.2f");
            changed |= ImGui.SliderFloat(GuideText("Text width##central", "Largeur du texte##central", "Textbreite##central", "文字幅##central"), ref _cfg.CentralAlertWidth, 260, 1200, "%.0f px");
            changed |= ImGui.Checkbox(GuideText("Warning icons", "Icônes d’avertissement", "Warnsymbole", "警告アイコン"), ref _cfg.CentralAlertIcons);
            changed |= ImGui.SliderFloat(GuideText("Text outline", "Contour du texte", "Textkontur", "文字の縁取り"), ref _cfg.CentralOutlineThickness, 0, 4, "%.1f px");
            changed |= ImGui.SliderFloat(GuideText("Dark background", "Fond sombre", "Dunkler Hintergrund", "暗い背景"), ref _cfg.CentralBackgroundOpacity, 0, 1, "%.2f");
            changed |= EditGuideColor(GuideText("Instruction color", "Couleur de la consigne", "Anweisungsfarbe", "指示の色"), ref _cfg.CentralAlertColor);
            changed |= EditGuideColor(GuideText("Mechanic name / timer color", "Couleur du nom / décompte", "Name / Timer-Farbe", "名前・タイマーの色"), ref _cfg.CentralDetailColor);
            changed |= EditGuideColor(GuideText("Cast bar color", "Couleur de la barre d’incantation", "Zauberleistenfarbe", "詠唱バーの色"), ref _cfg.CentralBarColor);
            if (ImGui.Button(GuideText("High contrast style", "Style contrasté", "Kontrastreicher Stil", "高コントラスト")))
            { ApplyCentralAlertStyle(_cfg); changed = true; }
            ImGui.SameLine();
            if (ImGui.Button(GuideText("Reset central alerts", "Réinitialiser les alertes centrales", "Zentrale Warnungen zurücksetzen", "中央警告をリセット")))
            {
                _cfg.TextPositionX = _cfg.TextPositionY = -1;
                _cfg.GuideAlertScale = 1.4f;
                _cfg.CentralAlertWidth = 620;
                ApplyCentralAlertStyle(_cfg);
                _textWasUnlocked = false;
                changed = true;
            }
        }
        if (ImGui.CollapsingHeader(GuideText("3D overlays", "Overlays 3D", "3D-Overlays", "3D表示"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.Checkbox(GuideText("Show 3D overlays", "Afficher les overlays 3D", "3D-Overlays anzeigen", "3D表示を有効化"), ref _cfg.WorldOverlay);
            changed |= ImGui.SliderFloat(GuideText("Line thickness", "Épaisseur des contours", "Linienstärke", "線の太さ"), ref _cfg.WorldLineScale, .5f, 3, "%.2f");
            changed |= ImGui.SliderFloat(GuideText("Opacity", "Opacité", "Deckkraft", "不透明度"), ref _cfg.WorldOpacity, .1f, 1, "%.2f");
            changed |= ImGui.Checkbox(GuideText("Automatic danger colors", "Couleurs automatiques des dangers", "Automatische Gefahrenfarben", "危険範囲の自動色"), ref _cfg.WorldConfidenceColors);
            if (!_cfg.WorldConfidenceColors)
                changed |= EditGuideColor(GuideText("Outline color", "Couleur des contours", "Konturfarbe", "輪郭の色"), ref _cfg.WorldColor);
            changed |= ImGui.Checkbox(GuideText("Show mechanic names in 3D", "Afficher les noms des mécaniques en 3D", "Mechaniknamen in 3D anzeigen", "3Dにギミック名を表示"), ref _cfg.WorldLabels);
            changed |= ImGui.SliderFloat(GuideText("Text size##world", "Taille du texte##world", "Textgröße##world", "文字サイズ##world"), ref _cfg.WorldLabelScale, .5f, 3, "%.2f");
            changed |= EditGuideColor(GuideText("Text color##world", "Couleur du texte##world", "Textfarbe##world", "文字色##world"), ref _cfg.WorldLabelColor);
            if (ImGui.Button(GuideText("Reset 3D appearance", "Réinitialiser l’apparence 3D", "3D-Darstellung zurücksetzen", "3Dの外観をリセット")))
            {
                _cfg.WorldConfidenceColors = _cfg.WorldLabels = true;
                _cfg.WorldColor = 0xFF3C3CFF;
                _cfg.WorldLabelColor = 0xFF47D4FF;
                _cfg.WorldOpacity = _cfg.WorldLineScale = _cfg.WorldLabelScale = 1;
                changed = true;
            }
        }
        if (changed) _cfg.Modified.Fire();
    }
}
