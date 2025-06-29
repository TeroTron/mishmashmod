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
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Spawn base actor at the spawnpoint and support units in an annulus around the base actor. " +
		"Both are defined at MPStartUnits. Attach this to the world actor.")]
	public class SpawnStartingUnitsInfo : TraitInfo, Requires<StartingUnitsInfo>, NotBefore<PathFinderInfo>, ILobbyOptions
	{
		public readonly string StartingUnitsClass = "none";

		[FluentReference]
		[Desc("Descriptive label for the starting units option in the lobby.")]
		public readonly string DropdownLabel = "dropdown-starting-units.label";

		[FluentReference]
		[Desc("Tooltip description for the starting units option in the lobby.")]
		public readonly string DropdownDescription = "dropdown-starting-units.description";

		[Desc("Prevent the starting units option from being changed in the lobby.")]
		public readonly bool DropdownLocked = false;

		[Desc("Whether to display the starting units option in the lobby.")]
		public readonly bool DropdownVisible = true;

		[Desc("Display order for the starting units option in the lobby.")]
		public readonly int DropdownDisplayOrder = 0;

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(MapPreview map)
		{
			var startingUnits = new Dictionary<string, string>();

			// Duplicate classes are defined for different race variants
			foreach (var t in map.WorldActorInfo.TraitInfos<StartingUnitsInfo>())
				startingUnits[t.Class] = map.GetMessage(t.ClassName);

			if (startingUnits.Count > 0)
				yield return new LobbyOption(map, "startingunits", DropdownLabel, DropdownDescription, DropdownVisible, DropdownDisplayOrder,
					startingUnits, StartingUnitsClass, DropdownLocked);
		}

		public override object Create(ActorInitializer init) { return new SpawnStartingUnits(this); }
	}

	public class SpawnStartingUnits : IWorldLoaded
	{
		readonly SpawnStartingUnitsInfo info;

		public SpawnStartingUnits(SpawnStartingUnitsInfo info)
		{
			this.info = info;
		}

		public void WorldLoaded(World world, WorldRenderer wr)
		{
			foreach (var p in world.Players)
				if (p.Playable)
					SpawnUnitsForPlayer(world, p);
		}

		void SpawnUnitsForPlayer(World w, Player p)
		{
			var spawnClass = p.PlayerReference.StartingUnitsClass ?? w.LobbyInfo.GlobalSettings
				.OptionOrDefault("startingunits", info.StartingUnitsClass);

			var unitGroup = w.Map.Rules.Actors[SystemActors.World].TraitInfos<StartingUnitsInfo>()
				.Where(g => g.Class == spawnClass && g.Factions != null && g.Factions.Contains(p.Faction.InternalName))
				.RandomOrDefault(w.SharedRandom);

			if (unitGroup == null)
				throw new InvalidOperationException($"No starting units defined for faction {p.Faction.InternalName} with class {spawnClass}");

			CPos[] homeLocations;
			if (unitGroup.BaseActor != null)
			{
				var facing = unitGroup.BaseActorFacing ?? new WAngle(w.SharedRandom.Next(1024));
				var baseActor = w.CreateActor(unitGroup.BaseActor.ToLowerInvariant(),
				[
					new LocationInit(p.HomeLocation + unitGroup.BaseActorOffset),
					new OwnerInit(p),
					new SkipMakeAnimsInit(),
					new FacingInit(facing),
					new SpawnedByMapInit(),
				]);
				var baseActorIsMovable =
					(baseActor.OccupiesSpace is Mobile mobile && !mobile.IsTraitDisabled && !mobile.IsTraitPaused && !mobile.IsImmovable) ||
					(baseActor.OccupiesSpace is Aircraft aircraft && !aircraft.IsTraitDisabled && !aircraft.IsTraitPaused && !aircraft.ForceLanding);
				if (baseActorIsMovable)
				{
					// If the base is movable, we want support actors to be able to path to its location.
					homeLocations = [baseActor.Location];
				}
				else
				{
					// For an immovable base, we want support actors to be able to path adjacent to it.
					// They won't to able to path to its location, because it is immovable and blocks them.
					var occupiedCells = baseActor.OccupiesSpace.OccupiedCells().Select(p => p.Cell).ToArray();
					homeLocations = Util.ExpandFootprint(occupiedCells, true).Except(occupiedCells).ToArray();
				}
			}
			else
			{
				// If there is no base actor, we want support actors to be able to path to the home location.
				homeLocations = [p.HomeLocation];
			}

			var buildingSpawnCells = w.Map.FindTilesInAnnulus(p.HomeLocation, unitGroup.InnerBuildingRadius + 1, unitGroup.OuterBuildingRadius);

			foreach (var b in unitGroup.SupportBuildings)
			{
				var actorRules = w.Map.Rules.Actors[b.ToLowerInvariant()];
				var building = actorRules.TraitInfo<BuildingInfo>();
				var validCells = buildingSpawnCells.Where(c => w.CanPlaceBuilding(c, actorRules, building, null));
				if (!validCells.Any())
				{
					Log.Write("debug", $"No cells available to spawn starting building {b} for player {p}");
					continue;
				}

				var cell = validCells.Random(w.SharedRandom);
				var facing = unitGroup.SupportActorsFacing ?? new WAngle(w.SharedRandom.Next(1024));

				w.CreateActor(b.ToLowerInvariant(),
				[
					new OwnerInit(p),
					new LocationInit(cell),
					new SkipMakeAnimsInit(),
					new FacingInit(facing)
				]);
			}

			var supportSpawnCells = w.Map
				.FindTilesInAnnulus(p.HomeLocation, unitGroup.InnerSupportRadius + 1, unitGroup.OuterSupportRadius)
				.ToList();

			var pathFinder = w.WorldActor.TraitOrDefault<IPathFinder>();
			var locomotorsByName = w.WorldActor.TraitsImplementing<Locomotor>().ToDictionary(l => l.Info.Name);
			foreach (var s in unitGroup.SupportActors)
			{
				var actorRules = w.Map.Rules.Actors[s.ToLowerInvariant()];
				var ip = actorRules.TraitInfo<IPositionableInfo>();
				var validCells = supportSpawnCells.Where(c => ip.CanEnterCell(w, null, c));

				if (pathFinder != null)
				{
					var locomotorName = actorRules.TraitInfoOrDefault<MobileInfo>()?.Locomotor;
					var locomotor = locomotorName != null ? locomotorsByName[locomotorName] : null;

					if (locomotor != null)
						validCells = validCells
							.Where(c => homeLocations.Any(h => pathFinder.PathMightExistForLocomotorBlockedByImmovable(locomotor, c, h)));
				}

				var validCell = validCells.RandomOrDefault(w.SharedRandom);

				if (validCell == CPos.Zero)
				{
					Log.Write("debug", $"No cells available to spawn starting unit {s} for player {p}");
					continue;
				}

				var subCell = ip.SharesCell ? w.ActorMap.FreeSubCell(validCell) : 0;
				var facing = unitGroup.SupportActorsFacing ?? new WAngle(w.SharedRandom.Next(1024));

				w.CreateActor(s.ToLowerInvariant(),
				[
					new OwnerInit(p),
					new LocationInit(validCell),
					new SubCellInit(subCell),
					new FacingInit(facing),
					new SpawnedByMapInit(),
				]);
			}

			foreach (var pa in unitGroup.SupportProxyActors)
			{
				w.CreateActor(pa.ToLowerInvariant(),
				[
					new OwnerInit(p)
				]);
			}
		}
	}
}
