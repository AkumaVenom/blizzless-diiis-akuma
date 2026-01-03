using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DiIiS_NA.Core.Helpers.Math;
using DiIiS_NA.Core.Logging;
using DiIiS_NA.D3_GameServer;
using DiIiS_NA.D3_GameServer.Core.Types.SNO;
using DiIiS_NA.GameServer.Core.Types.Math;
using DiIiS_NA.GameServer.Core.Types.TagMap;
using DiIiS_NA.GameServer.GSSystem.ActorSystem;
using DiIiS_NA.GameServer.GSSystem.AISystem.Brains;
using DiIiS_NA.GameServer.GSSystem.MapSystem;
using DiIiS_NA.GameServer.GSSystem.PlayerSystem;
using DiIiS_NA.GameServer.MessageSystem;

namespace DiIiS_NA.GameServer.GSSystem.BotSystem
{
	/// <summary>
	/// Spawns simple server-side "player" bots (AI-driven minions) and "fun" interactive bots in towns.
	///
	/// Design goals (deliberately simple):
	/// - No fake network clients.
	/// - Uses existing Actor AI and combat systems.
	/// - Completely gated by BotsConfig.Enabled.
	/// </summary>
	public static class BotManager
	{
		private static readonly Logger Logger = LogManager.CreateLogger();
		// Track combat bots per world so we can re-anchor/reposition them every time a player enters.
		private static readonly ConcurrentDictionary<uint, List<uint>> WorldCombatBotIds = new();
		private static readonly ConcurrentDictionary<uint, bool> WorldTownBotsSpawned = new();

		// A small pool of "minion-like" actor SNOs that have valid monster data + basic attacks.
		private static readonly ActorSno[] CombatBotSnos =
		{
			ActorSno._p6_necro_skeletonmage_a,
			ActorSno._p6_necro_skeletonmage_b,
			ActorSno._p6_necro_skeletonmage_c,
			ActorSno._p6_necro_skeletonmage_d,
			ActorSno._p6_necro_skeletonmage_e,
		};

		/// <summary>
		/// Ensures bots exist for the given world (idempotent).
		/// Called when a real player enters the world.
		/// </summary>
		public static void EnsureWorldBots(World world, Player anchorPlayer)
		{
			if (world == null || anchorPlayer == null) return;
			if (!BotsConfig.Instance.Enabled) return;

			try
			{
				// Allow combat mage bots to spawn in towns too (including the initial session world).
				if (IsTownWorld(world))
					EnsureTownBots(world);

				EnsureCombatBots(world, anchorPlayer);
			}
			catch (Exception ex)
			{
				Logger.WarnException(ex, "BotManager.EnsureWorldBots exception:");
			}
		}

		private static bool IsTownWorld(World world)
		{
			// Simple heuristic: most town worlds contain "town" in the enum name (e.g., trout_town).
			var name = world.SNO.ToString();
			return name.Contains("town", StringComparison.OrdinalIgnoreCase) ||
			       name.Contains("hub", StringComparison.OrdinalIgnoreCase);
		}

		private static void EnsureCombatBots(World world, Player anchorPlayer)
		{
			var maxBots = Math.Max(0, Math.Min(50, BotsConfig.Instance.MaxBotsPerWorld));
			if (maxBots == 0) return;

			var origin = anchorPlayer.Position;
			var ids = WorldCombatBotIds.GetOrAdd(world.GlobalID, _ => new List<uint>());

			// Clean up stale references.
			ids.RemoveAll(id => world.GetActorByGlobalId(id) == null);

			// Spawn any missing bots.
			var toSpawn = Math.Max(0, maxBots - ids.Count);
			for (int i = 0; i < toSpawn; i++)
			{
				var slot = ids.Count; // next slot
				var sno = CombatBotSnos[slot % CombatBotSnos.Length];
				var pos = FormationPoint(origin, slot, 10f);
				if (!world.CheckLocationForFlag(pos, DiIiS_NA.Core.MPQ.FileFormats.Scene.NavCellFlags.AllowWalk))
					pos = origin;

				var bot = new Minion(world, sno, anchorPlayer, new TagMap());
				bot.Attributes[GameAttributes.Is_Helper] = false;
				bot.Attributes[GameAttributes.TeamID] = anchorPlayer.Attributes[GameAttributes.TeamID];
				bot.Attributes[GameAttributes.Team_Override] = 1;
				bot.Brain = new BotBrain(bot, anchorPlayer, slot);
				bot.EnterWorld(pos);
				ids.Add(bot.GlobalID);
			}

			// Every time a player enters the world, re-anchor + reposition all bots near that player.
			for (int i = 0; i < ids.Count; i++)
			{
				var actor = world.GetActorByGlobalId(ids[i]);
				var bot = actor as Minion;
				if (bot == null) continue;
				bot.Master = anchorPlayer;
				bot.Attributes[GameAttributes.TeamID] = anchorPlayer.Attributes[GameAttributes.TeamID];
				bot.Attributes[GameAttributes.Team_Override] = 1;

				// Ensure bot damage stays in sync with the player.
				bot.Attributes[GameAttributes.Damage_Weapon_Min, 0] = anchorPlayer.Attributes[GameAttributes.Damage_Weapon_Min_Total, 0];
				bot.Attributes[GameAttributes.Damage_Weapon_Delta, 0] = anchorPlayer.Attributes[GameAttributes.Damage_Weapon_Delta_Total, 0];

				// Reset any stuck actions and teleport into formation.
				bot.Brain?.DeActivate();
				bot.Brain = new BotBrain(bot, anchorPlayer, i);
				bot.Brain?.Activate();
				var p = FormationPoint(origin, i, 10f);
				if (!world.CheckLocationForFlag(p, DiIiS_NA.Core.MPQ.FileFormats.Scene.NavCellFlags.AllowWalk))
					p = origin;
				bot.CheckPointPosition = p;
				bot.Teleport(p);
			}

			Logger.Info($"[Bots] Combat bots active: {ids.Count} in world {world.SNO} (#{world.GlobalID}).");
		}

		private static Vector3D FormationPoint(Vector3D anchor, int slot, float radius)
		{
			const float goldenAngle = 2.39996323f;
			var a = slot * goldenAngle;
			var r = radius + (slot % 3) * 1.5f;
			return new Vector3D(
				anchor.X + (float)Math.Cos(a) * r,
				anchor.Y + (float)Math.Sin(a) * r,
				anchor.Z);
		}

		private static void EnsureTownBots(World world)
		{
			if (WorldTownBotsSpawned.ContainsKey(world.GlobalID)) return;
			WorldTownBotsSpawned[world.GlobalID] = true;

			var count = Math.Max(0, Math.Min(20, BotsConfig.Instance.TownBotsPerTown));
			if (count == 0) return;

			// Use the existing Challenge Rift Nephalem interactive NPC as a "fun bot".
			// It already has conversation interactions wired.
			var origin = world.GetStartingPointById(172)?.Position ??
			            world.GetStartingPointById(0)?.Position ??
			            new Vector3D();

			for (int i = 0; i < count; i++)
			{
				var pos = RandomNear(origin, 4f, 12f);
				var npc = ActorFactory.Create(world, ActorSno._p6_challengerift_nephalem, new TagMap());
				if (npc == null) continue;
				npc.EnterWorld(pos);
			}

			Logger.Info($"[Bots] Spawned {count} town bots in world {world.SNO} (#{world.GlobalID}).");
		}

		private static Vector3D RandomNear(Vector3D origin, float minRadius, float maxRadius)
		{
			var angle = (float)(FastRandom.Instance.NextDouble() * Math.PI * 2);
			var radius = minRadius + (float)FastRandom.Instance.NextDouble() * (maxRadius - minRadius);
			return new Vector3D(
				origin.X + (float)Math.Cos(angle) * radius,
				origin.Y + (float)Math.Sin(angle) * radius,
				origin.Z);
		}
	}
}
