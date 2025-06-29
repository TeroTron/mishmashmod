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
using OpenRA.Effects;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.GameRules
{
	public class ProjectileArgs
	{
		public WeaponInfo Weapon;
		public int[] DamageModifiers;
		public int[] InaccuracyModifiers;
		public int[] RangeModifiers;
		public WAngle Facing;
		public Func<WAngle> CurrentMuzzleFacing;
		public WPos Source;
		public Func<WPos> CurrentSource;
		public Actor SourceActor;
		public WPos PassiveTarget;
		public Target GuidedTarget;
	}

	public class WarheadArgs
	{
		public WeaponInfo Weapon;
		public int[] DamageModifiers = [];
		public WPos? Source;
		public WRot ImpactOrientation;
		public WPos ImpactPosition;
		public Actor SourceActor;
		public Target WeaponTarget;

		public WarheadArgs(ProjectileArgs args)
		{
			Weapon = args.Weapon;
			DamageModifiers = args.DamageModifiers;
			ImpactPosition = args.PassiveTarget;
			Source = args.Source;
			SourceActor = args.SourceActor;
			WeaponTarget = args.GuidedTarget;
		}

		// For places that only want to update some of the fields (usually DamageModifiers)
		public WarheadArgs(WarheadArgs args)
		{
			Weapon = args.Weapon;
			DamageModifiers = args.DamageModifiers;
			Source = args.Source;
			SourceActor = args.SourceActor;
			WeaponTarget = args.WeaponTarget;
		}

		// Default empty constructor for callers that want to initialize fields themselves
		public WarheadArgs() { }
	}

	public interface IProjectile : IEffect { }
	public interface IProjectileInfo { IProjectile Create(ProjectileArgs args); }

	public sealed class WeaponInfo
	{
		[Desc("The maximum range the weapon can fire.")]
		public readonly WDist Range = WDist.Zero;

		[Desc("First burst is aimed at this offset relative to target position.")]
		public readonly WVec FirstBurstTargetOffset = WVec.Zero;

		[Desc("Each burst after the first lands by this offset away from the previous burst.")]
		public readonly WVec FollowingBurstTargetOffset = WVec.Zero;

		[Desc("The sound played each time the weapon is fired.")]
		public readonly string[] Report = null;

		[Desc("Sound played only on first burst in a salvo.")]
		public readonly string[] StartBurstReport = null;

		[Desc("The sound played when the weapon is reloaded.")]
		public readonly string[] AfterFireSound = null;

		[Desc("Do the sounds play under shroud or fog.")]
		public readonly bool AudibleThroughFog = false;

		[Desc("Volume the report sounds played at.")]
		public readonly float SoundVolume = 1f;

		[Desc("Delay in ticks to play reloading sound.")]
		public readonly int AfterFireSoundDelay = 0;

		[Desc("Delay in ticks between reloading ammo magazines.")]
		public readonly int ReloadDelay = 1;

		[Desc("Number of shots in a single ammo magazine.")]
		public readonly int Burst = 1;

		[Desc("Can this weapon target the attacker itself?")]
		public readonly bool CanTargetSelf = false;

		[Desc("What player relationships are affected.")]
		public readonly PlayerRelationship ValidRelationships = PlayerRelationship.Ally | PlayerRelationship.Neutral | PlayerRelationship.Enemy;

		[Desc("What types of targets are affected.")]
		public readonly BitSet<TargetableType> ValidTargets = new("Ground", "Water");

		[Desc("What types of targets are unaffected.", "Overrules ValidTargets.")]
		public readonly BitSet<TargetableType> InvalidTargets;

		static readonly BitSet<TargetableType> TargetTypeAir = new("Air");

		[Desc("If weapon is not directly targeting an actor and targeted position is above this altitude,",
			"the weapon will ignore terrain target types and only check TargetTypeAir for validity.")]
		public readonly WDist AirThreshold = new(128);

		[Desc("Delay in ticks between firing shots from the same ammo magazine. If one entry, it will be used for all bursts.",
			"If multiple entries, their number needs to match Burst - 1.")]
		public readonly int[] BurstDelays = [5];

		[Desc("The minimum range the weapon can fire.")]
		public readonly WDist MinRange = WDist.Zero;

		[Desc("Does this weapon aim at the target's center regardless of other targetable offsets?")]
		public readonly bool TargetActorCenter = false;

		[FieldLoader.LoadUsing(nameof(LoadProjectile))]
		public readonly IProjectileInfo Projectile;

		[FieldLoader.LoadUsing(nameof(LoadWarheads))]
		public readonly List<IWarhead> Warheads = [];

		/// <summary>
		/// This constructor is used solely for documentation generation.
		/// </summary>
		public WeaponInfo() { }

		public WeaponInfo(MiniYaml content)
		{
			// Resolve any weapon-level yaml inheritance or removals
			// HACK: The "Defaults" sequence syntax prevents us from doing this generally during yaml parsing
			content = content.WithNodes(MiniYaml.Merge([content.Nodes]));
			FieldLoader.Load(this, content);
		}

		static object LoadProjectile(MiniYaml yaml)
		{
			var proj = yaml.NodeWithKeyOrDefault("Projectile")?.Value;
			if (proj == null)
				return null;

			var ret = Game.CreateObject<IProjectileInfo>(proj.Value + "Info");
			if (ret == null)
				return null;

			FieldLoader.Load(ret, proj);
			return ret;
		}

		static object LoadWarheads(MiniYaml yaml)
		{
			var retList = new List<IWarhead>();
			foreach (var node in yaml.Nodes.Where(n => n.Key.StartsWith("Warhead", StringComparison.Ordinal)))
			{
				var ret = Game.CreateObject<IWarhead>(node.Value.Value + "Warhead");
				if (ret == null)
					continue;

				FieldLoader.Load(ret, node.Value);
				retList.Add(ret);
			}

			return retList;
		}

		public bool IsValidTarget(BitSet<TargetableType> targetTypes)
		{
			return ValidTargets.Overlaps(targetTypes) && !InvalidTargets.Overlaps(targetTypes);
		}

		/// <summary>Checks if the weapon is valid against (can target) the target.</summary>
		public bool IsValidAgainst(in Target target, World world, Actor firedBy)
		{
			if (target.Type == TargetType.Actor)
				return IsValidAgainst(target.Actor, firedBy);

			if (target.Type == TargetType.FrozenActor)
				return IsValidAgainst(target.FrozenActor, firedBy);

			if (target.Type == TargetType.Terrain)
			{
				var dat = world.Map.DistanceAboveTerrain(target.CenterPosition);
				if (dat > AirThreshold)
					return IsValidTarget(TargetTypeAir);

				var cell = world.Map.CellContaining(target.CenterPosition);
				if (!world.Map.Contains(cell))
					return false;

				var cellInfo = world.Map.GetTerrainInfo(cell);
				if (!IsValidTarget(cellInfo.TargetTypes))
					return false;

				return true;
			}

			return false;
		}

		/// <summary>Checks if the weapon is valid against (can target) the actor.</summary>
		public bool IsValidAgainst(Actor victim, Actor firedBy)
		{
			if (!CanTargetSelf && victim == firedBy)
				return false;

			var relationship = firedBy.Owner.RelationshipWith(victim.Owner);
			if (!ValidRelationships.HasRelationship(relationship))
				return false;

			var targetTypes = victim.GetEnabledTargetTypes();
			return IsValidTarget(targetTypes);
		}

		/// <summary>Checks if the weapon is valid against (can target) the frozen actor.</summary>
		public bool IsValidAgainst(FrozenActor victim, Actor firedBy)
		{
			if (!victim.IsValid)
				return false;

			if (!CanTargetSelf && victim.Actor == firedBy)
				return false;

			var relationship = firedBy.Owner.RelationshipWith(victim.Owner);
			if (!ValidRelationships.HasRelationship(relationship))
				return false;

			return IsValidTarget(victim.TargetTypes);
		}

		/// <summary>Applies all the weapon's warheads to the target.</summary>
		public void Impact(in Target target, WarheadArgs args)
		{
			var world = args.SourceActor.World;
			foreach (var warhead in Warheads)
			{
				if (warhead.Delay > 0)
				{
					// Lambdas can't use 'in' variables, so capture a copy for later
					var delayedTarget = target;
					world.AddFrameEndTask(w => w.Add(new DelayedImpact(warhead.Delay, warhead, delayedTarget, args)));
				}
				else
					warhead.DoImpact(target, args);
			}
		}

		/// <summary>Applies all the weapon's warheads to the target. Only use for projectile-less, special-case impacts.</summary>
		public void Impact(in Target target, Actor firedBy)
		{
			// The impact will happen immediately at target.CenterPosition.
			var args = new WarheadArgs
			{
				Weapon = this,
				SourceActor = firedBy,
				WeaponTarget = target
			};

			if (firedBy.OccupiesSpace != null)
				args.Source = firedBy.CenterPosition;

			Impact(target, args);
		}
	}
}
