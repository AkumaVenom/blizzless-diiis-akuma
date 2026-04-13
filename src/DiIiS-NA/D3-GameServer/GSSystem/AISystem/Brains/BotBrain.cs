using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DiIiS_NA.GameServer.Core.Types.Math;
using DiIiS_NA.GameServer.GSSystem.ActorSystem;
using DiIiS_NA.GameServer.GSSystem.ActorSystem.Actions;
using DiIiS_NA.GameServer.GSSystem.TickerSystem;
using DiIiS_NA.GameServer.GSSystem.PlayerSystem;
using DiIiS_NA.GameServer.GSSystem.MapSystem;
using DiIiS_NA.GameServer.GSSystem.ActorSystem.Movement;
using DiIiS_NA.D3_GameServer;
using DiIiS_NA.Core.Logging;
using DiIiS_NA.Core.MPQ;
using DiIiS_NA.GameServer.Core.Types.SNO;

namespace DiIiS_NA.GameServer.GSSystem.AISystem.Brains
{
	/// <summary>
	/// Combat-bot brain.
	///
	/// Key behavior differences vs <see cref="AggressiveNPCBrain"/>:
	/// - Targets are chosen from the whole world monster list (not just 40f around the bot).
	/// - Targets are chosen within a scan radius around the anchor player, so bots don't "drift" to map ends.
	/// - Bots attempt to spread by claiming different targets.
	/// - Bots always use Weapon_Melee_Instant (30592), which is implemented in this codebase.
	/// </summary>
	public sealed class BotBrain : Brain
	{
		// Default Power SNO: Weapon_Ranged_Instant (Purple_MagicPulse)
		private const int DefaultBotAttackPowerSno = 30796;

		private static readonly Logger Logger = LogManager.CreateLogger();
		private static readonly object AttackPowerInitLock = new();
		private static bool _attackPowerInitialized;
		private static int _attackPowerSno = DefaultBotAttackPowerSno;

		/// <summary>
		/// Resolves the combat-bot attack power from [Bots] CombatBotAttackPower.
		/// Supports either a numeric PowerSNO id (e.g. 30796) or a Power asset name (e.g. Purple_MagicPulse).
		/// </summary>
		private static int GetBotAttackPowerSno()
		{
			if (_attackPowerInitialized) return _attackPowerSno;

			lock (AttackPowerInitLock)
			{
				if (_attackPowerInitialized) return _attackPowerSno;

				var configured = BotsConfig.Instance.CombatBotAttackPower?.Trim();
				if (string.IsNullOrWhiteSpace(configured))
				{
					_attackPowerSno = DefaultBotAttackPowerSno;
					_attackPowerInitialized = true;
					return _attackPowerSno;
				}

				try
				{
					// 1) Numeric PowerSNO id
					if (int.TryParse(configured, out var snoId))
					{
						if (IsValidPowerSno(snoId))
						{
							_attackPowerSno = snoId;
							Logger.Info($"[Bots] CombatBotAttackPower resolved to PowerSNO {snoId}.");
						}
						else
						{
							_attackPowerSno = DefaultBotAttackPowerSno;
							Logger.Warn($"[Bots] CombatBotAttackPower={configured} is not a valid PowerSNO. Falling back to {DefaultBotAttackPowerSno}.");
						}
						_attackPowerInitialized = true;
						return _attackPowerSno;
					}

					// 2) Name lookup
					var assets = MPQStorage.Data.Assets[SNOGroup.Power].Values;
					var exact = assets.Where(a => a != null && a.Name.Equals(configured, StringComparison.OrdinalIgnoreCase)).ToList();
					var matches = exact.Count > 0
						? exact
						: assets.Where(a => a != null && a.Name.IndexOf(configured, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

					if (matches.Count == 1)
					{
						_attackPowerSno = matches[0].SNOId;
						Logger.Info($"[Bots] CombatBotAttackPower '{configured}' resolved to PowerSNO {_attackPowerSno} ({matches[0].Name}).");
					}
					else if (matches.Count > 1)
					{
						// Prefer an exact match if present; otherwise pick the lowest SNO id to be deterministic.
						var chosen = matches.OrderBy(m => m.SNOId).First();
						_attackPowerSno = chosen.SNOId;
						Logger.Warn($"[Bots] CombatBotAttackPower '{configured}' matched {matches.Count} powers; using {chosen.SNOId} ({chosen.Name}). Consider using a more specific name or a numeric SNO id.");
					}
					else
					{
						_attackPowerSno = DefaultBotAttackPowerSno;
						Logger.Warn($"[Bots] CombatBotAttackPower '{configured}' did not match any power. Falling back to {DefaultBotAttackPowerSno}.");
					}
				}
				catch (Exception ex)
				{
					_attackPowerSno = DefaultBotAttackPowerSno;
					Logger.Warn($"[Bots] Failed to resolve CombatBotAttackPower '{configured}'. Falling back to {DefaultBotAttackPowerSno}. Error: {ex.Message}");
				}

				_attackPowerInitialized = true;
				return _attackPowerSno;
			}
		}

		private static bool IsValidPowerSno(int snoId)
		{
			try
			{
				return MPQStorage.Data.Assets.TryGetValue(SNOGroup.Power, out var group) && group.ContainsKey(snoId);
			}
			catch
			{
				return false;
			}
		}
        private readonly Player _anchor;
		private readonly int _slot;
		private readonly float _scanRadius;
		private readonly float _leashRadius;

		private Actor _target;
		private TickTimer _rethink;
		private TickTimer _attackCooldown;
		private Vector3D _lastProgressPosition;
		private int _lastProgressTick = -1;

		// WorldId -> (TargetGlobalId -> BotGlobalId)
		private static readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, uint>> TargetClaims = new();

		public BotBrain(Actor body, Player anchor, int slot, float scanRadius = 90f, float leashRadius = 120f)
			: base(body)
		{
			_anchor = anchor;
			_slot = Math.Max(0, slot);
			_scanRadius = Math.Max(30f, scanRadius);
			_leashRadius = Math.Max(_scanRadius + 10f, leashRadius);
			_lastProgressPosition = body?.Position ?? new Vector3D();
		}

		public override void Think(int tickCounter)
		{
			if (Body?.World == null || _anchor?.World == null) return;
			if (Body.Dead || Body.Hidden || !Body.Visible) return;
			if (_anchor.World != Body.World) return;

			_rethink ??= new SecondsTickTimer(Body.World.Game, 0.25f);
			if (!_rethink.TimedOut) return;
			_rethink = null;

			if (!TrackProgress(tickCounter))
			{
				ResetAndTeleportNearAnchor(_anchor.Position);
				return;
			}

			// If we're too far from the anchor, snap back near them.
			var anchorPos = _anchor.Position;
			if (Distance2D(Body.Position, anchorPos) > _leashRadius)
			{
				ResetAndTeleportNearAnchor(anchorPos);
				return;
			}

			// Validate / reacquire target.
			if (!IsValidTarget(_target, Body.World, anchorPos))
			{
				ReleaseClaim(Body.World, _target);
				_target = AcquireTarget(Body.World, anchorPos);
			}

			if (_target == null)
			{
				// Idle: hold position in a small formation around the anchor.
				var idlePos = FormationPoint(anchorPos, _slot, radius: 8f);
				Body.CheckPointPosition = idlePos;
				if (Distance2D(Body.Position, idlePos) > 6f)
					CurrentAction = new MoveToPointWithPathfindAction(Body, idlePos, 2f);
				return;
			}

			// Combat: move towards an offset around the target to reduce stacking.
			var targetPos = _target.Position;
			var attackRange = GetAttackRange(Body, _target);
			var desired = OffsetAroundTarget(targetPos, anchorPos, _slot, attackRange);

			var distToTarget = Distance2D(Body.Position, targetPos);
			var canHit = distToTarget <= (attackRange + _target.ActorData.Cylinder.Ax2);

			if (!canHit)
			{
				// If we have an active action already moving us, leave it unless it's clearly wrong.
				if (CurrentAction == null || CurrentAction is PowerAction)
					CurrentAction = new MoveToPointWithPathfindAction(Body, desired, attackRange);
				return;
			}

			// In range: attack.
			_attackCooldown ??= new SecondsTickTimer(Body.World.Game, 0.55f);
			if (!_attackCooldown.TimedOut) return;
			_attackCooldown = null;

			Body.TranslateFacing(targetPos, false);
			CurrentAction = new PowerAction(Body, GetBotAttackPowerSno(), _target);
		}

		private void ResetAndTeleportNearAnchor(Vector3D anchorPos)
		{
			try
			{
				// Cancel any outstanding action before teleport.
				CurrentAction = null;
				ReleaseClaim(Body.World, _target);
				_target = null;

				var p = FormationPoint(anchorPos, _slot, radius: 10f);
				Body.CheckPointPosition = p;
				Body.Teleport(p);
				_lastProgressPosition = p;
				_lastProgressTick = -1;
			}
			catch
			{
				// Best-effort: never crash the world tick.
			}
		}


		private bool TrackProgress(int tickCounter)
		{
			if (CurrentAction == null)
			{
				_lastProgressPosition = Body.Position;
				_lastProgressTick = tickCounter;
				return true;
			}

			if (Distance2D(Body.Position, _lastProgressPosition) > 1.5f)
			{
				_lastProgressPosition = Body.Position;
				_lastProgressTick = tickCounter;
				return true;
			}

			if (_lastProgressTick < 0)
			{
				_lastProgressTick = tickCounter;
				return true;
			}

			// 120 ticks ~= 2 seconds. If a bot is still on the same spot while it has an action, recover it.
			return (tickCounter - _lastProgressTick) < 120;
		}

		private Actor AcquireTarget(World world, Vector3D anchorPos)
		{
			// NOTE: Avoid allocations (world.Monsters creates a new List every call) and LINQ in high-density fights.
			var claims = TargetClaims.GetOrAdd(world.GlobalID, _ => new ConcurrentDictionary<uint, uint>());

			var scanRSqr = _scanRadius * _scanRadius;

			Monster bestUnclaimed = null;
			float bestUnclaimedScore = float.MaxValue;

			Monster bestAny = null;
			float bestAnyDist = float.MaxValue;

			foreach (var m in world.EnumerateMonsters())
			{
				if (m == null || !m.Visible || m.Hidden || m.Dead) continue;

				var dx = m.Position.X - anchorPos.X;
				var dy = m.Position.Y - anchorPos.Y;
				var distSqr = dx * dx + dy * dy;
				if (distSqr > scanRSqr) continue;

				if (distSqr < bestAnyDist)
				{
					bestAnyDist = distSqr;
					bestAny = m;
				}

				var gid = m.GlobalID;

				// Skip targets already claimed by other bots if possible.
				if (claims.TryGetValue(gid, out var claimer) && claimer != Body.GlobalID)
					continue;

				// Add a tiny deterministic bias per slot to spread bots across nearby enemies.
				uint h = gid * 2654435761u;
				h ^= (uint)_slot * 374761393u;
				var bias = (h & 0xFFu) * 0.001f; // [0, 0.255]
				var score = distSqr + bias;

				if (score < bestUnclaimedScore)
				{
					bestUnclaimedScore = score;
					bestUnclaimed = m;
				}
			}

			var chosen = (Actor)(bestUnclaimed ?? bestAny);
			if (chosen != null)
				claims.AddOrUpdate(chosen.GlobalID, Body.GlobalID, (_, __) => Body.GlobalID);

			return chosen;
		}

		private static void ReleaseClaim(World world, Actor target)
		{
			if (world == null || target == null) return;
			if (!TargetClaims.TryGetValue(world.GlobalID, out var claims)) return;
			if (!claims.TryGetValue(target.GlobalID, out var claimedBy)) return;
			if (claimedBy == 0) return;
			claims.TryRemove(target.GlobalID, out _);
		}

		private static bool IsValidTarget(Actor target, World world, Vector3D anchorPos)
		{
			if (target == null || world == null) return false;
			if (target.World != world) return false;
			var m = target as Monster;
			if (m == null) return false;
			if (!m.Visible || m.Hidden || m.Dead) return false;
			return Distance2D(m.Position, anchorPos) <= 120f;
		}

		private static float GetAttackRange(Actor attacker, Actor target)
		{
			// Keep it simple and generous for bots; melee instant has special casing in AggressiveNPCBrain.
			var baseRange = attacker.ActorData.Cylinder.Ax2 + 10f;
			return Math.Max(8f, baseRange);
		}

		private static Vector3D FormationPoint(Vector3D anchor, int slot, float radius)
		{
			// Golden-angle spiral distribution.
			const float goldenAngle = 2.39996323f; // radians
			var a = slot * goldenAngle;
			var r = radius + (slot % 3) * 1.5f;
			return new Vector3D(
				anchor.X + (float)Math.Cos(a) * r,
				anchor.Y + (float)Math.Sin(a) * r,
				anchor.Z);
		}

		private static Vector3D OffsetAroundTarget(Vector3D target, Vector3D anchor, int slot, float radius)
		{
			// Offset bots around the target in a ring, but bias slightly towards the anchor direction.
			var dirX = target.X - anchor.X;
			var dirY = target.Y - anchor.Y;
			var dirLen = (float)Math.Sqrt(dirX * dirX + dirY * dirY);
			if (dirLen < 0.01f) dirLen = 1f;
			dirX /= dirLen;
			dirY /= dirLen;

			// Rotate base direction by slot angle.
			var angle = (slot % 8) * (float)(Math.PI / 4.0);
			var cos = (float)Math.Cos(angle);
			var sin = (float)Math.Sin(angle);
			var ox = dirX * cos - dirY * sin;
			var oy = dirX * sin + dirY * cos;

			var r = Math.Max(6f, radius);
			return new Vector3D(target.X - ox * r, target.Y - oy * r, target.Z);
		}

		private static float Distance2D(Vector3D a, Vector3D b)
		{
			var dx = a.X - b.X;
			var dy = a.Y - b.Y;
			return (float)Math.Sqrt(dx * dx + dy * dy);
		}
	}
}