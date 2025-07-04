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

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Modifies the damage applied by this actor.")]
	public class FirepowerMultiplierInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[Desc("Percentage modifier to apply.")]
		public readonly int Modifier = 100;

		[Desc("Weapon types to applies to. Leave empty to apply to all weapons.")]
		public readonly HashSet<string> Types = new();

		public override object Create(ActorInitializer init) { return new FirepowerMultiplier(this); }
	}

	public class FirepowerMultiplier : ConditionalTrait<FirepowerMultiplierInfo>, IFirepowerModifier
	{
		public FirepowerMultiplier(FirepowerMultiplierInfo info)
			: base(info) { }

		int IFirepowerModifier.GetFirepowerModifier(string armamentName)
		{
			return !IsTraitDisabled && (Info.Types.Count == 0 || (!string.IsNullOrEmpty(armamentName) && Info.Types.Contains(armamentName))) ? Info.Modifier : 100;
		}
	}
}
