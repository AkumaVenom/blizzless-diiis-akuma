using System;
using System.Collections.Generic;
using DiIiS_NA.Core.Helpers.Math;
using DiIiS_NA.Core.Logging;
using DiIiS_NA.D3_GameServer;
using DiIiS_NA.D3_GameServer.Core.Types.SNO;
using DiIiS_NA.GameServer.Core.Types.Math;
using DiIiS_NA.GameServer.GSSystem.ActorSystem;
using DiIiS_NA.GameServer.GSSystem.ActorSystem.Implementations;
using DiIiS_NA.GameServer.GSSystem.MapSystem;
using DiIiS_NA.GameServer.GSSystem.TickerSystem;

namespace DiIiS_NA.GameServer.GSSystem.BotSystem
{
	/// <summary>
	/// Schedules basic monster respawns in a world.
	/// This is intentionally conservative: it only respawns standard monsters (not bosses, not gizmos, not summons).
	/// </summary>
	public sealed class MonsterRespawnManager
	{
		private static readonly Logger Logger = LogManager.CreateLogger();
		private readonly World _world;
		private readonly List<TickTimer> _timers = new();

		public MonsterRespawnManager(World world)
		{
			_world = world;
		}

		public void Update(int tickCounter)
		{
			// Tick timers (remove completed ones).
			for (int i = _timers.Count - 1; i >= 0; i--)
			{
				var t = _timers[i];
				t?.Update(tickCounter);
				if (t == null || t.TimedOut)
					_timers.RemoveAt(i);
			}
		}

		public void OnMonsterRemoved(Monster monster)
		{
			if (monster == null) return;
			if (!BotsConfig.Instance.Enabled) return;

			// Keep respawns limited to regular monsters.
			if (monster is Boss) return;
			if (monster is Minion) return; // don't respawn pets/bots
			if (!monster.Dead) return;

			var sno = monster.SNO;
			var pos = monster.Position;

			// If monster has no valid SNO or world isn't active, skip.
			if (sno == ActorSno.__NONE) return;

			var min = Math.Max(5, BotsConfig.Instance.MobRespawnMinSeconds);
			var max = Math.Max(min, BotsConfig.Instance.MobRespawnMaxSeconds);
			var delay = RandomHelper.NextFloat(min, max);

			var timer = TickTimer.WaitSeconds(_world.Game, delay, _ =>
			{
				try
				{
					if (_world == null || _world.Game == null) return;
					if (!_world.HasPlayersIn) return; // only respawn when the world is active
					if (!BotsConfig.Instance.Enabled) return;

					// Only respawn if the location still looks walkable.
					var spawnPos = pos;
					if (!_world.CheckLocationForFlag(spawnPos, DiIiS_NA.Core.MPQ.FileFormats.Scene.NavCellFlags.AllowWalk))
						spawnPos = _world.GetStartingPointById(0)?.Position ?? spawnPos;

					_world.SpawnMonster(sno, spawnPos);
				}
				catch (Exception ex)
				{
					Logger.WarnException(ex, "[Bots] Monster respawn callback exception:");
				}
			});

			_timers.Add(timer);
		}
	}
}
