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

		// Track combat bots per GAME SESSION (prevents respawn/duplication across world transitions).
		private static readonly ConcurrentDictionary<int, List<uint>> GameCombatBotIds = new();
		private static readonly ConcurrentDictionary<int, bool> GameCombatBotsSpawned = new();
		private static readonly ConcurrentDictionary<int, object> GameLocks = new();

		// A small pool of "minion-like" actor SNOs that have valid monster data + basic attacks.
		// Default pool of "minion-like" actor SNOs that have valid monster data + basic attacks.
		private static readonly ActorSno[] DefaultCombatBotSnos =
		{
			ActorSno._p6_necro_skeletonmage_a,
			ActorSno._p6_necro_skeletonmage_b,
			ActorSno._p6_necro_skeletonmage_c,
			ActorSno._p6_necro_skeletonmage_d,
			ActorSno._p6_necro_skeletonmage_e,
		};

		// Configurable combat bot model list (see [Bots] CombatBotModels in config.ini).
		private static readonly object CombatBotSnoLock = new();
		private static string _cachedCombatBotModels;
		private static ActorSno[] _cachedCombatBotSnos;

		private static ActorSno[] GetCombatBotSnos()
		{
			var models = BotsConfig.Instance.CombatBotModels ?? string.Empty;
			lock (CombatBotSnoLock)
			{
				if (_cachedCombatBotSnos != null && string.Equals(_cachedCombatBotModels, models, StringComparison.Ordinal))
					return _cachedCombatBotSnos;

				_cachedCombatBotModels = models;

				var tokens = (models ?? string.Empty)
					.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
					.Select(t => t.Trim())
					.Where(t => t.Length > 0)
					.ToArray();

				var parsed = new List<ActorSno>();
				foreach (var raw in tokens)
				{
					var token = raw;
					if (token.StartsWith("ActorSno.", StringComparison.OrdinalIgnoreCase))
						token = token.Substring("ActorSno.".Length);

					// Allow users to omit the leading underscore.
					if (!token.StartsWith("_", StringComparison.Ordinal) && token.Length > 0)
						token = "_" + token;

					ActorSno sno;
					if (int.TryParse(token, out var numeric))
					{
						sno = (ActorSno)numeric;
					}
					else if (!Enum.TryParse(token, true, out sno))
					{
						Logger.Warn($"[Bots] CombatBotModels entry '{raw}' is not a valid ActorSno name. Ignoring.");
						continue;
					}

					if (sno == ActorSno.__NONE)
						continue;

					if (!parsed.Contains(sno))
						parsed.Add(sno);
				}

				if (parsed.Count == 0)
				{
					_cachedCombatBotSnos = DefaultCombatBotSnos;
				}
				else
				{
					_cachedCombatBotSnos = parsed.ToArray();
				}

				return _cachedCombatBotSnos;
			}
		}

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

			var gameId = world.Game?.GameId ?? 0;
			var lockObj = GameLocks.GetOrAdd(gameId, _ => new object());

			List<uint> ids;
			lock (lockObj)
			{
				ids = GameCombatBotIds.GetOrAdd(gameId, _ => new List<uint>());

				// Adopt any already-existing combat bots in this world (e.g., after world transitions).
				// We identify combat bots by their BotBrain.
				var discovered = world.Actors.Values
					.OfType<Minion>()
					.Where(m => m.Brain is BotBrain)
					.Select(m => m.GlobalID)
					.ToList();

				foreach (var id in discovered)
				{
					if (!ids.Contains(id))
						ids.Add(id);
				}

				// Mark the session as having spawned/bound bots once any exist.
				if (ids.Count > 0 && !GameCombatBotsSpawned.ContainsKey(gameId))
					GameCombatBotsSpawned[gameId] = true;

				var spawnedAlready = GameCombatBotsSpawned.TryGetValue(gameId, out var s) && s;

				// Spawn combat bots ONLY once per session, and ONLY in town worlds (session start location).
				if (!spawnedAlready && IsTownWorld(world))
				{
					var toSpawn = Math.Max(0, maxBots - ids.Count);
					for (int i = 0; i < toSpawn; i++)
					{
						var slot = ids.Count; // next slot
						var snoPool = GetCombatBotSnos();
						var sno = snoPool[slot % snoPool.Length];
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

					// From this point on, never spawn additional combat bots in this session (prevents duplication).
					GameCombatBotsSpawned[gameId] = true;
				}
			}

			// Maintain a per-world view for logging/debug (do not use it for spawn decisions).
			var worldIds = WorldCombatBotIds.GetOrAdd(world.GlobalID, _ => new List<uint>());
			worldIds.Clear();
			for (int i = 0; i < ids.Count; i++)
			{
				if (world.GetActorByGlobalId(ids[i]) != null)
					worldIds.Add(ids[i]);
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

			Logger.Info($"[Bots] Combat bots active: {worldIds.Count} in world {world.SNO} (#{world.GlobalID}).");
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