namespace Amt.Core;

/// The 13 category buckets and the keyword classifier, ported from the iced build. A mod is placed
/// by the first keyword table it matches (over its id+name with non-alphanumerics stripped), falling
/// back to "Other". Later, Modrinth's real project tags will override this guess.
public static class Categories
{
    // Finer buckets than the original 13 (author call) — the real CF tags can feed this granularity;
    // the keyword fallback maps into the same set. Index order = sidebar display order.
    public static readonly string[] All =
    {
        "All",                    // 0
        "Performance",            // 1
        "Worldgen",               // 2
        "Biomes",                 // 3
        "Structures",             // 4
        "Dimensions",             // 5
        "Combat",                 // 6
        "Building & Deco",        // 7
        "Storage",                // 8
        "Magic",                  // 9
        "Tech",                   // 10
        "Redstone & Automation",  // 11
        "Mobs & Animals",         // 12
        "Food & Farming",         // 13
        "Adventure & RPG",        // 14
        "Map & Info",             // 15
        "Library",                // 16
        "Utility & QoL",          // 17
        "Server",                 // 18
        "Other",                  // 19
    };

    public const int Other = 19;

    private static readonly (int Cat, string[] Keys)[] Table =
    {
        (1, new[]{"sodium","iris","oculus","embeddium","rubidium","ferrite","modernfix","lithium","c2me","entityculling","moreculling","cull","dynamiclights","immediatelyfast","lazydfu","smoothboot","scalablelux","distanthorizon","noisium","debugify","threadtweak","krypton","leak","nvidium","framerate","fps","badoptimization","enhancedblockentities","animatica","memoryleakfix","optim","fastload"}),
        (16, new[]{"lib","library","api","kotlin","architectury","geckolib","azurelib","resourceful","clothconfig","forgeconfig","yungsapi","balm","bookshelf","collective","supermartijn","midnightlib","framework","puzzleslib","moonlight","prism","glitchcore","terrablender","nightconfig","creativecore","playeranim","fabricapi","caelus","curios","patchouli","blueprint","corelib"}),
        (6, new[]{"combat","weapon","sword","spear","dagger","shield","epicknight","gun","tacz","firearm","artifact","spartan","battle","mineandslash","apotheosis","enchant","rpg","paladin","samurai","bow"}),
        (9, new[]{"magic","spell","arcane","wizard","mana","ironspell","arsnouveau","occult","eldritch","forbidden","sorcer","witch","alchem","enigmatic","goety","malum","rune","mahou"}),
        (10, new[]{"tech","mekanism","create","thermal","machine","energy","powah","industrial","immersiveeng","appliedenergistics","ae2","refinedstorage","flux","reactor","automation","factory","integrateddynamics","pipez"}),
        (7, new[]{"macaw","decor","furniture","chair","chisel","building","builders","deco","paint","framed","supplementaries","handcrafted","storagedrawer","shelf","lamp","lantern","blockus","rustic","arch"}),
        (13, new[]{"farmer","farming","farm","food","cooking","crop","harvest","pam","croptopia","delight","brewery","kitchen","bake","culinary","vinery","honey","fish"}),
        (12, new[]{"mob","creature","animal","pet","beast","monster","zombie","fauna","alexs","naturalist","critter","dragon","horse","hamster","aquacul","untamed","goblin","frog"}),
        (4, new[]{"structure","townsandtowers","structory","dungeonsarise","whendungeon"}),
        (3, new[]{"biome","geophilic","naturecomp","williamwythers","region"}),
        (5, new[]{"dimension","nether","incendium","nullscape"}),
        (2, new[]{"terra","worldgen","tectonic","continents","overhaul","lithosphere","yungs","dripstone","cave","geode","ore"}),
        (14, new[]{"adventure","dungeon","quest","explor","loot","treasure","raid","waystone","philosopher","graveyard","bandit"}),
        (15, new[]{"jei","emi","rei","jade","wthit","minimap","xaero","map","tooltip","catalogue","hud","overlay"}),
        (17, new[]{"inventory","sorting","search","clumps","ping","controlling","configured","screenshot","zoom","clipboard","qol","convenient","utilit"}),
    };

    public static int Classify(string modId, string name)
    {
        var s = new string((modId + " " + name).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        foreach (var (cat, keys) in Table)
            if (keys.Any(s.Contains))
                return cat;
        return Other;
    }

    /// Map CurseForge category NAMES (fetched from the official list, ordered primary-first) onto a sidebar
    /// bucket; -1 = nothing mapped, keep the keyword guess. Matching by NAME keeps this immune to CF renumbering;
    /// an unrecognised name just falls through, so a new CF category degrades to the heuristic, never misfiles.
    public static int FromCf(IEnumerable<string> cfNames)
    {
        foreach (var raw in cfNames)
        {
            var bucket = raw.ToLowerInvariant() switch
            {
                "performance" => 1,
                "world gen" or "ores and resources" => 2,
                "biomes" => 3,
                "structures" => 4,
                "dimensions" => 5,
                "armor, tools, and weapons" or "combat" => 6,
                "cosmetic" => 7,                       // deco/visual mods live under Cosmetic on CF
                "storage" => 8,
                "magic" => 9,
                "technology" or "processing" or "player transport" or "energy, fluid, and item transport"
                    or "energy" or "genetics" => 10,
                "redstone" or "automation" => 11,
                "mobs" or "animals" => 12,
                "food" or "farming" => 13,
                "adventure and rpg" or "exploration" => 14,
                "map and information" => 15,
                "api and library" or "library" => 16,
                "utility & qol" or "bug fixes" or "education" => 17,
                "server utility" => 18,
                _ => -1,
            };
            if (bucket >= 0) return bucket;            // primary-first order: the first mappable tag wins
        }
        return -1;
    }
}
