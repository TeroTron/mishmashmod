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
using OpenRA.Mods.AS.Activities;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("This actor can enter SharedCargo actors.")]
	public class SharedPassengerInfo : TraitInfo
	{
		public readonly string CargoType = null;

		[Desc("If defined, use a custom pip type defined on the transport's WithCargoPipsDecoration.CustomPipSequences list.")]
		public readonly string CustomPipType = null;

		public readonly int Weight = 1;

		[GrantedConditionReference]
		[Desc("The condition to grant to when this actor is loaded inside any transport.")]
		public readonly string CargoCondition = null;

		[Desc("Conditions to grant when this actor is loaded inside specified transport.",
			"A dictionary of [actor id]: [condition].")]
		public readonly Dictionary<string, string> CargoConditions = new();

		[GrantedConditionReference]
		public IEnumerable<string> LinterCargoConditions { get { return CargoConditions.Values; } }

		[VoiceReference]
		public readonly string Voice = "Action";

		[Desc("Color to use for the target line.")]
		public readonly Color TargetLineColor = Color.Green;

		[ConsumedConditionReference]
		[Desc("Boolean expression defining the condition under which the regular (non-force) enter cursor is disabled.")]
		public readonly BooleanExpression RequireForceMoveCondition = null;

		[Desc("Cursor to display when able to enter target actor.")]
		public readonly string EnterCursor = "enter";

		[Desc("Cursor to display when unable to enter target actor.")]
		public readonly string EnterBlockedCursor = "enter-blocked";

		public override object Create(ActorInitializer init) { return new SharedPassenger(this, init.Self); }
	}

	public class SharedPassenger : IIssueOrder, IResolveOrder, IOrderVoice,
		INotifyRemovedFromWorld, INotifyEnteredSharedCargo, INotifyExitedSharedCargo, INotifyKilled, IObservesVariables
	{
		public readonly SharedPassengerInfo Info;
		public Actor Transport;
		bool requireForceMove;

		int anyCargoToken = Actor.InvalidConditionToken;
		int specificCargoToken = Actor.InvalidConditionToken;

		public SharedPassenger(SharedPassengerInfo info, Actor self)
		{
			Info = info;
		}

		public SharedCargo ReservedCargo { get; private set; }

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get
			{
				yield return new EnterAlliedActorTargeter<SharedCargoInfo>(
					"EnterSharedTransport",
					5,
					Info.EnterCursor,
					Info.EnterBlockedCursor,
					IsCorrectCargoType,
					CanEnter);
			}
		}

		public Order IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			if (order.OrderID == "EnterSharedTransport")
				return new Order(order.OrderID, self, target, queued);

			return null;
		}

		bool IsCorrectCargoType(Actor target, TargetModifiers modifiers)
		{
			if (requireForceMove && !modifiers.HasModifier(TargetModifiers.ForceMove))
				return false;

			return IsCorrectCargoType(target);
		}

		bool IsCorrectCargoType(Actor target)
		{
			var ci = target.Info.TraitInfo<SharedCargoInfo>();
			return ci.Types.Contains(Info.CargoType);
		}

		bool CanEnter(SharedCargo cargo)
		{
			return cargo != null && cargo.Manager.HasSpace(Info.Weight) && !cargo.IsTraitPaused;
		}

		bool CanEnter(Actor target)
		{
			return CanEnter(target.TraitOrDefault<SharedCargo>());
		}

		public string VoicePhraseForOrder(Actor self, Order order)
		{
			if (order.OrderString != "EnterSharedTransport")
				return null;

			if (order.Target.Type != TargetType.Actor || !CanEnter(order.Target.Actor))
				return null;

			return Info.Voice;
		}

		void INotifyEnteredSharedCargo.OnEnteredSharedCargo(Actor self, Actor cargo)
		{
			if (anyCargoToken == Actor.InvalidConditionToken)
				anyCargoToken = self.GrantCondition(Info.CargoCondition);

			if (specificCargoToken == Actor.InvalidConditionToken && Info.CargoConditions.TryGetValue(cargo.Info.Name, out var specificCargoCondition))
				specificCargoToken = self.GrantCondition(specificCargoCondition);
		}

		void INotifyExitedSharedCargo.OnExitedSharedCargo(Actor self, Actor cargo)
		{
			if (anyCargoToken != Actor.InvalidConditionToken)
				anyCargoToken = self.RevokeCondition(anyCargoToken);

			if (specificCargoToken != Actor.InvalidConditionToken)
				specificCargoToken = self.RevokeCondition(specificCargoToken);
		}

		public void ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != "EnterSharedTransport")
				return;

			// Enter orders are only valid for own/allied actors,
			// which are guaranteed to never be frozen.
			if (order.Target.Type != TargetType.Actor)
				return;

			var targetActor = order.Target.Actor;
			if (!CanEnter(targetActor))
				return;

			if (!IsCorrectCargoType(targetActor))
				return;

			self.QueueActivity(order.Queued, new RideSharedTransport(self, order.Target, Info.TargetLineColor));
			self.ShowTargetLines();
		}

		public bool Reserve(Actor self, SharedCargo cargo)
		{
			Unreserve(self);
			if (!cargo.ReserveSpace(self))
				return false;
			ReservedCargo = cargo;
			return true;
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self) { Unreserve(self); }

		public void Unreserve(Actor self)
		{
			if (ReservedCargo == null)
				return;
			ReservedCargo.UnreserveSpace(self);
			ReservedCargo = null;
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (Transport == null)
				return;

			// Something killed us, but it wasn't our transport blowing up. Remove us from the cargo.
			if (!Transport.IsDead)
				Transport.Trait<SharedCargo>().Unload(Transport, self);
		}

		IEnumerable<VariableObserver> IObservesVariables.GetVariableObservers()
		{
			if (Info.RequireForceMoveCondition != null)
				yield return new VariableObserver(RequireForceMoveConditionChanged, Info.RequireForceMoveCondition.Variables);
		}

		void RequireForceMoveConditionChanged(Actor self, IReadOnlyDictionary<string, int> conditions)
		{
			requireForceMove = Info.RequireForceMoveCondition.Evaluate(conditions);
		}
	}
}
