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
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Server;

namespace OpenRA.Mods.Common.Lint
{
	sealed class CheckTooltips : ILintRulesPass, ILintServerMapPass
	{
		void ILintRulesPass.Run(Action<string> emitError, Action<string> emitWarning, ModData modData, Ruleset rules)
		{
			Run(emitError, rules);
		}

		void ILintServerMapPass.Run(Action<string> emitError, Action<string> emitWarning, ModData modData, MapPreview map, Ruleset mapRules)
		{
			Run(emitError, mapRules);
		}

		static void Run(Action<string> emitError, Ruleset rules)
		{
			foreach (var actorInfo in rules.Actors)
			{
				// Catch TypeDictionary errors.
				try
				{
					var buildable = actorInfo.Value.TraitInfos<BuildableInfo>().ToArray();
					if (buildable.Length == 0)
						continue;

					var tooltip = actorInfo.Value.TraitInfos<TooltipInfo>().FirstOrDefault(info => info.EnabledByDefault);
					if (tooltip == null)
						emitError($"The following buildable actor has no (enabled) Tooltip: `{actorInfo.Key}`.");
				}
				catch (InvalidOperationException e)
				{
					emitError($"{e.Message} (Actor type `{actorInfo.Key}`).");
				}
			}
		}
	}
}
