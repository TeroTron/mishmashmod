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

using System.Collections.Generic;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Orders;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Can instantly repair other actors, but gets consumed afterwards.")]
	public class InstantlyRepairsInfo : ConditionalTraitInfo
	{
		[Desc("Uses the " + nameof(InstantlyRepairable) + " trait to determine repairability.")]
		public readonly BitSet<InstantlyRepairType> Types = default;

		[VoiceReference]
		public readonly string Voice = "Action";

		[Desc("Color to use for the target line.")]
		public readonly Color TargetLineColor = Color.Yellow;

		[Desc("Behaviour when entering the structure.",
			"Possible values are Exit, Suicide, Dispose.")]
		public readonly EnterBehaviour EnterBehaviour = EnterBehaviour.Dispose;

		[Desc("What player relationship the target's owner needs to be repaired by this actor.")]
		public readonly PlayerRelationship ValidRelationships = PlayerRelationship.Ally;

		[Desc("Sound to play when repairing is done.")]
		public readonly string RepairSound = null;

		[Desc("Do the sounds play under shroud or fog.")]
		public readonly bool AudibleThroughFog = false;

		[Desc("Volume the RepairSound played at.")]
		public readonly float SoundVolume = 1f;

		[CursorReference]
		[Desc("Cursor to display when hovering over a valid actor to repair.")]
		public readonly string Cursor = "goldwrench";

		[CursorReference]
		[Desc("Cursor to display when target actor has full health so it can't be repaired.")]
		public readonly string RepairBlockedCursor = "goldwrench-blocked";

		public override object Create(ActorInitializer init) { return new InstantlyRepairs(this); }
	}

	public class InstantlyRepairs : ConditionalTrait<InstantlyRepairsInfo>, IIssueOrder, IResolveOrder, IOrderVoice
	{
		public InstantlyRepairs(InstantlyRepairsInfo info)
			: base(info) { }

		public IEnumerable<IOrderTargeter> Orders
		{
			get
			{
				if (IsTraitDisabled)
					yield break;

				yield return new InstantRepairOrderTargeter(this);
			}
		}

		public Order IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			if (order.OrderID != "InstantRepair")
				return null;

			return new Order(order.OrderID, self, target, queued);
		}

		bool IsValidOrder(Order order)
		{
			if (order.Target.Type == TargetType.FrozenActor)
				return IsValidFrozenActor(order.Target.FrozenActor) && order.Target.FrozenActor.DamageState > DamageState.Undamaged;

			if (order.Target.Type == TargetType.Actor)
				return IsValidActor(order.Target.Actor) && order.Target.Actor.GetDamageState() > DamageState.Undamaged;

			return false;
		}

		bool IsValidActor(Actor target)
		{
			var instantlyRepairable = target.TraitOrDefault<InstantlyRepairable>();
			if (instantlyRepairable == null || instantlyRepairable.IsTraitDisabled)
				return false;

			if (!instantlyRepairable.Info.Types.IsEmpty && !instantlyRepairable.Info.Types.Overlaps(Info.Types))
				return false;

			return true;
		}

		bool IsValidFrozenActor(FrozenActor target)
		{
			// TODO: FrozenActors don't yet have a way of caching conditions, so we can't filter disabled traits
			// This therefore assumes that all InstantlyRepairable traits are enabled, which is probably wrong.
			// Actors with FrozenUnderFog should therefore not disable the InstantlyRepairable trait if
			// ValidStances includes Enemy actors.
			var instantlyRepairable = target.Info.TraitInfoOrDefault<InstantlyRepairableInfo>();
			if (instantlyRepairable == null)
				return false;

			if (!instantlyRepairable.Types.IsEmpty && !instantlyRepairable.Types.Overlaps(Info.Types))
				return false;

			return true;
		}

		public string VoicePhraseForOrder(Actor self, Order order)
		{
			return order.OrderString == "InstantRepair" && IsValidOrder(order)
				? Info.Voice : null;
		}

		public void ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != "InstantRepair" || !IsValidOrder(order))
				return;

			self.QueueActivity(order.Queued, new InstantRepair(self, order.Target, Info, Info.TargetLineColor));
			self.ShowTargetLines();
		}

		sealed class InstantRepairOrderTargeter : UnitOrderTargeter
		{
			readonly InstantlyRepairs instantRepair;

			public InstantRepairOrderTargeter(InstantlyRepairs instantRepair)
				: base("InstantRepair", 6, instantRepair.Info.Cursor, true, true)
			{
				this.instantRepair = instantRepair;
			}

			public override bool CanTargetActor(Actor self, Actor target, TargetModifiers modifiers, ref string cursor)
			{
				if (!instantRepair.IsValidActor(target))
					return false;

				if (!instantRepair.Info.ValidRelationships.HasRelationship(target.Owner.RelationshipWith(self.Owner)))
					return false;

				if (target.GetDamageState() == DamageState.Undamaged)
					cursor = instantRepair.Info.RepairBlockedCursor;

				return true;
			}

			public override bool CanTargetFrozenActor(Actor self, FrozenActor target, TargetModifiers modifiers, ref string cursor)
			{
				if (!instantRepair.IsValidFrozenActor(target))
					return false;

				if (!instantRepair.Info.ValidRelationships.HasRelationship(target.Owner.RelationshipWith(self.Owner)))
					return false;

				if (target.DamageState == DamageState.Undamaged)
					cursor = instantRepair.Info.RepairBlockedCursor;

				return true;
			}
		}
	}
}
