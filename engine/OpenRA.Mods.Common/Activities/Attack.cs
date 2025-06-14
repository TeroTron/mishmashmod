#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	/* non-turreted attack */
	public class Attack : Activity, IActivityNotifyStanceChanged
	{
		[Flags]
		protected enum AttackStatus { UnableToAttack, NeedsToTurn, NeedsToMove, Attacking }

		readonly IEnumerable<AttackFrontal> attackTraits;
		readonly RevealsShroud[] revealsShroud;
		readonly IMove move;
		readonly Mobile mobile;
		readonly IFacing facing;
		readonly IPositionable positionable;
		readonly bool forceAttack;
		readonly Color? targetLineColor;
		readonly MoveCooldownHelper moveCooldownHelper;

		protected Target target;
		Target lastVisibleTarget;
		WDist lastVisibleMaximumRange;
		BitSet<TargetableType> lastVisibleTargetTypes;
		Player lastVisibleOwner;
		bool useLastVisibleTarget;

		WDist minRange;
		WDist maxRange;
		AttackStatus attackStatus = AttackStatus.UnableToAttack;

		public Attack(Actor self, in Target target, bool allowMovement, bool forceAttack, Color? targetLineColor = null)
		{
			ActivityType = ActivityType.Attack;
			this.target = target;
			this.targetLineColor = targetLineColor;
			this.forceAttack = forceAttack;
			ChildHasPriority = false;

			attackTraits = self.TraitsImplementing<AttackFrontal>().ToArray().Where(t => !t.IsTraitDisabled);
			revealsShroud = self.TraitsImplementing<RevealsShroud>().ToArray();
			facing = self.Trait<IFacing>();
			positionable = self.Trait<IPositionable>();

			var iMove = self.TraitOrDefault<IMove>();
			mobile = iMove as Mobile;
			move = allowMovement ? iMove : null;

			moveCooldownHelper = new MoveCooldownHelper(self.World, mobile);

			// The target may become hidden between the initial order request and the first tick (e.g. if queued)
			// Moving to any position (even if quite stale) is still better than immediately giving up
			if ((target.Type == TargetType.Actor && target.Actor.CanBeViewedByPlayer(self.Owner))
				|| target.Type == TargetType.FrozenActor || target.Type == TargetType.Terrain)
			{
				lastVisibleTarget = Target.FromPos(target.CenterPosition);

				// Lambdas can't use 'in' variables, so capture a copy for later
				var rangeTarget = target;
				lastVisibleMaximumRange = attackTraits.Min(x => x.GetMaximumRangeVersusTarget(rangeTarget));

				if (target.Type == TargetType.Actor)
				{
					lastVisibleOwner = target.Actor.Owner;
					lastVisibleTargetTypes = target.Actor.GetEnabledTargetTypes();
				}
				else if (target.Type == TargetType.FrozenActor)
				{
					lastVisibleOwner = target.FrozenActor.Owner;
					lastVisibleTargetTypes = target.FrozenActor.TargetTypes;
				}
			}
		}

		protected virtual Target RecalculateTarget(Actor self, out bool targetIsHiddenActor)
		{
			return target.Recalculate(self.Owner, out targetIsHiddenActor);
		}

		public override bool Tick(Actor self)
		{
			if (!IsCanceling && !HasArmamentsFor(target))
				Cancel(self, true);

			if (!TickChild(self))
				return false;

			if (IsCanceling)
				return true;

			target = RecalculateTarget(self, out var targetIsHiddenActor);

			if (!targetIsHiddenActor && target.Type == TargetType.Actor)
			{
				lastVisibleTarget = Target.FromTargetPositions(target);
				lastVisibleMaximumRange = attackTraits.Min(x => x.GetMaximumRangeVersusTarget(target));

				lastVisibleOwner = target.Actor.Owner;
				lastVisibleTargetTypes = target.Actor.GetEnabledTargetTypes();
			}

			useLastVisibleTarget = targetIsHiddenActor || !target.IsValidFor(self);

			var result = moveCooldownHelper.Tick(targetIsHiddenActor);
			if (result != null)
				return result.Value;

			// Target is hidden or dead, and we don't have a fallback position to move towards
			if (useLastVisibleTarget && !lastVisibleTarget.IsValidFor(self))
				return true;

			var pos = self.CenterPosition;
			var checkTarget = useLastVisibleTarget ? lastVisibleTarget : target;

			// We don't know where the target actually is, so move to where we last saw it
			if (useLastVisibleTarget)
			{
				// We've reached the assumed position but it is not there or we can't move any further - give up
				if (checkTarget.IsInRange(pos, lastVisibleMaximumRange) || move == null || lastVisibleMaximumRange == WDist.Zero)
					return true;

				// Move towards the last known position
				moveCooldownHelper.NotifyMoveQueued();
				QueueChild(move.MoveWithinRange(target, WDist.Zero, lastVisibleMaximumRange, checkTarget.CenterPosition, Color.Red));
				return false;
			}

			attackStatus = AttackStatus.UnableToAttack;

			foreach (var attack in attackTraits)
			{
				var status = TickAttack(self, attack);
				attack.IsAiming = status == AttackStatus.Attacking || status == AttackStatus.NeedsToTurn;
			}

			if (attackStatus >= AttackStatus.NeedsToTurn)
				return false;

			return true;
		}

		protected override void OnLastRun(Actor self)
		{
			foreach (var attack in attackTraits)
				attack.IsAiming = false;
		}

		protected virtual AttackStatus TickAttack(Actor self, AttackFrontal attack)
		{
			if (!target.IsValidFor(self))
				return AttackStatus.UnableToAttack;

			if (attack.Info.AttackRequiresEnteringCell && !positionable.CanEnterCell(target.Actor.Location, null, BlockedByActor.None))
				return AttackStatus.UnableToAttack;

			if (!attack.Info.TargetFrozenActors && !forceAttack && target.Type == TargetType.FrozenActor)
			{
				// Try to move within range, drop the target otherwise
				if (move == null)
					return AttackStatus.UnableToAttack;

				var rs = revealsShroud
					.Where(t => !t.IsTraitDisabled)
					.MaxByOrDefault(s => s.Range);

				// Default to 2 cells if there are no active traits
				var sightRange = rs != null ? rs.Range : WDist.FromCells(2);

				attackStatus |= AttackStatus.NeedsToMove;
				moveCooldownHelper.NotifyMoveQueued();
				QueueChild(move.MoveWithinRange(target, sightRange, target.CenterPosition, Color.Red));
				return AttackStatus.NeedsToMove;
			}

			// Drop the target once none of the weapons are effective against it
			var armaments = attack.ChooseArmamentsForTarget(target, forceAttack).ToList();
			if (armaments.Count == 0)
				return AttackStatus.UnableToAttack;

			// Update ranges. Exclude paused armaments except when ALL weapons are paused
			// (e.g. out of ammo), in which case use the paused, valid weapon with highest range.
			var activeArmaments = armaments.Where(x => !x.IsTraitPaused).ToList();
			if (activeArmaments.Count != 0)
			{
				minRange = activeArmaments.Max(a => a.Weapon.MinRange);
				maxRange = activeArmaments.Min(a => a.MaxRange());
			}
			else
			{
				minRange = WDist.Zero;
				maxRange = armaments.Max(a => a.MaxRange());
			}

			var pos = self.CenterPosition;
			if (!target.IsInRange(pos, maxRange)
				|| (minRange.Length != 0 && target.IsInRange(pos, minRange))
				|| (mobile != null && !mobile.CanInteractWithGroundLayer(self)))
			{
				// Try to move within range, drop the target otherwise
				if (move == null)
					return AttackStatus.UnableToAttack;

				attackStatus |= AttackStatus.NeedsToMove;
				moveCooldownHelper.NotifyMoveQueued();
				var checkTarget = useLastVisibleTarget ? lastVisibleTarget : target;
				QueueChild(move.MoveWithinRange(target, minRange, maxRange, checkTarget.CenterPosition, Color.Red));
				return AttackStatus.NeedsToMove;
			}

			if (!attack.TargetInFiringArc(self, target, attack.Info.FacingTolerance))
			{
				// Mirror Turn activity checks.
				if (mobile == null || (!mobile.IsTraitDisabled && !mobile.IsTraitPaused) || mobile.Info.CanTurnWhileDisabled)
				{
					// Don't queue a Turn activity: Executing a child takes an additional tick during which the target may have moved again.
					facing.Facing = Util.TickFacing(facing.Facing, (attack.GetTargetPosition(pos, target) - pos).Yaw, facing.TurnSpeed);

					// Check again if we turned enough and directly continue attacking if we did.
					if (!attack.TargetInFiringArc(self, target, attack.Info.FacingTolerance))
					{
						attackStatus |= AttackStatus.NeedsToTurn;
						return AttackStatus.NeedsToTurn;
					}
				}
				else
				{
					attackStatus |= AttackStatus.NeedsToTurn;
					return AttackStatus.NeedsToTurn;
				}
			}

			attackStatus |= AttackStatus.Attacking;
			DoAttack(self, attack, armaments);

			return AttackStatus.Attacking;
		}

		protected virtual void DoAttack(Actor self, AttackFrontal attack, IEnumerable<Armament> armaments)
		{
			if (!attack.IsTraitPaused)
				foreach (var a in armaments)
					a.CheckFire(self, facing, target);
		}

		void IActivityNotifyStanceChanged.StanceChanged(Actor self, AutoTarget autoTarget, UnitStance oldStance, UnitStance newStance)
		{
			// Cancel non-forced targets when switching to a more restrictive stance if they are no longer valid for auto-targeting
			if (newStance > oldStance || forceAttack)
				return;

			// If lastVisibleTarget is invalid we could never view the target in the first place, so we just drop it here too
			if (!lastVisibleTarget.IsValidFor(self) || !autoTarget.HasValidTargetPriority(self, lastVisibleOwner, lastVisibleTargetTypes))
				target = Target.Invalid;
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			if (targetLineColor != null)
				yield return new TargetLineNode(useLastVisibleTarget ? lastVisibleTarget : target, targetLineColor.Value);
		}

		bool HasArmamentsFor(Target target)
		{
			return attackTraits.Any(attack => attack.ChooseArmamentsForTarget(target, forceAttack).Any());
		}
	}
}
