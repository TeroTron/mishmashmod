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
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Attach this to an actor (usually a building) to let it produce units or construct buildings.",
		"If one builds another actor of this type, he will get a separate queue to create two actors",
		"at the same time. Will only work together with the Production: trait.")]
	public class ProductionQueueInfo : TraitInfo, IRulesetLoaded
	{
		[FieldLoader.Require]
		[Desc("What kind of production will be added (e.g. Building, Infantry, Vehicle, ...)")]
		public readonly string Type = null;

		[Desc("The value used when ordering this for display (e.g. in the Spectator UI).")]
		public readonly int DisplayOrder = 0;

		[Desc("Group queues from separate buildings together into the same tab.")]
		public readonly string Group = null;

		[Desc("Only enable this queue for certain factions.")]
		public readonly HashSet<string> Factions = [];

		[Desc("Show the queue for these factions, even if it doesn't have any buildable unit in it.")]
		public readonly HashSet<string> AlwaysShowForFactions = new();

		[Desc("Should the prerequisite remain enabled if the owner changes?")]
		public readonly bool Sticky = true;

		[Desc("Player must pay for item upfront")]
		public readonly bool PayUpFront = false;

		[Desc("Should right clicking on the icon instantly cancel the production instead of putting it on hold?")]
		public readonly bool DisallowPaused = false;

		[Desc("This percentage value is multiplied with actor cost to translate into build time (lower means faster).")]
		public readonly int BuildDurationModifier = 100;

		[Desc("Maximum number of a single actor type that can be queued (0 = infinite).")]
		public readonly int ItemLimit = 999;

		[Desc("Maximum number of items that can be queued across all actor types (0 = infinite).")]
		public readonly int QueueLimit = 0;

		[Desc("The build time is multiplied with this percentage on low power.")]
		public readonly int LowPowerModifier = 100;

		[Desc("Production items that have more than this many items in the queue will be produced in a loop.")]
		public readonly int InfiniteBuildLimit = -1;

		[NotificationReference("Speech")]
		[Desc("Notification played when production is complete.",
			"The filename of the audio is defined per faction in notifications.yaml.")]
		public readonly string ReadyAudio = null;

		[FluentReference(optional: true)]
		[Desc("Notification displayed when production is complete.")]
		public readonly string ReadyTextNotification = null;

		[NotificationReference("Speech")]
		[Desc("Notification played when you can't train another actor",
			"when the build limit exceeded or the exit is jammed.",
			"The filename of the audio is defined per faction in notifications.yaml.")]
		public readonly string BlockedAudio = null;

		[FluentReference(optional: true)]
		[Desc("Notification displayed when you can't train another actor",
			"when the build limit exceeded or the exit is jammed.")]
		public readonly string BlockedTextNotification = null;

		[NotificationReference("Speech")]
		[Desc("Notification played when you can't queue another actor",
			"when the queue length limit is exceeded.",
			"The filename of the audio is defined per faction in notifications.yaml.")]
		public readonly string LimitedAudio = null;

		[FluentReference(optional: true)]
		[Desc("Notification displayed when you can't queue another actor",
			"when the queue length limit is exceeded.")]
		public readonly string LimitedTextNotification = null;

		[NotificationReference("Speech")]
		[Desc("Notification played when you can't place a building.",
			"Overrides PlaceBuilding.CannotPlaceNotification for this queue.",
			"The filename of the audio is defined per faction in notifications.yaml.")]
		public readonly string CannotPlaceAudio = null;

		[Desc("Notification displayed when you can't place a building.",
			"Overrides PlaceBuilding.CannotPlaceTextNotification for this queue.")]
		public readonly string CannotPlaceTextNotification = null;

		[NotificationReference("Speech")]
		[Desc("Notification played when user clicks on the build palette icon.",
			"The filename of the audio is defined per faction in notifications.yaml.")]
		public readonly string QueuedAudio = null;

		[FluentReference(optional: true)]
		[Desc("Notification displayed when user clicks on the build palette icon.")]
		public readonly string QueuedTextNotification = null;

		[NotificationReference("Speech")]
		[Desc("Notification played when player right-clicks on the build palette icon.",
			"The filename of the audio is defined per faction in notifications.yaml.")]
		public readonly string OnHoldAudio = null;

		[FluentReference(optional: true)]
		[Desc("Notification displayed when player right-clicks on the build palette icon.")]
		public readonly string OnHoldTextNotification = null;

		[NotificationReference("Speech")]
		[Desc("Notification played when player right-clicks on a build palette icon that is already on hold.",
			"The filename of the audio is defined per faction in notifications.yaml.")]
		public readonly string CancelledAudio = null;

		[FluentReference(optional: true)]
		[Desc("Notification displayed when player right-clicks on a build palette icon that is already on hold.")]
		public readonly string CancelledTextNotification = null;

		public override object Create(ActorInitializer init) { return new ProductionQueue(init, this); }

		public void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			if (LowPowerModifier <= 0)
				throw new YamlException("Production queue must have LowPowerModifier of at least 1.");
		}
	}

	public class ProductionQueue : IResolveOrder, ITick, ITechTreeElement, INotifyOwnerChanged, INotifyKilled, INotifySold, ISync, INotifyTransform, INotifyCreated
	{
		public readonly ProductionQueueInfo Info;

		// A list of things we could possibly build
		public readonly Dictionary<ActorInfo, ProductionState> Producible = [];
		protected readonly List<ProductionItem> Queue = [];
		readonly IEnumerable<ActorInfo> allProducibles;
		readonly IEnumerable<ActorInfo> buildableProducibles;

		protected Production[] productionTraits;
		protected ConditionPrerequisiteInfo[] conditionPrerequisites;

		// Will change if the owner changes
		PowerManager playerPower;
		protected PlayerResources playerResources;
		protected DeveloperMode developerMode;

		public Actor Actor { get; }

		[Sync]
		public bool Enabled { get; protected set; }

		public string Faction { get; private set; }

		public TechTree TechTree { get; private set; }

		public bool AlwaysVisible { get; private set; }

		[Sync]
		public bool IsValidFaction { get; private set; }

		public ProductionQueue(ActorInitializer init, ProductionQueueInfo info)
		{
			Actor = init.Self;
			Info = info;

			Faction = init.GetValue<FactionInit, string>(Actor.Owner.Faction.InternalName);
			IsValidFaction = info.Factions.Count == 0 || info.Factions.Contains(Faction);
			AlwaysVisible = info.AlwaysShowForFactions.Contains(Faction);
			Enabled = IsValidFaction;

			allProducibles = Producible.Where(a => a.Value.Buildable || a.Value.Visible).Select(a => a.Key);
			buildableProducibles = Producible.Where(a => a.Value.Buildable).Select(a => a.Key);
		}

		void INotifyCreated.Created(Actor self)
		{
			playerPower = self.Owner.PlayerActor.TraitOrDefault<PowerManager>();
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
			developerMode = self.Owner.PlayerActor.Trait<DeveloperMode>();
			TechTree = self.Owner.PlayerActor.Trait<TechTree>();

			productionTraits = self.TraitsImplementing<Production>().Where(p => p.Info.Produces.Contains(Info.Type)).ToArray();
			conditionPrerequisites = self.Info.TraitInfos<ConditionPrerequisiteInfo>().ToArray();
			CacheProducibles();
		}

		protected void ClearQueue()
		{
			// Refund the current item
			foreach (var item in Queue)
			{
				if (item.ResourcesPaid > 0)
				{
					playerResources.GiveResources(item.ResourcesPaid);
					item.RemainingCost += item.ResourcesPaid;
				}

				playerResources.GiveCash(item.TotalCost - item.RemainingCost);
			}

			Queue.Clear();
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			ClearQueue();

			playerPower = newOwner.PlayerActor.TraitOrDefault<PowerManager>();
			playerResources = newOwner.PlayerActor.Trait<PlayerResources>();
			developerMode = newOwner.PlayerActor.Trait<DeveloperMode>();
			TechTree = newOwner.PlayerActor.Trait<TechTree>();

			if (!Info.Sticky)
			{
				Faction = self.Owner.Faction.InternalName;
				IsValidFaction = Info.Factions.Count == 0 || Info.Factions.Contains(Faction);
				AlwaysVisible = Info.AlwaysShowForFactions.Contains(Faction);
			}

			// Regenerate the producibles and tech tree state
			oldOwner.PlayerActor.Trait<TechTree>().Remove(this);
			CacheProducibles();
			TechTree.Update();
		}

		void INotifyKilled.Killed(Actor killed, AttackInfo e) { if (killed == Actor) { ClearQueue(); Enabled = false; } }
		void INotifySold.Selling(Actor self) { ClearQueue(); Enabled = false; }
		void INotifySold.Sold(Actor self) { }

		void INotifyTransform.BeforeTransform(Actor self) { ClearQueue(); Enabled = false; }
		void INotifyTransform.OnTransform(Actor self) { }
		void INotifyTransform.AfterTransform(Actor self) { }

		public void CacheProducibles()
		{
			foreach (var a in Actor.World.Map.Rules.Actors.Values)
			{
				if (!Actor.Info.TraitInfos<ConditionPrerequisiteInfo>().Any(t => t.Queue.Contains(Info.Type) && t.Actor == a.Name))
					Producible.Remove(a);
			}

			if (!Enabled)
				return;

			foreach (var a in AllBuildables(Info.Type))
			{
				var bi = BuildableInfo.GetTraitForQueue(a, Info.Type);

				if (!Producible.ContainsKey(a))
					Producible.Add(a, new ProductionState());
				TechTree.Add(a.Name, bi.Prerequisites, bi.BuildLimit, this);
			}
		}

		IEnumerable<ActorInfo> AllBuildables(string category)
		{
			return Actor.World.Map.Rules.Actors.Values
				.Where(x =>
					x.Name[0] != '^' &&
					x.HasTraitInfo<BuildableInfo>() &&
					BuildableInfo.GetTraitForQueue(x, category) != null);
		}

		public void PrerequisitesAvailable(string key)
		{
			if (conditionPrerequisites.Any(t => t.Queue.Contains(Info.Type) && t.Actor == key))
				return;

			Producible[Actor.World.Map.Rules.Actors[key]].Buildable = true;
		}

		public void PrerequisitesUnavailable(string key)
		{
			if (conditionPrerequisites.Any(t => t.Queue.Contains(Info.Type) && t.Actor == key))
				return;

			Producible[Actor.World.Map.Rules.Actors[key]].Buildable = false;
		}

		public void PrerequisitesItemHidden(string key)
		{
			if (conditionPrerequisites.Any(t => t.Queue.Contains(Info.Type) && t.Actor == key))
				return;

			Producible[Actor.World.Map.Rules.Actors[key]].Visible = false;
		}

		public void PrerequisitesItemVisible(string key)
		{
			if (conditionPrerequisites.Any(t => t.Queue.Contains(Info.Type) && t.Actor == key))
				return;

			Producible[Actor.World.Map.Rules.Actors[key]].Visible = true;
		}

		public virtual bool IsProducing(ProductionItem item)
		{
			return Queue.Count > 0 && Queue[0] == item;
		}

		public virtual bool IsInQueue(ActorInfo actor)
		{
			return Queue.Any(i => i.Item == actor.Name);
		}

		public ProductionItem CurrentItem()
		{
			return Queue.ElementAtOrDefault(0);
		}

		public virtual IEnumerable<ProductionItem> AllQueued()
		{
			return Queue;
		}

		public virtual IEnumerable<ActorInfo> AllItems()
		{
			if (productionTraits.Length > 0 && productionTraits.All(p => p.IsTraitDisabled))
				return [];
			if (developerMode.AllTech)
				return Producible.Keys;

			return allProducibles;
		}

		public virtual IEnumerable<ActorInfo> BuildableItems()
		{
			if (productionTraits.Length > 0 && productionTraits.All(p => p.IsTraitDisabled))
				return [];
			if (!Enabled)
				return [];
			if (!Info.PayUpFront && developerMode.AllTech)
				return Producible.Keys;
			if (Info.PayUpFront && developerMode.AllTech)
				return Producible.Keys.Where(a => GetProductionCost(a) <= playerResources.GetCashAndResources() || IsInQueue(a));
			if (Info.PayUpFront)
				return buildableProducibles.Where(a => GetProductionCost(a) <= playerResources.GetCashAndResources() || IsInQueue(a));
			return buildableProducibles;
		}

		public virtual bool AnyItemsToBuild()
		{
			return Enabled
				&& (productionTraits.Length <= 0 || productionTraits.Any(p => !p.IsTraitDisabled))
				&& ((developerMode.AllTech && Producible.Keys.Count != 0) || buildableProducibles.Any());
		}

		public bool CanBuild(ActorInfo actor)
		{
			if (!Producible.TryGetValue(actor, out var ps))
				return false;

			return ps.Buildable || developerMode.AllTech;
		}

		void ITick.Tick(Actor self)
		{
			Tick(self);
		}

		protected virtual void Tick(Actor self)
		{
			// PERF: Avoid LINQ when checking whether all production traits are disabled/paused
			var anyEnabledProduction = false;
			var anyUnpausedProduction = false;
			foreach (var p in productionTraits)
			{
				anyEnabledProduction |= !p.IsTraitDisabled;
				anyUnpausedProduction |= !p.IsTraitPaused;
			}

			if (!anyEnabledProduction)
				ClearQueue();

			Enabled = IsValidFaction && anyEnabledProduction;
			TickInner(self, !anyUnpausedProduction);
		}

		protected virtual void TickInner(Actor self, bool allProductionPaused)
		{
			CancelUnbuildableItems();

			if (Queue.Count > 0 && !allProductionPaused)
				Queue[0].Tick(playerResources);
		}

		protected void CancelUnbuildableItems()
		{
			if (Queue.Count == 0)
				return;

			var buildableNames = BuildableItems().Select(b => b.Name).ToHashSet();

			// EndProduction removes the item from the queue, so we enumerate
			// by index in reverse to avoid issues with index reassignment
			for (var i = Queue.Count - 1; i >= 0; i--)
			{
				if (buildableNames.Contains(Queue[i].Item))
					continue;

				// Refund spent resources
				if (Queue[i].ResourcesPaid > 0)
				{
					playerResources.GiveResources(Queue[i].ResourcesPaid);
					Queue[i].RemainingCost += Queue[i].ResourcesPaid;
				}

				// Refund what's been paid so far
				playerResources.GiveCash(Queue[i].TotalCost - Queue[i].RemainingCost);
				EndProduction(Queue[i]);
			}
		}

		public bool CanQueue(ActorInfo actor, out string notificationAudio, out string notificationText)
		{
			notificationAudio = Info.BlockedAudio;
			notificationText = Info.BlockedTextNotification;

			var bi = BuildableInfo.GetTraitForQueue(actor, Info.Type);
			if (bi == null)
				return false;

			if (!developerMode.AllTech)
			{
				if (Info.PayUpFront && GetProductionCost(actor) > playerResources.GetCashAndResources())
				{
					notificationAudio = playerResources.Info.InsufficientFundsNotification;
					notificationText = playerResources.Info.InsufficientFundsTextNotification;

					return false;
				}

				if (Info.QueueLimit > 0 && Queue.Count >= Info.QueueLimit)
				{
					notificationAudio = bi.LimitedAudio ?? Info.LimitedAudio;
					notificationText = bi.LimitedTextNotification ?? Info.LimitedTextNotification;
					return false;
				}

				var queueCount = Queue.Count(i => i.Item == actor.Name);
				if (Info.ItemLimit > 0 && queueCount >= Info.ItemLimit)
				{
					notificationAudio = bi.LimitedAudio ?? Info.LimitedAudio;
					notificationText = bi.LimitedTextNotification ?? Info.LimitedTextNotification;
					return false;
				}

				if (bi.BuildLimit > 0)
				{
					var owned = Actor.Owner.World.ActorsHavingTrait<Buildable>()
						.Count(a => a.Info.Name == actor.Name && a.Owner == Actor.Owner);
					if (queueCount + owned >= bi.BuildLimit)
						return false;
				}
			}

			notificationAudio = bi.QueuedAudio ?? Info.QueuedAudio;
			notificationText = bi.QueuedTextNotification ?? Info.QueuedTextNotification;
			return true;
		}

		public void ResolveOrder(Actor self, Order order)
		{
			if (!Enabled)
				return;

			var rules = self.World.Map.Rules;
			switch (order.OrderString)
			{
				case "StartProduction":
					var unit = rules.Actors[order.TargetString];
					var bi = BuildableInfo.GetTraitForQueue(unit, Info.Type);

					// You can't build that
					if (BuildableItems().All(b => b.Name != order.TargetString))
						return;

					// Check if the player is trying to build more units that they are allowed
					var fromLimit = int.MaxValue;
					if (!developerMode.AllTech)
					{
						if (Info.QueueLimit > 0)
							fromLimit = Info.QueueLimit - Queue.Count;

						if (Info.ItemLimit > 0)
							fromLimit = Math.Min(fromLimit, Info.ItemLimit - Queue.Count(i => i.Item == order.TargetString));

						if (bi.BuildLimit > 0)
						{
							var inQueue = Queue.Count(pi => pi.Item == order.TargetString);
							var owned = self.Owner.World.ActorsHavingTrait<Buildable>().Count(a => a.Info.Name == order.TargetString && a.Owner == self.Owner);
							fromLimit = Math.Min(fromLimit, bi.BuildLimit - (inQueue + owned));
						}

						if (fromLimit <= 0)
							return;
					}

					var cost = GetProductionCost(unit);
					var time = GetBuildTime(unit, bi);
					var amountToBuild = Math.Min(fromLimit, order.ExtraData);
					for (var n = 0; n < amountToBuild; n++)
					{
						if (Info.PayUpFront && cost > playerResources.GetCashAndResources())
							return;

						var hasPlayedSound = false;
						BeginProduction(new ProductionItem(this, order.TargetString, cost, playerPower, () => self.World.AddFrameEndTask(_ =>
						{
							// Make sure the item hasn't been invalidated between the ProductionItem ticking and this FrameEndTask running
							if (!Queue.Any(i => i.Done && i.Item == unit.Name))
								return;

							var isBuilding = unit.HasTraitInfo<BuildingInfo>();
							var readyAudio = bi.ReadyAudio ?? Info.ReadyAudio;
							var readyTextNotification = bi.ReadyTextNotification ?? Info.ReadyTextNotification;
							if (isBuilding && !hasPlayedSound)
							{
								hasPlayedSound = Game.Sound.PlayNotification(rules, self.Owner, "Speech", readyAudio, self.Owner.Faction.InternalName);
								TextNotificationsManager.AddTransientLine(self.Owner, readyTextNotification);
							}
							else if (!isBuilding)
							{
								if (BuildUnit(unit))
								{
									Game.Sound.PlayNotification(rules, self.Owner, "Speech", readyAudio, self.Owner.Faction.InternalName);
									TextNotificationsManager.AddTransientLine(self.Owner, readyTextNotification);
								}
								else if (!hasPlayedSound && time > 0)
								{
									hasPlayedSound = Game.Sound.PlayNotification(rules, self.Owner, "Speech", Info.BlockedAudio, self.Owner.Faction.InternalName);
									TextNotificationsManager.AddTransientLine(self.Owner, Info.BlockedTextNotification);
								}
							}
						})), !order.Queued);
					}

					break;
				case "PauseProduction":
					PauseProduction(order.TargetString, order.ExtraData != 0);

					break;
				case "CancelProduction":
					CancelProduction(order.TargetString, order.ExtraData);
					break;
			}
		}

		public virtual int GetBuildTime(ActorInfo unit, BuildableInfo bi)
		{
			if (developerMode.FastBuild)
				return 0;

			var time = bi.BuildDuration;
			if (time == -1)
				time = GetProductionCost(unit);

			var modifiers = unit.TraitInfos<IProductionTimeModifierInfo>()
				.Select(t => t.GetProductionTimeModifier(TechTree, Info.Type))
				.Append(bi.BuildDurationModifier)
				.Append(Info.BuildDurationModifier);

			return Util.ApplyPercentageModifiers(time, modifiers);
		}

		public virtual int GetProductionCost(ActorInfo unit)
		{
			var valued = unit.TraitInfoOrDefault<ValuedInfo>();
			if (valued == null)
				return 0;

			var modifiers = unit.TraitInfos<IProductionCostModifierInfo>()
				.Select(t => t.GetProductionCostModifier(TechTree, Info.Type));

			return Util.ApplyPercentageModifiers(valued.Cost, modifiers);
		}

		protected virtual void PauseProduction(string itemName, bool paused)
		{
			Queue.FirstOrDefault(a => a.Item == itemName)?.Pause(paused);
		}

		protected virtual void CancelProduction(string itemName, uint numberToCancel)
		{
			for (var i = 0; i < numberToCancel; i++)
				if (!CancelProductionInner(itemName))
					break;
		}

		protected bool CancelProductionInner(string itemName)
		{
			var item = Queue.LastOrDefault(a => a.Item == itemName);

			if (item != null)
			{
				if (item.Infinite)
				{
					item.Infinite = false;
					for (var i = 1; i < Info.InfiniteBuildLimit; i++)
						Queue.Add(new ProductionItem(this, item.Item, item.TotalCost, playerPower, item.OnComplete));
				}
				else
				{
					// Refund what has been paid
					if (item.ResourcesPaid > 0)
					{
						playerResources.GiveResources(item.ResourcesPaid);
						item.RemainingCost += item.ResourcesPaid;
					}

					playerResources.GiveCash(item.TotalCost - item.RemainingCost);
					EndProduction(item);
				}

				return true;
			}

			return false;
		}

		public void EndProduction(ProductionItem item)
		{
			Queue.Remove(item);

			if (item.Infinite)
				Queue.Add(new ProductionItem(this, item.Item, item.TotalCost, playerPower, item.OnComplete) { Infinite = true });
		}

		protected virtual void BeginProduction(ProductionItem item, bool hasPriority)
		{
			if (Info.PayUpFront)
			{
				if (playerResources.Resources > 0 && playerResources.Resources <= item.TotalCost)
					item.ResourcesPaid = playerResources.Resources;
				else if (playerResources.Resources > item.TotalCost)
					item.ResourcesPaid = item.TotalCost;

				playerResources.TakeCash(item.TotalCost);
				item.RemainingCost = 0;
			}

			if (Queue.Any(i => i.Item == item.Item && i.Infinite))
				return;
			if (hasPriority && Queue.Count > 1)
				Queue.Insert(1, item);
			else
				Queue.Add(item);

			if (Info.InfiniteBuildLimit < 0)
				return;

			var queued = Queue.FindAll(i => i.Item == item.Item);

			if (queued.Count <= Info.InfiniteBuildLimit)
				return;

			queued[0].Infinite = true;

			for (var i = 1; i < queued.Count; i++)
			{
				// Refund what has been paid
				if (queued[i].ResourcesPaid > 0)
				{
					playerResources.GiveResources(queued[i].ResourcesPaid);
					queued[i].RemainingCost += queued[i].ResourcesPaid;
				}

				playerResources.GiveCash(queued[i].TotalCost - queued[i].RemainingCost);
				EndProduction(queued[i]);
			}
		}

		public virtual int RemainingTimeActual(ProductionItem item)
		{
			return item.RemainingTimeActual;
		}

		// Returns the actor/trait that is most likely (but not necessarily guaranteed) to produce something in this queue
		public virtual TraitPair<Production> MostLikelyProducer()
		{
			var trait = productionTraits
				.Where(p => !p.IsTraitDisabled && p.Info.Produces.Contains(Info.Type))
				.OrderBy(p => p.IsTraitPaused)
				.FirstOrDefault();
			return new TraitPair<Production>(Actor, trait);
		}

		// Builds a unit from the actor that holds this queue (1 queue per building)
		// Returns false if the unit can't be built
		protected virtual bool BuildUnit(ActorInfo unit)
		{
			var mostLikelyProducerTrait = MostLikelyProducer().Trait;

			// Cannot produce if I'm dead or trait is disabled
			if (!Actor.IsInWorld || Actor.IsDead || mostLikelyProducerTrait == null)
			{
				CancelProduction(unit.Name, 1);
				return false;
			}

			var inits = new TypeDictionary
			{
				new OwnerInit(Actor.Owner),
				new FactionInit(BuildableInfo.GetInitialFaction(unit, Faction))
			};

			var bi = BuildableInfo.GetTraitForQueue(unit, Info.Type);
			var type = developerMode.AllTech ? Info.Type : (bi.BuildAtProductionType ?? Info.Type);
			var item = Queue.First(i => i.Done && i.Item == unit.Name);
			if (!mostLikelyProducerTrait.IsTraitPaused && mostLikelyProducerTrait.Produce(Actor, unit, type, inits, item.TotalCost))
			{
				EndProduction(item);
				return true;
			}

			return false;
		}
	}

	public class ProductionState
	{
		public bool Visible = true;
		public bool Buildable = false;
	}

	public class ProductionItem
	{
		public readonly string Item;
		public readonly ProductionQueue Queue;
		public readonly int TotalCost;
		public readonly Action OnComplete;
		public int TotalTime { get; private set; }
		public int RemainingTime { get; private set; }
		public int RemainingCost { get; set; }
		public int ResourcesPaid { get; set; }
		public int RemainingTimeActual =>
			(pm == null || pm.PowerState == PowerState.Normal) ? RemainingTime :
				RemainingTime * Queue.Info.LowPowerModifier / 100;

		public bool Paused { get; private set; }
		public bool Done { get; private set; }
		public bool Started { get; private set; }
		public int Slowdown { get; private set; }
		public bool Infinite { get; set; }
		public int BuildPaletteOrder { get; }

		public readonly ActorInfo ActorInfo;
		public readonly BuildableInfo BuildableInfo;
		readonly PowerManager pm;

		public ProductionItem(ProductionQueue queue, string item, int cost, PowerManager pm, Action onComplete)
		{
			Item = item;
			RemainingTime = TotalTime = 1;
			RemainingCost = TotalCost = cost;
			ResourcesPaid = 0;
			OnComplete = onComplete;
			Queue = queue;
			this.pm = pm;
			ActorInfo = Queue.Actor.World.Map.Rules.Actors[Item];
			BuildableInfo = BuildableInfo.GetTraitForQueue(ActorInfo, Queue.Info.Type);
			BuildPaletteOrder = BuildableInfo.BuildPaletteOrder;
			Infinite = false;
		}

		public void Tick(PlayerResources pr)
		{
			if (!Started)
			{
				var time = Queue.GetBuildTime(ActorInfo, BuildableInfo);
				if (time > 0)
					RemainingTime = TotalTime = time;
				Started = true;
			}

			if (Done)
			{
				OnComplete?.Invoke();

				return;
			}

			if (Paused)
				return;

			if (pm != null && pm.PowerState != PowerState.Normal)
			{
				Slowdown -= 100;
				if (Slowdown < 0)
					Slowdown = Queue.Info.LowPowerModifier + Slowdown;
				else
					return;
			}

			if (!Queue.Info.PayUpFront)
			{
				var expectedRemainingCost = RemainingTime == 1 ? 0 : TotalCost * RemainingTime / Math.Max(1, TotalTime);
				var costThisFrame = RemainingCost - expectedRemainingCost;
				if (pr.Resources > 0 && pr.Resources <= costThisFrame)
					ResourcesPaid += pr.Resources;
				else if (pr.Resources > costThisFrame)
					ResourcesPaid += costThisFrame;
				if (costThisFrame != 0 && !pr.TakeCash(costThisFrame, true))
				{
					ResourcesPaid -= pr.Resources;
					return;
				}

				RemainingCost -= costThisFrame;
			}

			RemainingTime--;
			if (RemainingTime > 0)
				return;

			Done = true;
		}

		public void Pause(bool paused) { Paused = paused; }
	}
}
