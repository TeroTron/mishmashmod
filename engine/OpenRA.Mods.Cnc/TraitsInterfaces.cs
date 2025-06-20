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

using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[RequireExplicitImplementation]
	public interface INotifyTeslaCharging { void Charging(Actor self, in Target target); }

	[RequireExplicitImplementation]
	public interface INotifyDisguised { void DisguiseChanged(Actor self, Actor target); }
}
