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
using System.Collections.Generic;
using System.Linq;
using OpenRA.GameRules;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Warheads
{
	public class FireClusterASWarhead : WarheadAS, IRulesetLoaded<WeaponInfo>
	{
		[WeaponReference]
		[FieldLoader.Require]
		[Desc("Has to be defined in weapons.yaml as well.")]
		public readonly string Weapon = null;

		[Desc("Number of weapons fired at random 'x' cells. Negative values will result in a number equal to 'x' footprint cells fired.")]
		public readonly int RandomClusterCount = -1;

		[FieldLoader.Require]
		[Desc("Size of the cluster footprint")]
		public readonly CVec Dimensions = CVec.Zero;

		[FieldLoader.Require]
		[Desc("Cluster footprint. Cells marked as x will be attacked.")]
		public readonly string Footprint = string.Empty;

		[Desc("Should the weapons be fired around the intended target or at the explosion's epicenter.")]
		public readonly bool AroundTarget = false;

		WeaponInfo weapon;

		public void RulesetLoaded(Ruleset rules, WeaponInfo info)
		{
			if (!rules.Weapons.TryGetValue(Weapon.ToLowerInvariant(), out weapon))
				throw new YamlException($"Weapons Ruleset does not contain an entry '{Weapon.ToLowerInvariant()}'");
		}

		public override void DoImpact(in Target target, WarheadArgs args)
		{
			var firedBy = args.SourceActor;
			if (!target.IsValidFor(firedBy))
				return;

			var map = firedBy.World.Map;

			if (!IsValidImpact(target.CenterPosition, firedBy))
				return;

			var targetCell = AroundTarget && args.WeaponTarget.Type != TargetType.Invalid
				? map.CellContaining(args.WeaponTarget.CenterPosition)
				: map.CellContaining(target.CenterPosition);

			var targetCells = CellsMatching(targetCell, false);

			foreach (var c in targetCells)
				FireProjectileAtCell(map, firedBy, target, c, args);

			if (RandomClusterCount != 0)
			{
				var randomTargetCells = CellsMatching(targetCell, true);
				var clusterCount = RandomClusterCount < 0 ? randomTargetCells.Count() : RandomClusterCount;
				if (randomTargetCells.Any())
					for (var i = 0; i < clusterCount; i++)
						FireProjectileAtCell(map, firedBy, target, randomTargetCells.Random(firedBy.World.SharedRandom), args);
			}
		}

		void FireProjectileAtCell(Map map, Actor firedBy, Target target, CPos targetCell, WarheadArgs args)
		{
			var tc = Target.FromCell(firedBy.World, targetCell);

			if (!weapon.IsValidAgainst(tc, firedBy.World, firedBy))
				return;

			var projectileArgs = new ProjectileArgs
			{
				Weapon = weapon,
				Facing = (map.CenterOfCell(targetCell) - target.CenterPosition).Yaw,
				CurrentMuzzleFacing = () => (map.CenterOfCell(targetCell) - target.CenterPosition).Yaw,

				DamageModifiers = args.DamageModifiers,
				InaccuracyModifiers = Array.Empty<int>(),
				RangeModifiers = Array.Empty<int>(),

				Source = target.CenterPosition,
				CurrentSource = () => target.CenterPosition,
				SourceActor = firedBy,
				PassiveTarget = map.CenterOfCell(targetCell),
				GuidedTarget = tc
			};

			if (projectileArgs.Weapon.Projectile != null)
			{
				var projectile = projectileArgs.Weapon.Projectile.Create(projectileArgs);
				if (projectile != null)
					firedBy.World.AddFrameEndTask(w => w.Add(projectile));

				if (projectileArgs.Weapon.Report != null && projectileArgs.Weapon.Report.Length > 0)
				{
					var pos = target.CenterPosition;
					if (projectileArgs.Weapon.AudibleThroughFog || (!firedBy.World.ShroudObscures(pos) && !firedBy.World.FogObscures(pos)))
						Game.Sound.Play(SoundType.World, projectileArgs.Weapon.Report, firedBy.World, pos, null, projectileArgs.Weapon.SoundVolume);
				}
			}
		}

		IEnumerable<CPos> CellsMatching(CPos location, bool random)
		{
			var cellType = !random ? 'X' : 'x';
			var index = 0;
			var footprint = Footprint.Where(c => !char.IsWhiteSpace(c)).ToArray();
			var x = location.X - (Dimensions.X - 1) / 2;
			var y = location.Y - (Dimensions.Y - 1) / 2;
			for (var j = 0; j < Dimensions.Y; j++)
				for (var i = 0; i < Dimensions.X; i++)
					if (footprint[index++] == cellType)
						yield return new CPos(x + i, y + j);
		}
	}
}
