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
		// Power SNO: Weapon_Ranged_Instant
        private const int BotAttackPowerSno = 30796; // Purple_MagicPulse

        private readonly Player _anchor;
		private readonly int _slot;
		private readonly float _scanRadius;
		private readonly float _leashRadius;

		private Actor _target;
		private TickTimer _rethink;
		private TickTimer _attackCooldown;

		// WorldId -> (TargetGlobalId -> BotGlobalId)
		private static readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, uint>> TargetClaims = new();

		public BotBrain(Actor body, Player anchor, int slot, float scanRadius = 90f, float leashRadius = 120f)
			: base(body)
		{
			_anchor = anchor;
			_slot = Math.Max(0, slot);
			_scanRadius = Math.Max(30f, scanRadius);
			_leashRadius = Math.Max(_scanRadius + 10f, leashRadius);
		}

		public override void Think(int tickCounter)
		{
			if (Body?.World == null || _anchor?.World == null) return;
			if (Body.Dead || Body.Hidden || !Body.Visible) return;
			if (_anchor.World != Body.World) return;

			_rethink ??= new SecondsTickTimer(Body.World.Game, 0.25f);
			if (!_rethink.TimedOut) return;
			_rethink = null;

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
					CurrentAction = new MoveToPointAction(Body, idlePos);
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
					CurrentAction = new MoveToPointAction(Body, desired);
				return;
			}

			// In range: attack.
			_attackCooldown ??= new SecondsTickTimer(Body.World.Game, 0.55f);
			if (!_attackCooldown.TimedOut) return;
			_attackCooldown = null;

			Body.TranslateFacing(targetPos, false);
			CurrentAction = new PowerAction(Body, BotAttackPowerSno, _target);
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
			}
			catch
			{
				// Best-effort: never crash the world tick.
			}
		}

		private Actor AcquireTarget(World world, Vector3D anchorPos)
		{
			var monsters = world.Monsters
				.Where(m => m != null && m.Visible && !m.Dead && !m.Hidden)
				.Where(m => Distance2D(m.Position, anchorPos) <= _scanRadius)
				.OrderBy(m => Distance2D(m.Position, anchorPos))
				.ToList();

			if (monsters.Count == 0) return null;

			var claims = TargetClaims.GetOrAdd(world.GlobalID, _ => new ConcurrentDictionary<uint, uint>());

			// First pass: try a deterministic "slot" pick that isn't claimed.
			var startIndex = _slot % monsters.Count;
			for (int i = 0; i < monsters.Count; i++)
			{
				var idx = (startIndex + i) % monsters.Count;
				var m = monsters[idx];
				if (m == null) continue;
				if (claims.TryGetValue(m.GlobalID, out var claimedBy) && claimedBy != Body.GlobalID)
					continue;
				claims[m.GlobalID] = Body.GlobalID;
				return m;
			}

			// Fallback: take the closest even if it's claimed.
			var fallback = monsters[0];
			if (fallback != null)
				claims[fallback.GlobalID] = Body.GlobalID;
			return fallback;
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
