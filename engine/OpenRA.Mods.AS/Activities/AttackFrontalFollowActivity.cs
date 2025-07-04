﻿#region Copyright & License Information
/*
 * Copyright 2007-2022 The OpenRA Developers (see AUTHORS)
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
using OpenRA.Mods.AS.Traits;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Activities
{
	public class AttackFrontalFollowActivity : Activity, IActivityNotifyStanceChanged
	{
		readonly AttackFollowFrontal attack;
		readonly RevealsShroud[] revealsShroud;
		readonly IMove move;
		readonly bool forceAttack;
		readonly Color? targetLineColor;
		readonly IFacing facing;
		readonly MoveCooldownHelper moveCooldownHelper;

		Target target;
		Target lastVisibleTarget;
		bool useLastVisibleTarget;
		WDist lastVisibleMaximumRange;
		WDist lastVisibleMinimumRange;
		BitSet<TargetableType> lastVisibleTargetTypes;
		Player lastVisibleOwner;
		bool hasTicked;

		public AttackFrontalFollowActivity(Actor self, in Target target, bool allowMove, bool forceAttack, Color? targetLineColor = null)
		{
			ActivityType = ActivityType.Attack;
			attack = self.Trait<AttackFollowFrontal>();
			move = allowMove ? self.TraitOrDefault<IMove>() : null;
			revealsShroud = self.TraitsImplementing<RevealsShroud>().ToArray();
			facing = self.Trait<IFacing>();
			moveCooldownHelper = new MoveCooldownHelper(self.World, move as Mobile) { RetryIfDestinationBlocked = true };

			this.target = target;
			this.forceAttack = forceAttack;
			this.targetLineColor = targetLineColor;

			// The target may become hidden between the initial order request and the first tick (e.g. if queued)
			// Moving to any position (even if quite stale) is still better than immediately giving up
			if ((target.Type == TargetType.Actor && target.Actor.CanBeViewedByPlayer(self.Owner))
				|| target.Type == TargetType.FrozenActor || target.Type == TargetType.Terrain)
			{
				lastVisibleTarget = Target.FromPos(target.CenterPosition);
				lastVisibleMaximumRange = attack.GetMaximumRangeVersusTarget(target);
				lastVisibleMinimumRange = attack.GetMinimumRangeVersusTarget(target);

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

		public override bool Tick(Actor self)
		{
			if (IsCanceling)
				return true;

			// Check that AttackFollow hasn't cancelled the target by modifying attack.Target
			// Having both this and AttackFollow modify that field is a horrible hack.
			if (hasTicked && attack.RequestedTarget.Type == TargetType.Invalid)
				return true;

			if (attack.IsTraitPaused)
				return false;

			target = target.Recalculate(self.Owner, out var targetIsHiddenActor);
			attack.SetRequestedTarget(self, target, forceAttack);
			hasTicked = true;

			if (!targetIsHiddenActor && target.Type == TargetType.Actor)
			{
				lastVisibleTarget = Target.FromTargetPositions(target);
				lastVisibleMaximumRange = attack.GetMaximumRangeVersusTarget(target);
				lastVisibleMinimumRange = attack.GetMinimumRange();
				lastVisibleOwner = target.Actor.Owner;
				lastVisibleTargetTypes = target.Actor.GetEnabledTargetTypes();

				var leeway = attack.Info.RangeMargin.Length;
				if (leeway != 0 && move != null && target.Actor.Info.HasTraitInfo<IMoveInfo>())
				{
					var preferMinRange = Math.Min(lastVisibleMinimumRange.Length + leeway, lastVisibleMaximumRange.Length);
					var preferMaxRange = Math.Max(lastVisibleMaximumRange.Length - leeway, lastVisibleMinimumRange.Length);
					lastVisibleMaximumRange = new WDist((lastVisibleMaximumRange.Length - leeway).Clamp(preferMinRange, preferMaxRange));
				}
			}

			// The target may become hidden in the same tick the AttackActivity constructor is called,
			// causing lastVisible* to remain uninitialized.
			// Fix the fallback values based on the frozen actor properties
			else if (target.Type == TargetType.FrozenActor && !lastVisibleTarget.IsValidFor(self))
			{
				lastVisibleTarget = Target.FromTargetPositions(target);
				lastVisibleMaximumRange = attack.GetMaximumRangeVersusTarget(target);
				lastVisibleOwner = target.FrozenActor.Owner;
				lastVisibleTargetTypes = target.FrozenActor.TargetTypes;
			}

			var maxRange = lastVisibleMaximumRange;
			var minRange = lastVisibleMinimumRange;
			useLastVisibleTarget = targetIsHiddenActor || !target.IsValidFor(self);

			// Most actors want to be able to see their target before shooting
			if (target.Type == TargetType.FrozenActor && !attack.Info.TargetFrozenActors && !forceAttack)
			{
				var rs = revealsShroud
					.Where(Exts.IsTraitEnabled)
					.MaxByOrDefault(s => s.Range);

				// Default to 2 cells if there are no active traits
				var sightRange = rs != null ? rs.Range : WDist.FromCells(2);
				if (sightRange < maxRange)
					maxRange = sightRange;
			}

			// If we are ticking again after previously sequencing a MoveWithRange then that move must have completed
			// Either we are in range and can see the target, or we've lost track of it and should give up
			var result = moveCooldownHelper.Tick(targetIsHiddenActor);
			if (result != null)
				return result.Value;

			// Target is hidden or dead, and we don't have a fallback position to move towards
			if (useLastVisibleTarget && !lastVisibleTarget.IsValidFor(self))
				return true;

			var pos = self.CenterPosition;
			var checkTarget = useLastVisibleTarget ? lastVisibleTarget : target;

			// We've reached the required range - if the target is visible and valid then we wait
			// otherwise if it is hidden or dead we give up
			if (checkTarget.IsInRange(pos, maxRange) && !checkTarget.IsInRange(pos, minRange))
			{
				var extraTurning = attack.Info.MustFaceTarget ? WAngle.Zero : attack.Info.FacingTolerance;
				if (!attack.TargetInFiringArc(self, checkTarget, extraTurning))
				{
					var desiredFacing = (attack.GetTargetPosition(pos, checkTarget) - pos).Yaw;
					facing.Facing = Util.TickFacing(facing.Facing, desiredFacing, facing.TurnSpeed);
				}

				if (useLastVisibleTarget)
					return true;

				return false;
			}

			// We can't move into range, so give up
			if (move == null || maxRange == WDist.Zero || maxRange < minRange)
				return true;

			moveCooldownHelper.NotifyMoveQueued();
			QueueChild(move.MoveWithinRange(target, minRange, maxRange, checkTarget.CenterPosition));
			return false;
		}

		protected override void OnLastRun(Actor self)
		{
			// Cancel the requested target, but keep firing on it while in range
			attack.ClearRequestedTarget();
		}

		void IActivityNotifyStanceChanged.StanceChanged(Actor self, AutoTarget autoTarget, UnitStance oldStance, UnitStance newStance)
		{
			// Cancel non-forced targets when switching to a more restrictive stance if they are no longer valid for auto-targeting
			if (newStance > oldStance || forceAttack)
				return;

			// If lastVisibleTarget is invalid we could never view the target in the first place, so we just drop it here too
			if (!lastVisibleTarget.IsValidFor(self) || !autoTarget.HasValidTargetPriority(self, lastVisibleOwner, lastVisibleTargetTypes))
				attack.ClearRequestedTarget();
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			if (targetLineColor != null)
				yield return new TargetLineNode(useLastVisibleTarget ? lastVisibleTarget : target, targetLineColor.Value);
		}
	}
}
