﻿#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Activities;
using OpenRA.Mods.AS.Traits;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Activities
{
	class EnterGarrison : Activity
	{
		enum EnterState { Approaching, Entering, Exiting, Finished }

		readonly IMove move;
		readonly Color? targetLineColor;
		readonly Garrisoner garrisoner;
		Target target;
		Target lastVisibleTarget;
		bool useLastVisibleTarget;
		EnterState lastState = EnterState.Approaching;

		// EnterGarrison Properties
		Actor enterActor;
		Garrisonable enterGarrison;
		Aircraft enterAircraft;

		public EnterGarrison(Actor self, Target target, Color? targetLineColor = null)
		{
			// Base - Enter Properties
			move = self.Trait<IMove>();
			this.target = target;
			this.targetLineColor = targetLineColor;

			// EnterGarrison Properties
			garrisoner = self.Trait<Garrisoner>();
		}

		protected bool TryStartEnter(Actor self, Actor targetActor)
		{
			enterActor = targetActor;
			enterGarrison = targetActor.TraitOrDefault<Garrisonable>();
			enterAircraft = targetActor.TraitOrDefault<Aircraft>();

			// Make sure we can still enter the transport
			// (but not before, because this may stop the actor in the middle of nowhere)
			if (enterGarrison == null || enterGarrison.IsTraitDisabled || enterGarrison.IsTraitPaused || !garrisoner.Reserve(self, enterGarrison))
			{
				Cancel(self, true);
				return false;
			}

			if (enterAircraft != null && !enterAircraft.AtLandAltitude)
				return false;

			return true;
		}

		protected void OnEnterComplete(Actor self, Actor targetActor)
		{
			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead)
					return;

				// Make sure the target hasn't changed while entering
				// OnEnterComplete is only called if targetActor is alive
				if (targetActor != enterActor)
					return;

				if (enterActor.AppearsHostileTo(self))
					return;

				if (!enterGarrison.CanLoad(self))
					return;

				foreach (var inl in targetActor.TraitsImplementing<INotifyLoadCargo>())
					inl.Loading(self);

				enterGarrison.Load(enterActor, self);
				w.Remove(self);
			});
		}

		// Base Enter Methods Below
		public override bool Tick(Actor self)
		{
			// Update our view of the target
			target = target.Recalculate(self.Owner, out var targetIsHiddenActor);

			// Re-acquire the target after change owner has happened.
			if (target.Type == TargetType.Invalid)
				target = Target.FromActor(target.Actor);

			if (!targetIsHiddenActor && target.Type == TargetType.Actor)
				lastVisibleTarget = Target.FromTargetPositions(target);

			useLastVisibleTarget = targetIsHiddenActor || !target.IsValidFor(self);

			// Cancel immediately if the target died while we were entering it
			if (!IsCanceling && useLastVisibleTarget && lastState == EnterState.Entering)
				Cancel(self, true);

			// We need to wait for movement to finish before transitioning to
			// the next state or next activity
			if (!TickChild(self))
				return false;

			// Note that lastState refers to what we have just *finished* doing
			switch (lastState)
			{
				case EnterState.Approaching:
				{
					// NOTE: We can safely cancel in this case because we know the
					// actor has finished any in-progress move activities
					if (IsCanceling)
						return true;

					// Lost track of the target
					if (useLastVisibleTarget && lastVisibleTarget.Type == TargetType.Invalid)
						return true;

					// We are not next to the target - lets fix that
					if (target.Type != TargetType.Invalid && !move.CanEnterTargetNow(self, target))
					{
						// Target lines are managed by this trait, so we do not pass targetLineColor
						var initialTargetPosition = (useLastVisibleTarget ? lastVisibleTarget : target).CenterPosition;
						QueueChild(move.MoveToTarget(self, target, initialTargetPosition));
						return false;
					}

					// We are next to where we thought the target should be, but it isn't here
					// There's not much more we can do here
					if (useLastVisibleTarget || target.Type != TargetType.Actor)
						return true;

					// Are we ready to move into the target?
					if (TryStartEnter(self, target.Actor))
					{
						lastState = EnterState.Entering;
						QueueChild(move.MoveIntoTarget(self, target));
						return false;
					}

					// Subclasses can cancel the activity during TryStartEnter
					// Return immediately to avoid an extra tick's delay
					if (IsCanceling)
						return true;

					return false;
				}

				case EnterState.Entering:
				{
					// Re-acquire the target after change owner has happened.
					if (target.Type == TargetType.Invalid)
						target = Target.FromActor(target.Actor);

					// Check that we reached the requested position
					var targetPos = target.Positions.ClosestToWithPathFrom(self);
					if (!IsCanceling && self.CenterPosition == targetPos && target.Type == TargetType.Actor)
						OnEnterComplete(self, target.Actor);
					else
					{
						// Need to move again as we have re-aquired a target
						QueueChild(move.MoveToTarget(self, target, targetPos));
						lastState = EnterState.Approaching;
						return false;
					}

					lastState = EnterState.Exiting;
					return false;
				}

				case EnterState.Exiting:
				{
					QueueChild(move.ReturnToCell(self));
					lastState = EnterState.Finished;
					return false;
				}
			}

			return true;
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			if (targetLineColor != null)
				yield return new TargetLineNode(useLastVisibleTarget ? lastVisibleTarget : target, targetLineColor.Value);
		}
	}
}
