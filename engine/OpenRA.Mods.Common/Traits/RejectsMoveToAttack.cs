#region Copyright & License Information
/*
 * Copyright 2007-2022 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

namespace OpenRA.Mods.Common.Traits
{
	[Desc("When enabled actor can't move within range to attack a target.")]
	public class RejectsMoveToAttackInfo : ConditionalTraitInfo
	{
		public override object Create(ActorInitializer init) { return new RejectsMoveToAttack(this); }
	}

	public class RejectsMoveToAttack : ConditionalTrait<RejectsMoveToAttackInfo>
	{
		public RejectsMoveToAttack(RejectsMoveToAttackInfo info)
			: base(info) { }
	}
}
