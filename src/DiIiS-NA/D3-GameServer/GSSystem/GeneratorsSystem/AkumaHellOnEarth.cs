using System;
using System.Collections.Generic;
using System.Linq;
using DiIiS_NA.Core.Helpers.Math;
using DiIiS_NA.Core.Logging;
using DiIiS_NA.Core.MPQ;
using DiIiS_NA.Core.MPQ.FileFormats;
using DiIiS_NA.D3_GameServer;
using DiIiS_NA.D3_GameServer.Core.Types.SNO;
using DiIiS_NA.GameServer.Core.Types.SNO;
using DiIiS_NA.GameServer.Core.Types.TagMap;
using DiIiS_NA.GameServer.GSSystem.ActorSystem;
using DiIiS_NA.GameServer.GSSystem.GameSystem;
using DiIiS_NA.GameServer.GSSystem.MapSystem;

// Avoid type-name collisions (World, Monster) between MPQ formats and runtime server types.
using GameWorld = DiIiS_NA.GameServer.GSSystem.MapSystem.World;
using MPQMonster = DiIiS_NA.Core.MPQ.FileFormats.Monster;

namespace DiIiS_NA.GameServer.GSSystem.GeneratorsSystem
{
    /// <summary>
    /// "Akuma's Hell on Earth" feature.
    /// When enabled, replaces regular monster spawns with random demon-family monsters.
    /// Quest actors and bosses are excluded.
    /// </summary>
    public static class AkumaHellOnEarth
    {
        private static readonly Logger Logger = LogManager.CreateLogger("HellOnEarth");

        private static readonly string[] DemonNameHints =
        {
            // broad but safe-ish name hints for demon families
            "demon", "hell", "fallen", "imp", "succub", "incub", "goat", "khazra", "terror", "fiend",
            "butcher", "diablo", "azmodan", "belial", "rakanoth", "ghom", "maghda", "skeletonking"
        };

        private static readonly string[] ExcludedNameHints =
        {
            // protect bosses / quest actors / special cases
            "unique", "boss", "uber", "quest", "event", "convo", "conversation", "npc", "hireling",
            "follower", "pet", "summon", "summoned", "companion", "town", "vendor", "training"
        };

        private static IReadOnlyList<ActorSno>? _cachedDemons;
        private static readonly object CacheLock = new();

        private static bool IsActSupported(GameWorld world)
        {
            var act = world.Game.CurrentActEnum;
            return act is ActEnum.Act1 or ActEnum.Act2 or ActEnum.Act3 or ActEnum.Act4 or ActEnum.Act5 or ActEnum.OpenWorld;
        }

        private static bool HasQuestOrBossTags(TagMap tags)
        {
            // Anything with quest ranges, conversations, boss encounter data, etc.
            return tags.ContainsKey(MarkerKeys.QuestRange) ||
                   tags.ContainsKey(MarkerKeys.QuestRange2) ||
                   tags.ContainsKey(MarkerKeys.ConversationList) ||
                   tags.ContainsKey(MarkerKeys.BossEncounter) ||
                   tags.ContainsKey(MarkerKeys.BossEncounterSnoLevelArea) ||
                   tags.ContainsKey(MarkerKeys.TriggeredConversation) ||
                   tags.ContainsKey(MarkerKeys.TriggeredConversation1) ||
                   tags.ContainsKey(MarkerKeys.OnActorSpawnedScript);
        }

        private static bool IsProtectedByName(string actorNameLower)
        {
            if (string.IsNullOrWhiteSpace(actorNameLower))
                return true;

            for (int i = 0; i < ExcludedNameHints.Length; i++)
                if (actorNameLower.Contains(ExcludedNameHints[i]))
                    return true;

            return false;
        }

        private static bool LooksLikeDemonByName(string actorNameLower)
        {
            for (int i = 0; i < DemonNameHints.Length; i++)
                if (actorNameLower.Contains(DemonNameHints[i]))
                    return true;

            return false;
        }

        private static IReadOnlyList<ActorSno> GetOrBuildDemonPool()
        {
            if (_cachedDemons != null)
                return _cachedDemons;

            lock (CacheLock)
            {
                if (_cachedDemons != null)
                    return _cachedDemons;

                var demons = new List<ActorSno>(1024);

                foreach (var kvp in MPQStorage.Data.Assets[SNOGroup.Actor])
                {
                    var snoId = kvp.Key;
                    var asset = kvp.Value;
                    var actorData = asset.Data as ActorData;
                    if (actorData == null)
                        continue;

                    if (actorData.Type != ActorType.Monster)
                        continue;

                    // Must map to a monster definition
                    if (!MPQStorage.Data.Assets[SNOGroup.Monster].ContainsKey(actorData.MonsterSNO))
                        continue;

                    var monsterAsset = MPQStorage.Data.Assets[SNOGroup.Monster][actorData.MonsterSNO];
                    var monsterData = monsterAsset.Data as MPQMonster;
                    if (monsterData == null)
                        continue;

                    // Exclude non-hostile / utility monster types
                    if (monsterData.Type is MPQMonster.MonsterType.Breakable or MPQMonster.MonsterType.Ally or MPQMonster.MonsterType.Helper or MPQMonster.MonsterType.Scenery)
                        continue;

                    var nameLower = (asset.Name ?? string.Empty).ToLowerInvariant();

                    // Exclude obvious bosses/uniques/quest objects
                    if (IsProtectedByName(nameLower))
                        continue;

                    // Exclude treasure goblins etc.
                    if (nameLower.Contains("goblin"))
                        continue;

                    if (!LooksLikeDemonByName(nameLower))
                        continue;

                    demons.Add((ActorSno)snoId);
                }

                // Fallback: if the heuristic was too strict, keep it safe and do nothing.
                if (demons.Count < 10)
                {
                    Logger.Warn("HellOnEarth demon pool build produced {0} entries. Feature will effectively be inert.", demons.Count);
                }
                else
                {
                    Logger.Info("HellOnEarth demon pool ready: {0} demon actors.", demons.Count);
                }

                _cachedDemons = demons;
                return _cachedDemons;
            }
        }

        /// <summary>
        /// Returns a replacement demon ActorSno when the feature is enabled and the original spawn is a regular monster.
        /// </summary>
        public static bool TryGetReplacement(GameWorld world, ActorSno originalSno, TagMap tags, out ActorSno replacementSno)
        {
            replacementSno = originalSno;

            if (!AkumaFeaturesConfig.Instance.HellOnEarthEnabled)
                return false;

            if (!IsActSupported(world))
                return false;

            if (originalSno == ActorSno.__NONE)
                return false;

            if (HasQuestOrBossTags(tags))
                return false;

            if (!MPQStorage.Data.Assets[SNOGroup.Actor].ContainsKey((int)originalSno))
                return false;

            var asset = MPQStorage.Data.Assets[SNOGroup.Actor][(int)originalSno];
            var actorData = asset.Data as ActorData;
            if (actorData == null)
                return false;

            // Only replace real monsters (not gizmos/env/items)
            if (actorData.Type != ActorType.Monster)
                return false;

            var nameLower = (asset.Name ?? string.Empty).ToLowerInvariant();

            // Do not touch uniques/bosses/quest actors
            if (IsProtectedByName(nameLower))
                return false;

            // Also avoid replacing NPC-like monsters / scenery/breakables
            if (!MPQStorage.Data.Assets[SNOGroup.Monster].ContainsKey(actorData.MonsterSNO))
                return false;

            var monsterAsset = MPQStorage.Data.Assets[SNOGroup.Monster][actorData.MonsterSNO];
            var monsterData = monsterAsset.Data as MPQMonster;
            if (monsterData == null)
                return false;

            if (monsterData.Type is MPQMonster.MonsterType.Breakable or MPQMonster.MonsterType.Ally or MPQMonster.MonsterType.Helper or MPQMonster.MonsterType.Scenery)
                return false;

            // Pick a random demon.
            var pool = GetOrBuildDemonPool();
            if (pool.Count == 0)
                return false;

            replacementSno = pool[RandomHelper.Next(0, pool.Count)];
            return replacementSno != originalSno;
        }
    }
}
