namespace BossMod.Foretell;

internal static class ForetellFabricPolicy
{
    internal static bool IsSheetStorageMember(Type owner, Type memberType, string member)
    {
        if (!(owner.Namespace?.StartsWith("Lumina", StringComparison.Ordinal) ?? false)) return false;
        var name = memberType.FullName ?? memberType.Name;
        return member is "ExcelPage" or "Sheet" or "Module" or "Data" or "data" or "RawData" or "RowOffset"
            || name.Contains("ExcelPage", StringComparison.Ordinal) || name.Contains("ExcelSheet", StringComparison.Ordinal)
            || name.Contains("ExcelModule", StringComparison.Ordinal) || name.Contains("GameData", StringComparison.Ordinal)
            || (owner.Name.StartsWith("RowRef", StringComparison.Ordinal) && member is not "RowId" and not "SubrowId" and not "IsValid");
    }
}
