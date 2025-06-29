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
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Overrides the default Tooltip when this actor is disguised (aids in deceiving enemy players).")]
	public sealed class DisguiseTooltipInfo : TooltipInfo, Requires<DisguiseInfo>
	{
		public override object Create(ActorInitializer init) { return new DisguiseTooltip(init.Self, this); }
	}

	public sealed class DisguiseTooltip : ConditionalTrait<DisguiseTooltipInfo>, ITooltip
	{
		readonly Actor self;
		readonly Disguise disguise;

		public DisguiseTooltip(Actor self, DisguiseTooltipInfo info)
			: base(info)
		{
			this.self = self;
			disguise = self.Trait<Disguise>();
		}

		public ITooltipInfo TooltipInfo => disguise.Disguised ? disguise.AsTooltipInfo : Info;

		public Player Owner
		{
			get
			{
				if (!disguise.Disguised || self.Owner.IsAlliedWith(self.World.RenderPlayer))
					return self.Owner;

				return disguise.AsPlayer;
			}
		}
	}

	[Flags]
	public enum RevealDisguiseType
	{
		None = 0,
		Attack = 1,
		Damaged = 2,
		Load = 4,
		Unload = 8,
		Infiltrate = 16,
		Demolish = 32,
		Move = 64,
	}

	[Desc("Provides access to the disguise command, which makes the actor appear to be another player's actor.")]
	public sealed class DisguiseInfo : TraitInfo
	{
		[VoiceReference]
		public readonly string Voice = "Action";

		[GrantedConditionReference]
		[Desc("The condition to grant to self while disguised.")]
		public readonly string DisguisedCondition = null;

		[Desc("Player relationships the owner of the disguise target needs.")]
		public readonly PlayerRelationship ValidRelationships = PlayerRelationship.Ally | PlayerRelationship.Neutral | PlayerRelationship.Enemy;

		[Desc("Target types of actors that this actor disguise as.")]
		public readonly BitSet<TargetableType> TargetTypes = new("Disguise");

		[Desc("Triggers which cause the actor to drop it's disguise. Possible values: None, Attack, Damaged,",
			"Unload, Infiltrate, Demolish, Move.")]
		public readonly RevealDisguiseType RevealDisguiseOn = RevealDisguiseType.Attack;

		[ActorReference(dictionaryReference: LintDictionaryReference.Keys)]
		[Desc("Conditions to grant when disguised as specified actor.",
			"A dictionary of [actor id]: [condition].")]
		public readonly Dictionary<string, string> DisguisedAsConditions = [];

		[CursorReference]
		[Desc("Cursor to display when hovering over a valid actor to disguise as.")]
		public readonly string Cursor = "ability";

		[GrantedConditionReference]
		public IEnumerable<string> LinterConditions => DisguisedAsConditions.Values;

		public override object Create(ActorInitializer init) { return new Disguise(init.Self, this); }
	}

	public sealed class Disguise : IEffectiveOwner, IIssueOrder, IResolveOrder, IOrderVoice, INotifyAttack,
		INotifyDamage, INotifyLoadCargo, INotifyUnloadCargo, INotifyDemolition, INotifyInfiltration, ITick
	{
		public ActorInfo AsActor { get; private set; }
		public Player AsPlayer { get; private set; }
		public string AsSprite { get; private set; }
		public ITooltipInfo AsTooltipInfo { get; private set; }
		public List<WVec> TurretOffsets = new() { WVec.Zero };

		public bool Disguised => AsPlayer != null;
		public Player Owner => AsPlayer;

		readonly Actor self;
		public readonly DisguiseInfo Info;

		int disguisedToken = Actor.InvalidConditionToken;
		int disguisedAsToken = Actor.InvalidConditionToken;
		CPos? lastPos;

		readonly INotifyDisguised[] notifiers;

		public Disguise(Actor self, DisguiseInfo info)
		{
			this.self = self;
			Info = info;

			AsActor = self.Info;
			notifiers = self.TraitsImplementing<INotifyDisguised>().ToArray();
		}

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get
			{
				yield return new DisguiseOrderTargeter(Info);
			}
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			if (order.OrderID == "Disguise")
				return new Order(order.OrderID, self, target, queued);

			return null;
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString == "Disguise")
			{
				var target = order.Target;
				if (target.Type == TargetType.Actor)
					DisguiseAs((target.Actor != self && target.Actor.IsInWorld) ? target.Actor : null);

				if (target.Type == TargetType.FrozenActor)
					DisguiseAs(target.FrozenActor.Info, target.FrozenActor.Owner);
			}
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order)
		{
			return order.OrderString == "Disguise" ? Info.Voice : null;
		}

		public void DisguiseAs(Actor target)
		{
			var oldEffectiveActor = AsActor;
			var oldEffectiveOwner = AsPlayer;
			var oldDisguiseSetting = Disguised;

			if (target != null)
			{
				// Take the image of the target's disguise, if it exists.
				// E.g., SpyA is disguised as a rifle infantry. SpyB then targets SpyA. We should use the rifle infantry image.
				var targetDisguise = target.TraitOrDefault<Disguise>();
				if (targetDisguise != null && targetDisguise.Disguised)
				{
					// Don't disguise as yourself
					if (targetDisguise.AsActor.Name == self.Info.Name && targetDisguise.AsPlayer == self.Owner)
					{
						AsSprite = null;
						AsPlayer = null;
						AsActor = self.Info;
						AsTooltipInfo = null;
					}
					else
					{
						AsSprite = targetDisguise.AsSprite;
						AsPlayer = targetDisguise.AsPlayer;
						AsActor = targetDisguise.AsActor;
						AsTooltipInfo = targetDisguise.AsTooltipInfo;
					}
				}
				else
				{
					// Don't disguise as yourself
					if (target.Info.Name == self.Info.Name && target.Owner == self.Owner)
					{
						AsSprite = null;
						AsPlayer = null;
						AsActor = self.Info;
						AsTooltipInfo = null;
					}
					else
					{
						var tooltip = target.TraitsImplementing<ITooltip>().FirstEnabledTraitOrDefault();
						if (tooltip == null)
							throw new ArgumentException("Missing tooltip or invalid target.", nameof(target));

						AsSprite = target.Trait<RenderSprites>().GetImage(target);
						AsPlayer = tooltip.Owner;
						AsActor = target.Info;
						AsTooltipInfo = tooltip.TooltipInfo;

						var targetTurreted = target.TraitsImplementing<Turreted>();
						if (targetTurreted != null)
						{
							TurretOffsets.Clear();
							foreach (var t in targetTurreted)
								TurretOffsets.Add(t.Offset);
						}
						else
						{
							TurretOffsets.Clear();
							TurretOffsets.Add(WVec.Zero);
						}

						foreach (var nd in notifiers)
							nd.DisguiseChanged(self, target);
					}
				}
			}
			else
			{
				AsTooltipInfo = null;
				AsPlayer = null;
				AsActor = self.Info;
				AsSprite = null;

				TurretOffsets.Clear();
				TurretOffsets.Add(WVec.Zero);

				foreach (var nd in notifiers)
					nd.DisguiseChanged(self, self);
			}

			HandleDisguise(oldEffectiveActor, oldEffectiveOwner, oldDisguiseSetting);
		}

		public void DisguiseAs(ActorInfo actorInfo, Player newOwner)
		{
			var oldEffectiveActor = AsActor;
			var oldEffectiveOwner = AsPlayer;
			var oldDisguiseSetting = Disguised;

			var renderSprites = actorInfo.TraitInfoOrDefault<RenderSpritesInfo>();
			AsSprite = renderSprites?.GetImage(actorInfo, newOwner.Faction.InternalName);
			AsPlayer = newOwner;
			AsActor = actorInfo;
			AsTooltipInfo = actorInfo.TraitInfos<TooltipInfo>().FirstOrDefault(info => info.EnabledByDefault);

			var targetTurreted = actorInfo.TraitInfos<TurretedInfo>();
			if (targetTurreted != null)
			{
				TurretOffsets.Clear();
				foreach (var t in targetTurreted)
					TurretOffsets.Add(t.Offset);
			}
			else
			{
				TurretOffsets.Clear();
				TurretOffsets.Add(WVec.Zero);
			}

			HandleDisguise(oldEffectiveActor, oldEffectiveOwner, oldDisguiseSetting);
		}

		void HandleDisguise(ActorInfo oldEffectiveActor, Player oldEffectiveOwner, bool oldDisguiseSetting)
		{
			foreach (var t in self.TraitsImplementing<INotifyEffectiveOwnerChanged>())
				t.OnEffectiveOwnerChanged(self, oldEffectiveOwner, AsPlayer);

			if (Disguised != oldDisguiseSetting)
			{
				if (Disguised && disguisedToken == Actor.InvalidConditionToken)
					disguisedToken = self.GrantCondition(Info.DisguisedCondition);
				else if (!Disguised && disguisedToken != Actor.InvalidConditionToken)
					disguisedToken = self.RevokeCondition(disguisedToken);
			}

			if (AsActor != oldEffectiveActor)
			{
				if (disguisedAsToken != Actor.InvalidConditionToken)
					disguisedAsToken = self.RevokeCondition(disguisedAsToken);

				if (Info.DisguisedAsConditions.TryGetValue(AsActor.Name, out var disguisedAsCondition))
					disguisedAsToken = self.GrantCondition(disguisedAsCondition);
			}
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (Info.RevealDisguiseOn.HasFlag(RevealDisguiseType.Attack))
				DisguiseAs(null);
		}

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (Info.RevealDisguiseOn.HasFlag(RevealDisguiseType.Damaged) && e.Damage.Value > 0)
				DisguiseAs(null);
		}

		void INotifyLoadCargo.Loading(Actor self)
		{
			if (Info.RevealDisguiseOn.HasFlag(RevealDisguiseType.Load))
				DisguiseAs(null);
		}

		void INotifyUnloadCargo.Unloading(Actor self)
		{
			if (Info.RevealDisguiseOn.HasFlag(RevealDisguiseType.Unload))
				DisguiseAs(null);
		}

		void INotifyDemolition.Demolishing(Actor self)
		{
			if (Info.RevealDisguiseOn.HasFlag(RevealDisguiseType.Demolish))
				DisguiseAs(null);
		}

		void INotifyInfiltration.Infiltrating(Actor self)
		{
			if (Info.RevealDisguiseOn.HasFlag(RevealDisguiseType.Infiltrate))
				DisguiseAs(null);
		}

		void ITick.Tick(Actor self)
		{
			if (Info.RevealDisguiseOn.HasFlag(RevealDisguiseType.Move) && lastPos != null && lastPos.Value != self.Location)
				DisguiseAs(null);

			lastPos = self.Location;
		}
	}

	public sealed class DisguiseOrderTargeter : UnitOrderTargeter
	{
		readonly DisguiseInfo info;

		public DisguiseOrderTargeter(DisguiseInfo info)
			: base("Disguise", 7, info.Cursor, true, true)
		{
			this.info = info;
			ForceAttack = false;
		}

		public override bool CanTargetActor(Actor self, Actor target, TargetModifiers modifiers, ref string cursor)
		{
			var relationship = self.Owner.RelationshipWith(target.Owner);
			if (!info.ValidRelationships.HasRelationship(relationship))
				return false;

			if (target.Equals(self))
				return false;

			return info.TargetTypes.Overlaps(target.GetAllTargetTypes());
		}

		public override bool CanTargetFrozenActor(Actor self, FrozenActor target, TargetModifiers modifiers, ref string cursor)
		{
			var relationship = self.Owner.RelationshipWith(target.Owner);
			if (!info.ValidRelationships.HasRelationship(relationship))
				return false;

			return info.TargetTypes.Overlaps(target.Info.GetAllTargetTypes());
		}
	}
}
