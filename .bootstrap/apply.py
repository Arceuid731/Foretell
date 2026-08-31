from pathlib import Path
import json, shutil, sys

src = Path(sys.argv[1])
dst = Path(sys.argv[2])

# Import BossMod Reborn while keeping Foretell's own GitHub/bootstrap files.
for item in src.iterdir():
    if item.name in {'.git', '.github'}:
        continue
    target = dst / item.name
    if target.exists():
        shutil.rmtree(target) if target.is_dir() else target.unlink()
    shutil.copytree(item, target) if item.is_dir() else shutil.copy2(item, target)

# Overlay Foretell-specific sources.
payload = dst / '.bootstrap' / 'payload'
for item in payload.rglob('*'):
    if item.is_file():
        rel = item.relative_to(payload)
        target = dst / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(item, target)

plugin_path = dst / 'BossMod/Framework/Plugin.cs'
plugin = plugin_path.read_text(encoding='utf-8-sig')
repls = [
    ('public string Name => "BossMod Reborn";', 'public string Name => "Foretell";'),
    ('private PartyRolesManager _partyRoles = null!;', 'private PartyRolesManager _partyRoles = null!;\n    private Foretell.ForetellEngine _foretell = null!;'),
    ('CommandManager.AddHandler("/bmr", new CommandInfo(OnCommand) { HelpMessage = "Show boss mod settings UI" });', 'CommandManager.AddHandler("/bmr", new CommandInfo(OnCommand) { HelpMessage = "Show Foretell/BossMod settings UI" });\n        CommandManager.AddHandler("/foretell", new CommandInfo(OnCommand) { HelpMessage = "Show Foretell settings UI" });'),
    ('_partyRoles = new(_ws);', '_partyRoles = new(_ws);\n        _foretell = new(_ws, _dalamud.ConfigDirectory.FullName);'),
    ('_partyRoles.Dispose();', '_foretell.Dispose();\n        _partyRoles.Dispose();'),
    ('CommandManager.RemoveHandler("/bmr");', 'CommandManager.RemoveHandler("/foretell");\n        CommandManager.RemoveHandler("/bmr");'),
    ('_bossmod.Update();', '_bossmod.Update();\n        _foretell.Update();'),
    ('Camera.Instance?.DrawWorldPrimitives();', '_foretell.Draw();\n        Camera.Instance?.DrawWorldPrimitives();'),
    ('new UISimpleWindow("BossModReborn", _configUI.Draw, true, new(300, 300))', 'new UISimpleWindow("Foretell", _configUI.Draw, true, new(300, 300))')
]
for old, new in repls:
    if old not in plugin:
        raise RuntimeError(f'Patch anchor missing: {old}')
    plugin = plugin.replace(old, new, 1)
plugin_path.write_text(plugin, encoding='utf-8')

# Dalamud resolves the entry DLL/manifest from the internal plugin name, so keep all three aligned.
csproj_path = dst / 'BossMod/BossModReborn.csproj'
csproj = csproj_path.read_text(encoding='utf-8-sig')
if '<AssemblyName>BossModReborn</AssemblyName>' not in csproj:
    raise RuntimeError('AssemblyName patch anchor missing')
csproj = csproj.replace('<AssemblyName>BossModReborn</AssemblyName>', '<AssemblyName>Foretell</AssemblyName>', 1)
csproj_path.write_text(csproj, encoding='utf-8')

old_manifest_path = dst / 'BossMod/BossModReborn.json'
manifest = json.loads(old_manifest_path.read_text(encoding='utf-8-sig'))
manifest.update({
    'Author': 'Arceuid731; based on FFXIV-CombatReborn/BossmodReborn',
    'Name': 'Foretell',
    'InternalName': 'Foretell',
    'RepoUrl': 'https://github.com/Arceuid731/Foretell',
    'Description': 'Adaptive encounter intelligence built on BossMod Reborn. Learns mechanics locally and renders predictive world/radar/text guidance while preserving BMR.',
    'Punchline': 'Adaptive encounter intelligence: observe, learn, predict.',
    'AcceptsFeedback': True
})
manifest.pop('IconUrl', None)
manifest.pop('ImageUrls', None)
new_manifest_path = dst / 'BossMod/Foretell.json'
new_manifest_path.write_text(json.dumps(manifest, indent=2), encoding='utf-8')
old_manifest_path.unlink()

print('Foretell patch applied')
