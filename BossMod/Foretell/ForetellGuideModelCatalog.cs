namespace BossMod.Foretell;

internal sealed record GuideModelProfile(string ID, string Name, GuideModelAsset Asset, int MaximumContext, string ChatOptions)
{
    public string Revision => ID + ":" + Asset.Hash + ":multi-source-v3";
    public bool Reasoning => false;
}

internal static class GuideModelCatalog
{
    public const string DefaultID = "qwen3.5-4b";
    public static readonly GuideModelProfile[] Profiles =
    [
        new("granite-4.1-3b", "Granite 4.1 · 3B", new("granite-4.1-3b-Q4_K_M.gguf",
            "https://huggingface.co/ibm-granite/granite-4.1-3b-GGUF/resolve/ab4701481089b58a082ef63cc1cee738887293ff/granite-4.1-3b-Q4_K_M.gguf",
            2099501664, "662b0626cd58f443baea23559b469df6576a81d349649c59413b36a9fb32eb29"), 131072, "{\"thinking\":false}"),
        new("gemma-4-e2b", "Gemma 4 · E2B", new("gemma-4-E2B-it-Q4_K_M.gguf",
            "https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF/resolve/0314792d7f1f7e229411f620751375812bb9faf2/gemma-4-E2B-it-Q4_K_M.gguf",
            3106738272, "740185b21d22ceb83a11c3aa62ad5842ef32c70f6096d756bbee85a1e4ec34b8"), 131072, "{\"enable_thinking\":false}"),
        new("qwen3.5-4b", "Qwen 3.5 · 4B", new("Qwen3.5-4B-Q4_K_M.gguf",
            "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/e87f176479d0855a907a41277aca2f8ee7a09523/Qwen3.5-4B-Q4_K_M.gguf",
            2740937888, "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4"), 131072, "{\"enable_thinking\":false}")
    ];
    public static GuideModelProfile Get(string? id) => Profiles.FirstOrDefault(profile => profile.ID == id) ?? Profiles.First(profile => profile.ID == DefaultID);
}
