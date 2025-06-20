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
	[Desc("Where the unit should leave the building. Multiples are allowed if IDs are added: Exit@2, ...")]
	public class ExitInfo : ConditionalTraitInfo, Requires<IOccupySpaceInfo>
	{
		[Desc("Offset at which that the exiting actor is spawned relative to the center of the producing actor.")]
		public readonly WVec SpawnOffset = WVec.Zero;

		[Desc("Cell offset where the exiting actor enters the ActorMap relative to the topleft cell of the producing actor.")]
		public readonly CVec ExitCell = CVec.Zero;
		public readonly WAngle? Facing = null;

		[Desc("Type tags on this exit.")]
		public readonly HashSet<string> ProductionTypes = [];

		[Desc("Number of ticks to wait before moving into the world.")]
		public readonly int ExitDelay = 0;

		[Desc("Exits with larger priorities will be used before lower priorities.")]
		public readonly int Priority = 1;

		public override object Create(ActorInitializer init) { return new Exit(this); }
	}

	public class Exit : ConditionalTrait<ExitInfo>
	{
		public Exit(ExitInfo info)
			: base(info) { }
	}

	public static class ExitExts
	{
		public static Exit FirstExitOrDefault(this Actor actor, string productionType = null)
		{
			var all = actor.TraitsImplementing<Exit>()
				.Where(Exts.IsTraitEnabled)
				.OrderBy(e => e.Info.Priority);

			if (string.IsNullOrEmpty(productionType))
				return all.FirstOrDefault();

			return all.FirstOrDefault(e => e.Info.ProductionTypes.Count == 0 || e.Info.ProductionTypes.Contains(productionType));
		}

		public static Exit NearestExitOrDefault(this Actor actor, WPos pos, string productionType = null, Func<Exit, bool> p = null)
		{
			var all = Exits(actor, productionType)
				.OrderByDescending(e => e.Info.Priority)
				.ThenBy(e => (actor.World.Map.CenterOfCell(actor.Location + e.Info.ExitCell) - pos).LengthSquared);

#pragma warning disable RCS1077 // Optimize LINQ method call.
			return p != null ? all.FirstOrDefault(p) : all.FirstOrDefault();
#pragma warning restore RCS1077 // Optimize LINQ method call.
		}

		public static IEnumerable<Exit> Exits(this Actor actor, string productionType = null)
		{
			if (!actor.IsInWorld || actor.Disposed)
				return [];

			var all = actor.TraitsImplementing<Exit>()
				.Where(t => !t.IsTraitDisabled);

			if (string.IsNullOrEmpty(productionType))
				return all;

			return all.Where(e => e.Info.ProductionTypes.Count == 0 || e.Info.ProductionTypes.Contains(productionType));
		}

		public static Exit RandomExitOrDefault(this Actor actor, World world, string productionType, Func<Exit, bool> p = null)
		{
			if (!actor.IsInWorld || actor.Disposed)
				return null;

			var allOfType = Exits(actor, productionType);
			foreach (var g in allOfType.GroupBy(e => e.Info.Priority))
			{
				var shuffled = g.Shuffle(world.SharedRandom);
				if (p == null)
					return shuffled.First();

				var valid = shuffled.FirstOrDefault(p);
				if (valid != null)
					return valid;
			}

			return null;
		}
	}
}
