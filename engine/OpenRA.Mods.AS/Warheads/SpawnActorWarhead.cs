#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System;
using System.Linq;
using OpenRA.GameRules;
using OpenRA.Mods.AS.Activities;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Warheads
{
	public enum ASOwnerType { Attacker, InternalName }

	[Desc("Spawn actors upon explosion. Don't use this with buildings.")]
	public class SpawnActorWarhead : WarheadAS, IRulesetLoaded<WeaponInfo>
	{
		[Desc("The cell range to try placing the actors within.")]
		public readonly int Range = 10;

		[Desc("Actors to spawn.")]
		public readonly string[] Actors = Array.Empty<string>();

		[Desc("Should this actor link to the actor who create them? This will pass firer as the Parent Actor to spawned.")]
		public readonly bool LinkToParent = false;

		[Desc("Try to parachute the actors. When unset, actors will just fall down visually using FallRate."
			+ " Requires the Parachutable trait on all actors if set.")]
		public readonly bool Paradrop = false;

		public readonly int FallRate = 130;

		[Desc("Always spawn the actors on the ground.")]
		public readonly bool ForceGround = false;

		[Desc("Owner of the spawned actor. Allowed keywords:" +
			"'Attacker' and 'InternalName'.")]
		public readonly ASOwnerType OwnerType = ASOwnerType.Attacker;

		[Desc("Map player to use when 'InternalName' is defined on 'OwnerType'.")]
		public readonly string InternalOwner = "Neutral";

		[Desc("Defines the image of an optional animation played at the spawning location.")]
		public readonly string Image = null;

		[SequenceReference(nameof(Image), allowNullImage: true)]
		[Desc("Defines the sequence of an optional animation played at the spawning location.")]
		public readonly string Sequence = "idle";

		[PaletteReference]
		[Desc("Defines the palette of an optional animation played at the spawning location.")]
		public readonly string Palette = "effect";

		[Desc("List of sounds that can be played at the spawning location.")]
		public readonly string[] Sounds = Array.Empty<string>();

		public readonly bool UsePlayerPalette = false;

		public void RulesetLoaded(Ruleset rules, WeaponInfo info)
		{
			foreach (var a in Actors)
			{
				var actorInfo = rules.Actors[a.ToLowerInvariant()];
				var buildingInfo = actorInfo.TraitInfoOrDefault<BuildingInfo>();

				if (buildingInfo != null)
					throw new YamlException($"SpawnActorWarhead cannot be used to spawn building actor '{a}'!");
			}
		}

		public override void DoImpact(in Target target, WarheadArgs args)
		{
			var firedBy = args.SourceActor;
			if (!target.IsValidFor(firedBy))
				return;

			var map = firedBy.World.Map;
			var targetCell = map.CellContaining(target.CenterPosition);

			if (!IsValidImpact(target.CenterPosition, firedBy))
				return;

			var targetCells = map.FindTilesInCircle(targetCell, Range);
			var cell = targetCells.GetEnumerator();

			foreach (var a in Actors)
			{
				var placed = false;
				var td = new TypeDictionary();
				var ai = map.Rules.Actors[a.ToLowerInvariant()];

				if (OwnerType == ASOwnerType.Attacker)
					td.Add(new OwnerInit(firedBy.Owner));
				else
					td.Add(new OwnerInit(Array.Find(firedBy.World.Players, p => p.InternalName == InternalOwner)));

				if (LinkToParent)
					td.Add(new ParentActorInit(firedBy));

				// HACK HACK HACK
				// Immobile does not offer a check directly if the actor can exist in a position.
				// It also crashes the game if it's actor's created without a LocationInit.
				// See AS/Engine#84.
				if (ai.HasTraitInfo<ImmobileInfo>())
				{
					var immobileInfo = ai.TraitInfo<ImmobileInfo>();

					while (cell.MoveNext())
					{
						if (!immobileInfo.OccupiesSpace || !firedBy.World.ActorMap.GetActorsAt(cell.Current).Any())
						{
							td.Add(new LocationInit(cell.Current));
							var immobileunit = firedBy.World.CreateActor(false, a.ToLowerInvariant(), td);

							firedBy.World.AddFrameEndTask(w =>
							{
								w.Add(immobileunit);

								var palette = Palette;
								if (UsePlayerPalette)
									palette += immobileunit.Owner.InternalName;

								var immobilespawnpos = firedBy.World.Map.CenterOfCell(cell.Current);

								if (Image != null)
									w.Add(new SpriteEffect(immobilespawnpos, w, Image, Sequence, palette));

								var sound = Sounds.RandomOrDefault(firedBy.World.LocalRandom);
								if (sound != null)
									Game.Sound.Play(SoundType.World, sound, immobilespawnpos);
							});

							break;
						}
					}

					continue;
				}

				// Lambdas can't use 'in' variables, so capture a copy for later
				var delayedTarget = target;

				firedBy.World.AddFrameEndTask(w =>
				{
					var unit = firedBy.World.CreateActor(false, a.ToLowerInvariant(), td);
					var positionable = unit.TraitOrDefault<IPositionable>();
					cell = targetCells.GetEnumerator();

					while (cell.MoveNext() && !placed)
					{
						var subCell = positionable.GetAvailableSubCell(cell.Current);

						if (ai.HasTraitInfo<AircraftInfo>()
							&& ai.TraitInfo<AircraftInfo>().CanEnterCell(firedBy.World, unit, cell.Current))
							subCell = SubCell.FullCell;

						if (subCell != SubCell.Invalid)
						{
							positionable.SetPosition(unit, cell.Current, subCell);

							var pos = unit.CenterPosition;
							if (!ForceGround)
								pos += new WVec(WDist.Zero, WDist.Zero, firedBy.World.Map.DistanceAboveTerrain(delayedTarget.CenterPosition));

							positionable.SetCenterPosition(unit, pos);
							w.Add(unit);

							if (Paradrop)
								unit.QueueActivity(new Parachute(unit));
							else
								unit.QueueActivity(new FallDown(unit, pos, FallRate));

							var palette = Palette;
							if (UsePlayerPalette)
								palette += unit.Owner.InternalName;

							if (Image != null)
								w.Add(new SpriteEffect(pos, w, Image, Sequence, palette));

							var sound = Sounds.RandomOrDefault(firedBy.World.LocalRandom);
							if (sound != null)
								Game.Sound.Play(SoundType.World, sound, pos);

							placed = true;
						}
					}

					if (!placed)
						unit.Dispose();
				});
			}
		}
	}
}
