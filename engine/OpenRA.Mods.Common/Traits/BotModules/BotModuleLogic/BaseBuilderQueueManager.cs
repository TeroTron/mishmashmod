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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	sealed class BaseBuilderQueueManager
	{
		public readonly string Category;
		public int WaitTicks;

		readonly BaseBuilderBotModule baseBuilder;
		readonly World world;
		readonly Player player;
		readonly PlayerResources playerResources;
		readonly IResourceLayer resourceLayer;

		Actor[] playerBuildings;
		int failCount;
		int failRetryTicks;
		int checkForBasesTicks;
		int cachedBases;
		int cachedBuildings;
		int minimumExcessPower;

		bool itemQueuedThisTick = false;

		WaterCheck waterState = WaterCheck.NotChecked;

		public BaseBuilderQueueManager(BaseBuilderBotModule baseBuilder, string category, Player p, PlayerResources pr, IResourceLayer rl)
		{
			this.baseBuilder = baseBuilder;
			world = p.World;
			player = p;
			playerResources = pr;
			resourceLayer = rl;
			Category = category;
			failRetryTicks = baseBuilder.Info.StructureProductionResumeDelay;
			minimumExcessPower = baseBuilder.Info.MinimumExcessPower;
			if (baseBuilder.Info.NavalProductionTypes.Count == 0)
				waterState = WaterCheck.DontCheck;
		}

		public void Tick(IBot bot, ILookup<string, ProductionQueue> queuesByCategory)
		{
			// If failed to place something N consecutive times, wait M ticks until resuming building production
			if (failCount >= baseBuilder.Info.MaximumFailedPlacementAttempts && --failRetryTicks <= 0)
			{
				var currentBuildings = world.ActorsHavingTrait<Building>().Count(a => a.Owner == player);
				var baseProviders = world.ActorsHavingTrait<BaseProvider>().Count(a => a.Owner == player);

				// Only bother resetting failCount if either a) the number of buildings has decreased since last failure M ticks ago,
				// or b) number of BaseProviders (construction yard or similar) has increased since then.
				// Otherwise reset failRetryTicks instead to wait again.
				if (currentBuildings < cachedBuildings || baseProviders > cachedBases)
					failCount = 0;
				else
					failRetryTicks = baseBuilder.Info.StructureProductionResumeDelay;
			}

			if (waterState == WaterCheck.NotChecked)
			{
				if (AIUtils.IsAreaAvailable<BaseProvider>(world, player, world.Map, baseBuilder.Info.MaxBaseRadius, baseBuilder.Info.WaterTerrainTypes))
					waterState = WaterCheck.EnoughWater;
				else
				{
					waterState = WaterCheck.NotEnoughWater;
					checkForBasesTicks = baseBuilder.Info.CheckForNewBasesDelay;
				}
			}

			if (waterState == WaterCheck.NotEnoughWater && --checkForBasesTicks <= 0)
			{
				var currentBases = world.ActorsHavingTrait<BaseProvider>().Count(a => a.Owner == player);

				if (currentBases > cachedBases)
				{
					cachedBases = currentBases;
					waterState = WaterCheck.NotChecked;
				}
			}

			// Only update once per second or so
			if (WaitTicks > 0)
				return;

			playerBuildings = world.ActorsHavingTrait<Building>().Where(a => a.Owner == player).ToArray();
			var excessPowerBonus =
				baseBuilder.Info.ExcessPowerIncrement *
				(playerBuildings.Length / baseBuilder.Info.ExcessPowerIncreaseThreshold.Clamp(1, int.MaxValue));
			minimumExcessPower =
				(baseBuilder.Info.MinimumExcessPower + excessPowerBonus)
					.Clamp(baseBuilder.Info.MinimumExcessPower, baseBuilder.Info.MaximumExcessPower);

			// PERF: Queue only one actor at a time per category
			itemQueuedThisTick = false;
			var active = false;
			foreach (var queue in queuesByCategory[Category])
			{
				if (TickQueue(bot, queue))
					active = true;
			}

			// Add a random factor so not every AI produces at the same tick early in the game.
			// Minimum should not be negative as delays in HackyAI could be zero.
			var randomFactor = world.LocalRandom.Next(0, baseBuilder.Info.StructureProductionRandomBonusDelay);

			WaitTicks = active ? baseBuilder.Info.StructureProductionActiveDelay + randomFactor
				: baseBuilder.Info.StructureProductionInactiveDelay + randomFactor;
		}

		bool TickQueue(IBot bot, ProductionQueue queue)
		{
			var currentBuilding = queue.AllQueued().FirstOrDefault();

			// Waiting to build something
			if (currentBuilding == null && failCount < baseBuilder.Info.MaximumFailedPlacementAttempts)
			{
				// PERF: We shouldn't be queueing new units when we're low on cash
				if (playerResources.GetCashAndResources() < baseBuilder.Info.ProductionMinCashRequirement || itemQueuedThisTick)
					return false;

				var item = ChooseBuildingToBuild(queue);
				if (item == null)
					return false;

				bot.QueueOrder(Order.StartProduction(queue.Actor, item.Name, 1));
				itemQueuedThisTick = true;
			}
			else if (currentBuilding != null && currentBuilding.Done)
			{
				// Production is complete
				// Choose the placement logic
				// HACK: HACK HACK HACK
				// TODO: Derive this from BuildingCommonNames instead
				var type = BuildingType.Building;
				CPos? location = null;
				var actorVariant = 0;
				var orderString = "PlaceBuilding";

				// Check if Building is a plug for other Building
				var actorInfo = world.Map.Rules.Actors[currentBuilding.Item];
				var plugInfo = actorInfo.TraitInfoOrDefault<PlugInfo>();

				if (plugInfo != null)
				{
					var possibleBuilding = world.ActorsWithTrait<Pluggable>().FirstOrDefault(a =>
						a.Actor.Owner == player && a.Trait.AcceptsPlug(plugInfo.Type));

					if (possibleBuilding.Actor != null)
					{
						orderString = "PlacePlug";
						location = possibleBuilding.Actor.Location + possibleBuilding.Trait.Info.Offset;
					}
				}
				else
				{
					// Check if Building is a defense and if we should place it towards the enemy or not.
					if (baseBuilder.Info.DefenseTypes.Contains(actorInfo.Name) && world.LocalRandom.Next(100) < baseBuilder.Info.PlaceDefenseTowardsEnemyChance)
						type = BuildingType.Defense;
					else if (baseBuilder.Info.RefineryTypes.Contains(actorInfo.Name))
						type = BuildingType.Refinery;

					(location, actorVariant) = ChooseBuildLocation(currentBuilding.Item, true, queue.Actor, type);
				}

				if (location == null)
				{
					AIUtils.BotDebug($"{player} has nowhere to place {currentBuilding.Item}");
					bot.QueueOrder(Order.CancelProduction(queue.Actor, currentBuilding.Item, 1));
					failCount += failCount;

					// If we just reached the maximum fail count, cache the number of current structures
					if (failCount == baseBuilder.Info.MaximumFailedPlacementAttempts)
					{
						cachedBuildings = world.ActorsHavingTrait<Building>().Count(a => a.Owner == player);
						cachedBases = world.ActorsHavingTrait<BaseProvider>().Count(a => a.Owner == player);
					}
				}
				else
				{
					failCount = 0;

					bot.QueueOrder(new Order(orderString, player.PlayerActor, Target.FromCell(world, location.Value), false)
					{
						// Building to place
						TargetString = currentBuilding.Item,

						// Actor variant will always be small enough to safely pack in a CPos
						ExtraLocation = new CPos(actorVariant, 0),

						// Actor ID to associate the placement with
						ExtraData = queue.Actor.ActorID,
						SuppressVisualFeedback = true
					});

					return true;
				}
			}

			return true;
		}

		ActorInfo GetProducibleBuilding(HashSet<string> actors, IEnumerable<ActorInfo> buildables, Func<ActorInfo, int> orderBy = null)
		{
			var available = buildables.Where(actor =>
			{
				// Are we able to build this?
				if (!actors.Contains(actor.Name))
					return false;

				if (!baseBuilder.Info.BuildingLimits.TryGetValue(actor.Name, out var limit))
					return true;

				return playerBuildings.Count(a => a.Info.Name == actor.Name) +
					(baseBuilder.BuildingsBeingProduced.TryGetValue(actor.Name, out var beingProduced) ? beingProduced : 0) < limit;
			});

			if (orderBy != null)
				return available.MaxByOrDefault(orderBy);

			return available.RandomOrDefault(world.LocalRandom);
		}

		bool HasSufficientPowerForActor(ActorInfo actorInfo)
		{
			return baseBuilder.PlayerPower == null || actorInfo.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault)
				.Sum(p => p.Amount) + baseBuilder.ExcessPower >= baseBuilder.Info.MinimumExcessPower;
		}

		ActorInfo ChooseBuildingToBuild(ProductionQueue queue)
		{
			var buildableThings = queue.BuildableItems().ToList();

			// This gets used quite a bit, so let's cache it here
			var power = GetProducibleBuilding(baseBuilder.Info.PowerTypes, buildableThings,
				a => a.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault).Sum(p => p.Amount));

			// First priority is to get out of a low power situation
			if (baseBuilder.PlayerPower != null && baseBuilder.ExcessPower < minimumExcessPower &&
				power != null && power.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault).Sum(p => p.Amount) > 0)
			{
				AIUtils.BotDebug("{0} decided to build {1}: Priority override (low power)", queue.Actor.Owner, power.Name);
				return power;
			}

			// Next is to build up a strong economy
			if (!baseBuilder.HasAdequateRefineryCount())
			{
				var refinery = GetProducibleBuilding(baseBuilder.Info.RefineryTypes, buildableThings);
				if (refinery != null && HasSufficientPowerForActor(refinery))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (refinery)", queue.Actor.Owner, refinery.Name);
					return refinery;
				}

				if (power != null && refinery != null && !HasSufficientPowerForActor(refinery))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);
					return power;
				}
			}

			// Make sure that we can spend as fast as we are earning
			if (baseBuilder.Info.NewProductionCashThreshold > 0 && playerResources.GetCashAndResources() > baseBuilder.Info.NewProductionCashThreshold)
			{
				var production = GetProducibleBuilding(baseBuilder.Info.ProductionTypes, buildableThings);
				if (production != null && HasSufficientPowerForActor(production))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (production)", queue.Actor.Owner, production.Name);
					return production;
				}

				if (power != null && production != null && !HasSufficientPowerForActor(production))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);
					return power;
				}
			}

			// Only consider building this if there is enough water inside the base perimeter and there are close enough adjacent buildings
			if (waterState == WaterCheck.EnoughWater && baseBuilder.Info.NewProductionCashThreshold > 0
				&& playerResources.GetCashAndResources() > baseBuilder.Info.NewProductionCashThreshold
				&& AIUtils.IsAreaAvailable<GivesBuildableArea>(world, player, world.Map, baseBuilder.Info.CheckForWaterRadius, baseBuilder.Info.WaterTerrainTypes))
			{
				var navalproduction = GetProducibleBuilding(baseBuilder.Info.NavalProductionTypes, buildableThings);
				if (navalproduction != null && HasSufficientPowerForActor(navalproduction))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (navalproduction)", queue.Actor.Owner, navalproduction.Name);
					return navalproduction;
				}

				if (power != null && navalproduction != null && !HasSufficientPowerForActor(navalproduction))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);
					return power;
				}
			}

			// Create some head room for resource storage if we really need it
			if (playerResources.Resources > 0.8 * playerResources.ResourceCapacity)
			{
				var silo = GetProducibleBuilding(baseBuilder.Info.SiloTypes, buildableThings);
				if (silo != null && HasSufficientPowerForActor(silo))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (silo)", queue.Actor.Owner, silo.Name);
					return silo;
				}

				if (power != null && silo != null && !HasSufficientPowerForActor(silo))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);
					return power;
				}
			}

			// Build everything else
			foreach (var frac in baseBuilder.Info.BuildingFractions.Shuffle(world.LocalRandom))
			{
				var name = frac.Key;

				// Does this building have initial delay, if so have we passed it?
				if (baseBuilder.Info.BuildingDelays != null &&
					baseBuilder.Info.BuildingDelays.TryGetValue(name, out var delay) &&
					delay > world.WorldTick)
					continue;

				// Can we build this structure?
				if (!buildableThings.Any(b => b.Name == name))
					continue;

				// Check the number of this structure and its variants
				var actorInfo = world.Map.Rules.Actors[name];
				var buildingVariantInfo = actorInfo.TraitInfoOrDefault<PlaceBuildingVariantsInfo>();
				var variants = buildingVariantInfo?.Actors ?? [];

				var count = playerBuildings.Count(a =>
					a.Info.Name == name || variants.Contains(a.Info.Name)) +
					(baseBuilder.BuildingsBeingProduced.TryGetValue(name, out var num) ? num : 0);

				// Do we want to build this structure?
				if (count * 100 > frac.Value * playerBuildings.Length)
					continue;

				if (baseBuilder.Info.BuildingLimits.TryGetValue(name, out var limit) && limit <= count)
					continue;

				// If we're considering to build a naval structure, check whether there is enough water inside the base perimeter
				// and any structure providing buildable area close enough to that water.
				// TODO: Extend this check to cover any naval structure, not just production.
				if (baseBuilder.Info.NavalProductionTypes.Contains(name)
					&& (waterState == WaterCheck.NotEnoughWater
						|| !AIUtils.IsAreaAvailable<GivesBuildableArea>(world, player, world.Map, baseBuilder.Info.CheckForWaterRadius, baseBuilder.Info.WaterTerrainTypes)))
					continue;

				// Will this put us into low power?
				var actor = world.Map.Rules.Actors[name];
				if (baseBuilder.PlayerPower != null && (baseBuilder.ExcessPower < minimumExcessPower || !HasSufficientPowerForActor(actor)))
				{
					// Try building a power plant instead
					if (power != null && power.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault).Sum(pi => pi.Amount) > 0)
					{
						if (baseBuilder.PlayerPower.PowerOutageRemainingTicks > 0)
							AIUtils.BotDebug("{0} decided to build {1}: Priority override (is low power)", queue.Actor.Owner, power.Name);
						else
							AIUtils.BotDebug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);

						return power;
					}
				}

				// Lets build this
				AIUtils.BotDebug("{0} decided to build {1}: Desired is {2} ({3} / {4}); current is {5} / {4}",
					queue.Actor.Owner, name, frac.Value, frac.Value * playerBuildings.Length, playerBuildings.Length, count);
				return actor;
			}

			// Too spammy to keep enabled all the time, but very useful when debugging specific issues.
			// AIUtils.BotDebug("{0} couldn't decide what to build for queue {1}.", queue.Actor.Owner, queue.Info.Group);
			return null;
		}

		(CPos? Location, int Variant) ChooseBuildLocation(string actorType, bool distanceToBaseIsImportant, Actor producer, BuildingType type)
		{
			var actorInfo = world.Map.Rules.Actors[actorType];
			var bi = actorInfo.TraitInfoOrDefault<BuildingInfo>();

			if (bi == null)
				return (null, 0);

			// Find the buildable cell that is closest to pos and centered around center
			(CPos? Location, int Variant) FindPos(CPos center, CPos target, int minRange, int maxRange)
			{
				var actorVariant = 0;
				var buildingVariantInfo = actorInfo.TraitInfoOrDefault<PlaceBuildingVariantsInfo>();
				var variantActorInfo = actorInfo;
				var vbi = bi;

				var cells = world.Map.FindTilesInAnnulus(center, minRange, maxRange);

				// Sort by distance to target if we have one
				if (center != target)
				{
					cells = cells.OrderBy(c => (c - target).LengthSquared);

					// Rotate building if we have a Facings in buildingVariantInfo.
					// If we don't have Facings in buildingVariantInfo, use a random variant
					if (buildingVariantInfo?.Actors != null)
					{
						if (buildingVariantInfo.Facings != null)
						{
							var vector = world.Map.CenterOfCell(target) - world.Map.CenterOfCell(center);

							// The rotation Y point to upside vertically, so -Y = Y(rotation)
							var desireFacing = new WAngle(WAngle.ArcSin((int)((long)Math.Abs(vector.X) * 1024 / vector.Length)).Angle);
							if (vector.X > 0 && vector.Y >= 0)
								desireFacing = new WAngle(512) - desireFacing;
							else if (vector.X < 0 && vector.Y >= 0)
								desireFacing = new WAngle(512) + desireFacing;
							else if (vector.X < 0 && vector.Y < 0)
								desireFacing = -desireFacing;

							for (int i = 0, e = 1024; i < buildingVariantInfo.Facings.Length; i++)
							{
								var minDelta = Math.Min((desireFacing - buildingVariantInfo.Facings[i]).Angle, (buildingVariantInfo.Facings[i] - desireFacing).Angle);
								if (e > minDelta)
								{
									e = minDelta;
									actorVariant = i;
								}
							}
						}
						else
							actorVariant = world.LocalRandom.Next(buildingVariantInfo.Actors.Length + 1);
					}
				}
				else
				{
					cells = cells.Shuffle(world.LocalRandom);

					if (buildingVariantInfo?.Actors != null)
						actorVariant = world.LocalRandom.Next(buildingVariantInfo.Actors.Length + 1);
				}

				if (actorVariant != 0)
				{
					variantActorInfo = world.Map.Rules.Actors[buildingVariantInfo.Actors[actorVariant - 1]];
					vbi = variantActorInfo.TraitInfoOrDefault<BuildingInfo>();
				}

				foreach (var cell in cells)
				{
					// AI need to check the building place with an additional 1 cell bounds for not enclosing units.
					if (!AIUtils.CanPlaceBuildingWithSpaceAround(world, cell, variantActorInfo, vbi, null, 1))
						continue;

					if (distanceToBaseIsImportant && !vbi.IsCloseEnoughToBase(world, player, variantActorInfo, producer, cell))
						continue;

					return (cell, actorVariant);
				}

				return (null, 0);
			}

			var baseCenter = baseBuilder.GetRandomBaseCenter();

			switch (type)
			{
				case BuildingType.Defense:

					// Build near the closest enemy structure
					var closestEnemy = world.ActorsHavingTrait<Building>()
						.Where(a => !a.Disposed && player.RelationshipWith(a.Owner) == PlayerRelationship.Enemy)
						.ClosestToIgnoringPath(world.Map.CenterOfCell(baseBuilder.DefenseCenter));

					var targetCell = closestEnemy != null ? closestEnemy.Location : baseCenter;

					return FindPos(baseBuilder.DefenseCenter, targetCell, baseBuilder.Info.MinimumDefenseRadius, baseBuilder.Info.MaximumDefenseRadius);

				case BuildingType.Refinery:

					// Don't check for resources if the mod has docks
					if (baseBuilder.Info.SupplyDockTypes.Count == 0)
					{
						// Try and place the refinery near a resource field
						if (resourceLayer != null)
						{
							var nearbyResources = world.Map.FindTilesInAnnulus(baseCenter, baseBuilder.Info.MinBaseRadius, baseBuilder.Info.MaxBaseRadius)
								.Where(a => resourceLayer.GetResource(a).Type != null)
								.Shuffle(world.LocalRandom).Take(baseBuilder.Info.MaxResourceCellsToCheck);

							foreach (var r in nearbyResources)
							{
								var found = FindPos(baseCenter, r, baseBuilder.Info.MinBaseRadius, baseBuilder.Info.MaxBaseRadius);
								if (found.Location != null)
									return found;
							}
						}
					}
					else
					{
						// Try and place the refinery near a supply dock
						var nearbyDocks = world.FindActorsInCircle(world.Map.CenterOfCell(baseCenter), WDist.FromCells(baseBuilder.Info.MaxBaseRadius))
							.Where(a => baseBuilder.Info.SupplyDockTypes.Contains(a.Info.Name))
							.Shuffle(world.LocalRandom).Take(baseBuilder.Info.MaxResourceCellsToCheck);

						foreach (var r in nearbyDocks)
						{
							var found = FindPos(baseCenter, world.Map.CellContaining(r.CenterPosition), baseBuilder.Info.MinBaseRadius, baseBuilder.Info.MaxBaseRadius);
							if (found.Location != null)
								return found;
						}
					}

					// Try and find a free spot somewhere else in the base
					return FindPos(baseCenter, baseCenter, baseBuilder.Info.MinBaseRadius, baseBuilder.Info.MaxBaseRadius);

				case BuildingType.Building:
					return FindPos(baseCenter, baseCenter, baseBuilder.Info.MinBaseRadius,
						distanceToBaseIsImportant ? baseBuilder.Info.MaxBaseRadius : world.Map.Grid.MaximumTileSearchRange);
			}

			// Can't find a build location
			return (null, 0);
		}
	}
}
