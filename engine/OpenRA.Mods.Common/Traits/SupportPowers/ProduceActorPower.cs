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

using System.Linq;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Produces an actor without using the standard production queue.")]
	public class ProduceActorPowerInfo : SupportPowerInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Actors to produce.")]
		public readonly string[] Actors = null;

		[FieldLoader.Require]
		[Desc("Production queue type to use")]
		public readonly string Type = null;

		[NotificationReference("Speech")]
		[Desc("Speech notification played when production is activated.",
			"The filename of the audio is defined per faction in notifications.yaml.")]
		public readonly string ReadyAudio = null;

		[FluentReference(optional: true)]
		[Desc("Text notification displayed when production is activated.")]
		public readonly string ReadyTextNotification = null;

		[NotificationReference("Speech")]
		[Desc("Speech notification played when the exit is jammed.",
			"The filename of the audio is defined per faction in notifications.yaml.")]
		public readonly string BlockedAudio = null;

		[FluentReference(optional: true)]
		[Desc("Text notification displayed when the exit is jammed.")]
		public readonly string BlockedTextNotification = null;

		[Desc("Allows the actors to be produced immediately when charged.")]
		public readonly bool AutoFire = false;

		public override object Create(ActorInitializer init) { return new ProduceActorPower(init, this); }
	}

	public class ProduceActorPower : SupportPower, ITick
	{
		readonly string faction;
		readonly string key;
		readonly bool autoFire;

		int ticks;

		public ProduceActorPower(ActorInitializer init, ProduceActorPowerInfo info)
			: base(init.Self, info)
		{
			faction = init.GetValue<FactionInit, string>(init.Self.Owner.Faction.InternalName);
			autoFire = info.AutoFire;
			key = info.AllowMultiple ? info.OrderName + "_" + init.Self.ActorID : info.OrderName;
		}

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			self.World.IssueOrder(new Order(order, manager.Self, false));
		}

		public override void Charged(Actor self, string key)
		{
			base.Charged(self, key);

			if (autoFire)
			{
				self.Owner.PlayerActor.Trait<SupportPowerManager>().Powers[key].Activate(new Order());
				ticks = 10;
			}
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			PlayLaunchSounds();

			var info = Info as ProduceActorPowerInfo;
			var producers = self.World.ActorsWithTrait<Production>()
				.Where(x => x.Actor.Owner == self.Owner
					&& !x.Trait.IsTraitDisabled
					&& x.Trait.Info.Produces.Contains(info.Type))
					.OrderByDescending(x => x.Actor.IsPrimaryBuilding())
					.ThenByDescending(x => x.Actor.ActorID);

			// TODO: The power should not reset if the production fails.
			// Fixing this will require a larger rework of the support power code
			var activated = false;

			foreach (var p in producers)
			{
				foreach (var name in info.Actors)
				{
					var ai = self.World.Map.Rules.Actors[name];
					var inits = new TypeDictionary
					{
						new OwnerInit(self.Owner),
						new FactionInit(BuildableInfo.GetInitialFaction(ai, faction))
					};

					activated |= p.Trait.Produce(p.Actor, ai, info.Type, inits, 0);
				}

				if (activated)
					break;
			}

			if (activated)
			{
				Game.Sound.PlayNotification(self.World.Map.Rules, manager.Self.Owner, "Speech", info.ReadyAudio, self.Owner.Faction.InternalName);
				TextNotificationsManager.AddTransientLine(manager.Self.Owner, info.ReadyTextNotification);
			}
			else
			{
				Game.Sound.PlayNotification(self.World.Map.Rules, manager.Self.Owner, "Speech", info.BlockedAudio, self.Owner.Faction.InternalName);
				TextNotificationsManager.AddTransientLine(manager.Self.Owner, info.BlockedTextNotification);
			}
		}

		void ITick.Tick(Actor self)
		{
			if (autoFire && self.Owner.PlayerActor.Trait<SupportPowerManager>().Powers[key].Ready && --ticks < 0)
				self.Owner.PlayerActor.Trait<SupportPowerManager>().Powers[key].Activate(new Order());
		}
	}
}
